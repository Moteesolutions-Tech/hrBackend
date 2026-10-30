using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Contracts;
using Motee.Application.Countries;
using Motee.Application.Documents;
using Motee.Application.Tenancy;
using Motee.Domain.Common;
using Motee.Domain.Contracts;
using Motee.Domain.Documents;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Contracts;

internal sealed class ContractService(
    MoteeDbContext dbContext,
    IDocumentService documents,
    ICountryProfileProvider countries,
    ICurrentEmployee currentEmployee,
    ICurrentTenant currentTenant,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IContractService
{
    public async Task<PagedResult<ContractDto>> ListAsync(
        ContractQuery query,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = Today();

        IQueryable<Contract> source = dbContext.Contracts.AsNoTracking();

        if (!query.IncludeArchived)
        {
            source = source.Where(contract => !contract.IsArchived);
        }

        if (query.EmployeeId is Guid employeeId)
        {
            source = source.Where(contract => contract.EmployeeId == employeeId);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            source = source.Where(contract => dbContext.Employees
                .Any(employee => employee.Id == contract.EmployeeId
                    && employee.DepartmentId == departmentId));
        }

        if (query.Status is ContractStatus status)
        {
            source = source.Where(contract => contract.Status == status);
        }

        if (query.ContractType is ContractType type)
        {
            source = source.Where(contract => contract.ContractType == type);
        }

        if (query.ExpiringSoonOnly == true)
        {
            // Narrowed in SQL to the ones that could possibly qualify — ending,
            // not renewing, not terminated or draft — then judged in memory,
            // because the window depends on each contract's own notice period.
            source = source.Where(contract => contract.EndDate != null
                && !contract.AutoRenew
                && contract.EndDate >= today
                && contract.Status != ContractStatus.Terminated
                && contract.Status != ContractStatus.Draft);
        }

        List<Contract> all = await source
            .OrderByDescending(contract => contract.CreatedAt)
            .ToListAsync(cancellationToken);

        if (query.ExpiringSoonOnly == true)
        {
            all = [.. all.Where(contract => Domain.Contracts.Contracts.IsExpiringSoon(contract, today))];
        }

        return new PagedResult<ContractDto>
        {
            Items = await DescribeAsync(
                [.. all.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)],
                cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = all.Count,
        };
    }

    public async Task<ContractDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Contract? contract = await dbContext.Contracts
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return contract is null ? null : (await DescribeAsync([contract], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<ContractDto>> MineAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return [];
        }

        List<Contract> mine = await dbContext.Contracts
            .AsNoTracking()
            .Where(contract => contract.EmployeeId == employeeId && !contract.IsArchived)
            .OrderByDescending(contract => contract.StartDate)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(mine, cancellationToken);
    }

    public async Task<ContractResult> CreateAsync(
        ContractRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Validate(request) is ContractOutcome problem)
        {
            return ContractResult.Failed(problem);
        }

        if (!await dbContext.Employees.AnyAsync(
                employee => employee.Id == request.EmployeeId, cancellationToken))
        {
            return ContractResult.Failed(ContractOutcome.EmployeeNotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        Contract contract = new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = request.EmployeeId,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            ContractType = request.ContractType,
            Status = ContractStatus.Draft,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            AutoRenew = request.AutoRenew,
            NoticePeriodDays = request.NoticePeriodDays,
            Salary = request.Salary,
            Currency = await CurrencyAsync(request.Currency, cancellationToken),
            CreatedByUserId = UserId(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.Contracts.Add(contract);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ContractResult.Ok((await DescribeAsync([contract], cancellationToken))[0]);
    }

    public async Task<ContractResult> UpdateAsync(
        Guid id,
        ContractRequest request,
        CancellationToken cancellationToken = default)
    {
        Contract? contract = await dbContext.Contracts
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (contract is null)
        {
            return ContractResult.Failed(ContractOutcome.NotFound);
        }

        // A signature is against what the contract said at the moment it was
        // given. Changing the terms afterwards is forgery by another name, so the
        // service refuses rather than trusting that no screen offers the button.
        if (await dbContext.ContractSignatures.AnyAsync(
                signature => signature.ContractId == id, cancellationToken))
        {
            return ContractResult.Failed(ContractOutcome.SignedContractIsFixed);
        }

        if (Validate(request) is ContractOutcome problem)
        {
            return ContractResult.Failed(problem);
        }

        contract.Title = request.Title.Trim();
        contract.Description = request.Description?.Trim();
        contract.ContractType = request.ContractType;
        contract.StartDate = request.StartDate;
        contract.EndDate = request.EndDate;
        contract.AutoRenew = request.AutoRenew;
        contract.NoticePeriodDays = request.NoticePeriodDays;
        contract.Salary = request.Salary;
        contract.Currency = await CurrencyAsync(request.Currency, cancellationToken);
        contract.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ContractResult.Ok((await DescribeAsync([contract], cancellationToken))[0]);
    }

    public Task<ContractResult> SendForSignatureAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        MoveAsync(id, ContractAction.SendForSignature, ContractStatus.PendingSignature,
            null, cancellationToken);

    public Task<ContractResult> ActivateAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        MoveAsync(id, ContractAction.Activate, ContractStatus.Active, null, cancellationToken);

    public async Task<ContractResult> TerminateAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(reason)
            ? ContractResult.Failed(ContractOutcome.ReasonRequired)
            : await MoveAsync(
                id, ContractAction.Terminate, ContractStatus.Terminated, reason.Trim(),
                cancellationToken);

    private async Task<ContractResult> MoveAsync(
        Guid id,
        ContractAction action,
        ContractStatus to,
        string? note,
        CancellationToken cancellationToken)
    {
        Contract? contract = await dbContext.Contracts
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (contract is null)
        {
            return ContractResult.Failed(ContractOutcome.NotFound);
        }

        if (!ContractLifecycle.Can(contract.Status, action))
        {
            return ContractResult.Failed(
                ContractOutcome.NotAllowed, ContractLifecycle.Refusal(contract.Status, action));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        contract.Status = to;
        contract.UpdatedAt = now;

        // The reason lands as a note rather than its own column. A termination
        // reason and a note about one are the same kind of thing to whoever reads
        // the contract later, and one timeline beats two.
        if (note is not null)
        {
            dbContext.ContractNotes.Add(new ContractNote
            {
                Id = Guid.NewGuid(),
                ContractId = id,
                Content = note,
                CreatedByUserId = UserId(),
                CreatedAt = now,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ContractResult.Ok((await DescribeAsync([contract], cancellationToken))[0]);
    }

    public async Task<ContractOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Contract? contract = await dbContext.Contracts
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (contract is null)
        {
            return ContractOutcome.NotFound;
        }

        // A draft nobody has seen is deletable. Anything signed is a record, and a
        // record somebody can erase is not one.
        if (await dbContext.ContractSignatures.AnyAsync(
                signature => signature.ContractId == id, cancellationToken))
        {
            return ContractOutcome.NotAllowed;
        }

        if (contract.Status != ContractStatus.Draft)
        {
            return ContractOutcome.NotAllowed;
        }

        dbContext.Contracts.Remove(contract);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ContractOutcome.Succeeded;
    }

    public async Task<ContractResult> AddNoteAsync(
        Guid id,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return ContractResult.Failed(ContractOutcome.ContentRequired);
        }

        Contract? contract = await dbContext.Contracts
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (contract is null)
        {
            return ContractResult.Failed(ContractOutcome.NotFound);
        }

        dbContext.ContractNotes.Add(new ContractNote
        {
            Id = Guid.NewGuid(),
            ContractId = id,
            Content = content.Trim(),
            CreatedByUserId = UserId(),
            CreatedAt = timeProvider.GetUtcNow(),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return ContractResult.Ok((await DescribeAsync([contract], cancellationToken))[0]);
    }

    public async Task<ContractResult> FileToDocumentsAsync(
        Guid id,
        Guid storedFileId,
        CancellationToken cancellationToken = default)
    {
        Contract? contract = await dbContext.Contracts
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (contract is null)
        {
            return ContractResult.Failed(ContractOutcome.NotFound);
        }

        // Twice would leave the employee with two copies and nothing to say which
        // is current.
        if (contract.DocumentId is not null)
        {
            return ContractResult.Failed(ContractOutcome.AlreadyFiled);
        }

        // There is no agreed copy of a draft, so filing one files nothing.
        if (contract.Status == ContractStatus.Draft)
        {
            return ContractResult.Failed(ContractOutcome.NotReadyToFile);
        }

        if (!await dbContext.StoredFiles.AnyAsync(
                file => file.Id == storedFileId, cancellationToken))
        {
            return ContractResult.Failed(ContractOutcome.FileNotFound);
        }

        DocumentResult filed = await documents.CreateAsync(
            new CreateDocumentRequest
            {
                Name = contract.Title,
                Category = DocumentCategory.Contract,
                FolderId = await ContractsFolderAsync(cancellationToken),
                StoredFileId = storedFileId,
                Description = contract.Description,

                // Named people, not the whole company. A contract is between two
                // parties and filing it anywhere else publishes somebody's salary.
                Assignment = AssignmentScope.Specific,
            },
            cancellationToken);

        if (!filed.Succeeded)
        {
            return ContractResult.Failed(ContractOutcome.FileNotFound);
        }

        // Shared with the person it is about, with permission to keep a copy. It
        // is their contract — view-only would be the company holding somebody's
        // own agreement just out of reach.
        await documents.ShareAsync(
            filed.Document!.Id,
            new ShareDocumentRequest
            {
                EmployeeId = contract.EmployeeId,
                Permission = DocumentPermission.Download,
            },
            cancellationToken);

        contract.DocumentId = filed.Document.Id;
        contract.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ContractResult.Ok((await DescribeAsync([contract], cancellationToken))[0]);
    }

    // The folder contracts are filed into, created on first use.
    //
    // A System folder, so it cannot be deleted — screens reach it by purpose, and
    // a company that deleted it would find contracts filing into nothing.
    private async Task<Guid> ContractsFolderAsync(CancellationToken cancellationToken)
    {
        const string name = "Contracts";

        Guid existing = await dbContext.DocumentFolders
            .Where(folder => folder.Name == name && folder.ParentId == null)
            .Select(folder => folder.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != Guid.Empty)
        {
            return existing;
        }

        DocumentFolder folder = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Type = FolderType.System,
            CreatedByUserId = UserId(),
            CreatedAt = timeProvider.GetUtcNow(),
        };

        dbContext.DocumentFolders.Add(folder);
        await dbContext.SaveChangesAsync(cancellationToken);

        return folder.Id;
    }

    public Task<ContractResult> SignAsync(
        Guid id,
        SignContractRequest request,
        CancellationToken cancellationToken = default) =>
        SignInternalAsync(id, request, asEmployee: false, cancellationToken);

    public Task<ContractResult> SignMineAsync(
        Guid id,
        SignContractRequest request,
        CancellationToken cancellationToken = default) =>
        SignInternalAsync(id, request, asEmployee: true, cancellationToken);

    private async Task<ContractResult> SignInternalAsync(
        Guid id,
        SignContractRequest request,
        bool asEmployee,
        CancellationToken cancellationToken)
    {
        Guid? me = await currentEmployee.IdAsync(cancellationToken);

        IQueryable<Contract> source = dbContext.Contracts;

        // Scoped to the signer when they are signing their own. A valid id is not
        // permission to sign somebody else's contract as them.
        if (asEmployee)
        {
            if (me is not Guid employeeId)
            {
                return ContractResult.Failed(ContractOutcome.EmployeeNotFound);
            }

            source = source.Where(contract => contract.EmployeeId == employeeId);
        }

        Contract? found = await source
            .FirstOrDefaultAsync(contract => contract.Id == id, cancellationToken);

        if (found is null)
        {
            return ContractResult.Failed(ContractOutcome.NotFound);
        }

        Guid? userId = UserId();

        // One signature per party. Twice would move the date and make a late one
        // look prompt.
        bool already = await dbContext.ContractSignatures.AnyAsync(
            signature => signature.ContractId == id
                && ((asEmployee && signature.EmployeeId == me)
                    || (!asEmployee && signature.UserId == userId && signature.EmployeeId == null)),
            cancellationToken);

        if (already)
        {
            return ContractResult.Failed(ContractOutcome.AlreadySigned);
        }

        dbContext.ContractSignatures.Add(new ContractSignature
        {
            Id = Guid.NewGuid(),
            ContractId = id,

            // Set for the employee's own signature and null for the company's, so
            // the two are distinguishable without reading the label somebody typed.
            EmployeeId = asEmployee ? me : null,
            UserId = userId,
            SignedAs = request.SignedAs.Trim(),
            SignatureFileId = request.SignatureFileId,
            SignedAt = timeProvider.GetUtcNow(),
        });

        found.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ContractResult.Ok((await DescribeAsync([found], cancellationToken))[0]);
    }

    private static ContractOutcome? Validate(ContractRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return ContractOutcome.TitleRequired;
        }

        if (request.EndDate is DateOnly end && end < request.StartDate)
        {
            return ContractOutcome.EndBeforeStart;
        }

        if (request.Salary is < 0m || request.NoticePeriodDays < 0)
        {
            return ContractOutcome.AmountNotPositive;
        }

        return request.Currency is { Length: > 0 } currency
            && (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
                ? ContractOutcome.CurrencyInvalid
                : null;
    }

    private async Task<string> CurrencyAsync(string? requested, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim().ToUpperInvariant();
        }

        if (currentTenant.TenantId is not Guid tenantId)
        {
            return countries.Get(CountryCode.Nigeria).CurrencyCode;
        }

        CountryCode country = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.CountryCode)
            .FirstOrDefaultAsync(cancellationToken);

        return countries.Get(country).CurrencyCode;
    }

    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private async Task<IReadOnlyList<ContractDto>> DescribeAsync(
        IReadOnlyList<Contract> contracts,
        CancellationToken cancellationToken)
    {
        if (contracts.Count == 0)
        {
            return [];
        }

        DateOnly today = Today();
        List<Guid> ids = [.. contracts.Select(contract => contract.Id)];

        List<ContractSignature> signatures = await dbContext.ContractSignatures
            .AsNoTracking()
            .Where(signature => ids.Contains(signature.ContractId))
            .OrderBy(signature => signature.SignedAt)
            .ToListAsync(cancellationToken);

        List<ContractNote> notes = await dbContext.ContractNotes
            .AsNoTracking()
            .Where(note => ids.Contains(note.ContractId))
            .OrderByDescending(note => note.CreatedAt)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, (string Name, string? Department)> people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => contracts.Select(contract => contract.EmployeeId)
                .Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                Department = dbContext.Departments
                    .Where(department => department.Id == employee.DepartmentId)
                    .Select(department => department.Name)
                    .FirstOrDefault(),
            })
            .ToDictionaryAsync(row => row.Id, row => (row.Name, row.Department), cancellationToken);

        List<Guid> userIds =
        [
            .. signatures.Where(signature => signature.UserId is not null)
                .Select(signature => signature.UserId!.Value)
                .Concat(notes.Where(note => note.CreatedByUserId is not null)
                    .Select(note => note.CreatedByUserId!.Value))
                .Distinct(),
        ];

        Dictionary<Guid, string> userNames = userIds.Count == 0
            ? []
            : await dbContext.Users
                .AsNoTracking()
                .Where(user => userIds.Contains(user.Id))
                .ToDictionaryAsync(
                    user => user.Id,
                    user => user.FirstName + " " + user.LastName,
                    cancellationToken);

        return
        [
            .. contracts.Select(contract => new ContractDto
            {
                Id = contract.Id,
                EmployeeId = contract.EmployeeId,
                EmployeeName = people.TryGetValue(contract.EmployeeId, out (string Name, string? Department) person)
                    ? person.Name
                    : "Unknown",
                DepartmentName = people.GetValueOrDefault(contract.EmployeeId).Department,
                Title = contract.Title,
                Description = contract.Description,
                ContractType = contract.ContractType,
                Status = contract.Status,
                IsExpired = Domain.Contracts.Contracts.IsExpired(contract, today),
                IsExpiringSoon = Domain.Contracts.Contracts.IsExpiringSoon(contract, today),
                StartDate = contract.StartDate,
                EndDate = contract.EndDate,
                AutoRenew = contract.AutoRenew,
                NoticePeriodDays = contract.NoticePeriodDays,
                Salary = contract.Salary,
                Currency = contract.Currency,
                SignatureStatus = Domain.Contracts.Contracts.SignatureStatusOf(
                    signatures.Count(signature => signature.ContractId == contract.Id)),
                Signatures =
                [
                    .. signatures
                        .Where(signature => signature.ContractId == contract.Id)
                        .Select(signature => new ContractSignatureDto
                        {
                            EmployeeId = signature.EmployeeId,
                            SignedAs = signature.SignedAs,
                            SignedByName = signature.UserId is Guid signer
                                ? userNames.GetValueOrDefault(signer) ?? "Unknown"
                                : "Unknown",
                            SignatureFileId = signature.SignatureFileId,
                            SignedAt = signature.SignedAt,
                        }),
                ],
                Notes =
                [
                    .. notes
                        .Where(note => note.ContractId == contract.Id)
                        .Select(note => new ContractNoteDto
                        {
                            Id = note.Id,
                            Content = note.Content,
                            CreatedByName = note.CreatedByUserId is Guid author
                                ? userNames.GetValueOrDefault(author)
                                : null,
                            CreatedAt = note.CreatedAt,
                        }),
                ],
                IsArchived = contract.IsArchived,
                DocumentId = contract.DocumentId,
                CreatedAt = contract.CreatedAt,
            }),
        ];
    }
}
