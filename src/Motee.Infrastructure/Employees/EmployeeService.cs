using Microsoft.EntityFrameworkCore;
using Motee.Application.Assets;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Employees;

internal sealed class EmployeeService(
    MoteeDbContext dbContext,
    IAssetService assets,
    AvatarLinker avatars,
    TimeProvider timeProvider) : IEmployeeService
{
    public async Task<PagedResult<EmployeeListItemDto>> ListAsync(
        EmployeeQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Employee> matching = Filter(query);

        int total = await matching.CountAsync(cancellationToken);

        List<EmployeeListItemDto> items = await ProjectList(matching)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        items = await WithAvatarsAsync(items, cancellationToken);

        return new PagedResult<EmployeeListItemDto>
        {
            Items = items,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<IReadOnlyList<EmployeeListItemDto>> ExportAsync(
        EmployeeQuery query,
        CancellationToken cancellationToken = default) =>
        await ProjectList(Filter(query)).ToListAsync(cancellationToken);

    public async Task<EmployeeStatsDto> StatsAsync(
        EmployeeQuery query,
        CancellationToken cancellationToken = default)
    {
        // Grouped in the database. Counting each status with its own query would be
        // one round trip per tab, and the totals could disagree if a record moved
        // between them mid-flight.
        Dictionary<EmployeeStatus, int> counted = await Matching(query)
            .GroupBy(employee => employee.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancellationToken);

        return new EmployeeStatsDto
        {
            Headcount = counted
                .Where(entry => entry.Key != EmployeeStatus.Deleted)
                .Sum(entry => entry.Value),
            ByStatus =
            [
                .. Enum.GetValues<EmployeeStatus>().Select(status => new EmployeeStatusCount
                {
                    Status = status,
                    Count = counted.GetValueOrDefault(status),
                }),
            ],
        };
    }

    // Shared by the list and the export so the two can never disagree about which
    // rows the user is looking at.
    private IQueryable<Employee> Filter(EmployeeQuery query)
    {
        IQueryable<Employee> employees = Matching(query);

        if (query.Status is EmployeeStatus status)
        {
            return employees.Where(employee => employee.Status == status);
        }

        // Deleted rows live behind their own tab, so they stay out of the default
        // list and out of headcount.
        return employees.Where(employee => employee.Status != EmployeeStatus.Deleted);
    }

    // Everything except the status: the list defaults it, the stats count it.
    private IQueryable<Employee> Matching(EmployeeQuery query)
    {
        IQueryable<Employee> employees = Narrow(dbContext.Employees.AsNoTracking(), query);

        if (query.DepartmentId is Guid departmentId)
        {
            employees = employees.Where(employee => employee.DepartmentId == departmentId);
        }

        if (query.EmploymentType is EmploymentType employmentType)
        {
            employees = employees.Where(employee => employee.EmploymentType == employmentType);
        }

        if (query.WorkMode is WorkMode workMode)
        {
            employees = employees.Where(employee => employee.WorkMode == workMode);
        }

        // Someone with no start date recorded is outside any range, rather than
        // silently appearing in every one.
        if (query.StartedFrom is DateOnly from)
        {
            employees = employees.Where(employee =>
                employee.StartDate != null && employee.StartDate >= from);
        }

        if (query.StartedTo is DateOnly to)
        {
            employees = employees.Where(employee =>
                employee.StartDate != null && employee.StartDate <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{EscapeLike(query.Search.Trim())}%";

            // Department is included because the search box offers it. It is a
            // separate table, so this becomes an EXISTS rather than a join — the
            // alternative is duplicating the name onto the employee row.
            employees = employees.Where(employee =>
                EF.Functions.ILike(employee.FirstName, pattern, @"\")
                || EF.Functions.ILike(employee.LastName, pattern, @"\")
                || EF.Functions.ILike(employee.Email, pattern, @"\")
                || dbContext.Departments.Any(department =>
                    department.Id == employee.DepartmentId
                    && EF.Functions.ILike(department.Name, pattern, @"\")));
        }

        return employees;
    }

    private IQueryable<EmployeeListItemDto> ProjectList(IQueryable<Employee> employees) =>
        employees
            .OrderBy(employee => employee.FirstName)
            .ThenBy(employee => employee.LastName)
            .Select(employee => new EmployeeListItemDto
            {
                Id = employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                Initials = employee.FirstName.Substring(0, 1) + employee.LastName.Substring(0, 1),
                AvatarFileId = employee.AvatarFileId,
                Email = employee.Email,
                Phone = employee.Phone,
                JobTitle = employee.JobTitle,
                DepartmentId = employee.DepartmentId,
                Department = dbContext.Departments
                    .Where(department => department.Id == employee.DepartmentId)
                    .Select(department => department.Name)
                    .FirstOrDefault(),
                EmploymentType = employee.EmploymentType,
                WorkMode = employee.WorkMode,
                WorkLocation = employee.WorkLocation,
                Status = employee.Status,
                ManagerId = employee.ManagerId,
                ManagerName = dbContext.Employees
                    .Where(manager => manager.Id == employee.ManagerId)
                    .Select(manager => manager.FirstName + " " + manager.LastName)
                    .FirstOrDefault(),
                DirectReportCount = dbContext.Employees.Count(report => report.ManagerId == employee.Id),
                StartDate = employee.StartDate,
                EmployeeNumber = employee.EmployeeNumber,
            });

    public async Task<EmployeeDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EmployeeDto? employee = await Project(
                dbContext.Employees.AsNoTracking().Where(candidate => candidate.Id == id))
            .FirstOrDefaultAsync(cancellationToken);

        if (employee?.AvatarFileId is not Guid fileId)
        {
            return employee;
        }

        IReadOnlyDictionary<Guid, string> urls =
            await avatars.UrlsForAsync([fileId], cancellationToken);

        return urls.TryGetValue(fileId, out string? url) ? employee with { AvatarUrl = url } : employee;
    }

    private async Task<List<EmployeeListItemDto>> WithAvatarsAsync(
        List<EmployeeListItemDto> items,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, string> urls = await avatars.UrlsForAsync(
            items.Select(item => item.AvatarFileId), cancellationToken);

        if (urls.Count == 0)
        {
            return items;
        }

        return
        [
            .. items.Select(item => item.AvatarFileId is Guid id && urls.TryGetValue(id, out string? url)
                ? item with { AvatarUrl = url }
                : item),
        ];
    }

    public async Task<EmployeeResult> CreateAsync(
        EmployeeRequest request,
        OnboardingMethod via = OnboardingMethod.Manual,
        CancellationToken cancellationToken = default)
    {
        EmployeeOutcome? problem = await ValidateAsync(request, null, cancellationToken);

        if (problem is not null)
        {
            return EmployeeResult.Failed(problem.Value);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        Employee employee = new()
        {
            Id = Guid.NewGuid(),
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Email = request.Email.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        Apply(employee, request);
        employee.OnboardingMethod = via;

        dbContext.Employees.Add(employee);

        // Added before the save, so the wizard's later steps land in the same
        // transaction as the person they describe. A record created without its bank
        // details because step 3 failed is worse than a rejected form.
        await ApplyBlocksAsync(employee, request, now, cancellationToken);

        // Kit is created in the assets module, staged into this same transaction so
        // the person and what they were handed commit together.
        if (request.Assets is { Count: > 0 } kit
            && await assets.StageForEmployeeAsync(employee.Id, kit, cancellationToken)
                is AssetOutcome assetProblem)
        {
            return EmployeeResult.Failed(assetProblem == AssetOutcome.DuplicateTag
                ? EmployeeOutcome.DuplicateAssetTag
                : EmployeeOutcome.NotFound);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await LinkExistingUserAsync(employee, cancellationToken);

        return EmployeeResult.Ok((await GetAsync(employee.Id, cancellationToken))!);
    }

    // Covers the one person who signs up before any employee exists: the admin who
    // registered the tenant. When they later create their own record, the account is
    // already there, so it is linked here rather than left without an employee — and
    // without it their Team and Self scope stay empty.
    //
    // The other direction is the invite flow, which creates the account from the
    // employee and links it at that point.
    private async Task LinkExistingUserAsync(Employee employee, CancellationToken cancellationToken)
    {
        string email = employee.Email;

        // Users are not tenant-filtered — login has to find one before any tenant is
        // known — so the tenant is matched explicitly. An address that happens to
        // belong to a user at another company must not link.
        ApplicationUser? user = await dbContext.Users
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == employee.TenantId
                    && candidate.EmployeeId == null
                    && candidate.NormalizedEmail == email.ToUpperInvariant(),
                cancellationToken);

        if (user is null)
        {
            return;
        }

        user.EmployeeId = employee.Id;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<EmployeeResult> UpdateAsync(
        Guid id,
        EmployeeRequest request,
        CancellationToken cancellationToken = default)
    {
        Employee? employee = await dbContext.Employees
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (employee is null)
        {
            return EmployeeResult.Failed(EmployeeOutcome.NotFound);
        }

        EmployeeOutcome? problem = await ValidateAsync(request, id, cancellationToken);

        if (problem is not null)
        {
            return EmployeeResult.Failed(problem.Value);
        }

        // Saving the form must not be a back door around the lifecycle rules.
        if (!EmployeeLifecycle.CanMove(employee.Status, request.Status))
        {
            return EmployeeResult.Failed(EmployeeOutcome.InvalidStatusChange);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        Apply(employee, request);
        employee.UpdatedAt = now;

        await ApplyBlocksAsync(employee, request, now, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return EmployeeResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<EmployeeResult> ChangeStatusAsync(
        Guid id,
        EmployeeStatus status,
        CancellationToken cancellationToken = default)
    {
        Employee? employee = await dbContext.Employees
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (employee is null)
        {
            return EmployeeResult.Failed(EmployeeOutcome.NotFound);
        }

        if (!EmployeeLifecycle.CanMove(employee.Status, status))
        {
            return EmployeeResult.Failed(EmployeeOutcome.InvalidStatusChange);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Leaving is the point at which the date is known, so it is recorded here
        // rather than left for someone to fill in later.
        if (status == EmployeeStatus.Inactive && employee.DateOfLeaving is null)
        {
            employee.DateOfLeaving = DateOnly.FromDateTime(now.UtcDateTime);
        }

        employee.Status = status;
        employee.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return EmployeeResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    // A Line Manager who can edit employees must not thereby be able to edit the
    // whole company. Scope decides which rows they reach at all.
    private IQueryable<Employee> Narrow(IQueryable<Employee> employees, EmployeeQuery query) =>
        query.Scope.Kind switch
        {
            DataScopeKind.All => employees,

            // The departments the access level names, not the one the viewer happens
            // to be in. Moving someone between departments must not silently change
            // what they can see.
            DataScopeKind.Department => employees.Where(employee =>
                employee.DepartmentId != null
                && query.Scope.DepartmentIds.Contains(employee.DepartmentId.Value)),

            // An employee belongs to a department, and the department to a unit, so
            // this reaches everyone under the named units.
            DataScopeKind.BusinessUnit => employees.Where(employee =>
                employee.DepartmentId != null
                && dbContext.Departments.Any(department =>
                    department.Id == employee.DepartmentId
                    && department.BusinessUnitId != null
                    && query.Scope.BusinessUnitIds.Contains(department.BusinessUnitId.Value))),

            // Their reports and themselves — a manager needs their own row in the
            // list they work from.
            DataScopeKind.DirectReports => employees.Where(employee =>
                employee.ManagerId == query.ViewerEmployeeId || employee.Id == query.ViewerEmployeeId),

            DataScopeKind.Self => employees.Where(employee => employee.Id == query.ViewerEmployeeId),

            _ => employees.Where(_ => false),
        };

    private async Task<EmployeeOutcome?> ValidateAsync(
        EmployeeRequest request,
        Guid? excludingId,
        CancellationToken cancellationToken)
    {
        IQueryable<Employee> others = dbContext.Employees
            .Where(employee => employee.Id != excludingId);

        string email = request.Email.Trim();

        if (await others.AnyAsync(
                employee => EF.Functions.ILike(employee.Email, EscapeLike(email), @"\"),
                cancellationToken))
        {
            return EmployeeOutcome.DuplicateEmail;
        }

        string? number = Trimmed(request.EmployeeNumber);

        if (number is not null
            && await others.AnyAsync(employee => employee.EmployeeNumber == number, cancellationToken))
        {
            return EmployeeOutcome.DuplicateEmployeeNumber;
        }

        // The tenant filter makes this a tenant check too: a department from another
        // company simply is not visible here.
        if (!await dbContext.Departments.AnyAsync(
                department => department.Id == request.DepartmentId, cancellationToken))
        {
            return EmployeeOutcome.UnknownDepartment;
        }

        if (request.ManagerId is Guid managerId)
        {
            if (managerId == excludingId)
            {
                return EmployeeOutcome.ManagerCycle;
            }

            if (!await dbContext.Employees.AnyAsync(
                    manager => manager.Id == managerId, cancellationToken))
            {
                return EmployeeOutcome.UnknownManager;
            }

            if (excludingId is Guid employeeId
                && await ReportsToAsync(managerId, employeeId, cancellationToken))
            {
                return EmployeeOutcome.ManagerCycle;
            }
        }

        return null;
    }

    // Walks up from the proposed manager. If the employee appears, pointing one at
    // the other would close a loop and every hierarchy walk after it would not
    // terminate.
    private async Task<bool> ReportsToAsync(
        Guid managerId,
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        Guid? current = managerId;
        HashSet<Guid> seen = [];

        while (current is Guid id && seen.Add(id))
        {
            if (id == employeeId)
            {
                return true;
            }

            current = await dbContext.Employees
                .Where(employee => employee.Id == id)
                .Select(employee => employee.ManagerId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return false;
    }

    // Each block is upserted only when the request carries it. An absent block means
    // "this request is not about that step" — on update, a form that never opened the
    // Bank Details tab must not wipe what is already there.
    private async Task ApplyBlocksAsync(
        Employee employee,
        SelfProfileRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (request.BankDetails is BankDetailsRequest bank)
        {
            EmployeeBankDetails row = await dbContext.EmployeeBankDetails
                .FirstOrDefaultAsync(
                    candidate => candidate.EmployeeId == employee.Id, cancellationToken)
                ?? Add(new EmployeeBankDetails { Id = Guid.NewGuid(), EmployeeId = employee.Id });

            row.BankName = Trimmed(bank.BankName);
            row.AccountNumber = Trimmed(bank.AccountNumber);
            row.SortCode = Trimmed(bank.SortCode);
            row.AccountHolderName = Trimmed(bank.AccountHolderName);
            row.UpdatedAt = now;
        }

        if (request.IdentityDocuments is IdentityDocumentsRequest documents)
        {
            EmployeeIdentityDocuments row = await dbContext.EmployeeIdentityDocuments
                .FirstOrDefaultAsync(
                    candidate => candidate.EmployeeId == employee.Id, cancellationToken)
                ?? Add(new EmployeeIdentityDocuments
                {
                    Id = Guid.NewGuid(),
                    EmployeeId = employee.Id,
                });

            row.NationalIdNumber = Trimmed(documents.NationalIdNumber);
            row.TaxIdNumber = Trimmed(documents.TaxIdNumber);
            row.PensionId = Trimmed(documents.PensionId);
            row.HousingFundNumber = Trimmed(documents.HousingFundNumber);
            row.DrivingLicenceNumber = Trimmed(documents.DrivingLicenceNumber);
            row.DrivingLicenceExpiry = documents.DrivingLicenceExpiry;
            row.PassportNumber = Trimmed(documents.PassportNumber);
            row.PassportExpiry = documents.PassportExpiry;
            row.PassportIssuingCountry = Trimmed(documents.PassportIssuingCountry);
            row.UpdatedAt = now;
        }

        if (request.Medical is MedicalRequest medical)
        {
            EmployeeMedical row = await dbContext.EmployeeMedical
                .FirstOrDefaultAsync(
                    candidate => candidate.EmployeeId == employee.Id, cancellationToken)
                ?? Add(new EmployeeMedical { Id = Guid.NewGuid(), EmployeeId = employee.Id });

            row.Allergies = Trimmed(medical.Allergies);
            row.Conditions = Trimmed(medical.Conditions);
            row.Medications = Trimmed(medical.Medications);
            row.DietaryRequirements = Trimmed(medical.DietaryRequirements);
            row.AccessibilityNeeds = Trimmed(medical.AccessibilityNeeds);
            row.UpdatedAt = now;
        }

        // TenantId is stamped by SaveChanges, so it is not set here.
        TEntity Add<TEntity>(TEntity row) where TEntity : class
        {
            dbContext.Set<TEntity>().Add(row);
            return row;
        }
    }

    public async Task<MedicalDto?> GetMedicalAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default) =>
        await dbContext.EmployeeMedical
            .AsNoTracking()
            .Where(medical => medical.EmployeeId == employeeId)
            .Select(medical => new MedicalDto
            {
                Allergies = medical.Allergies,
                Conditions = medical.Conditions,
                Medications = medical.Medications,
                DietaryRequirements = medical.DietaryRequirements,
                AccessibilityNeeds = medical.AccessibilityNeeds,
            })
            .FirstOrDefaultAsync(cancellationToken);

    public async Task StageSelfProfileAsync(
        Guid employeeId,
        SelfProfileRequest profile,
        CancellationToken cancellationToken = default)
    {
        Employee? employee = await dbContext.Employees
            .FirstOrDefaultAsync(candidate => candidate.Id == employeeId, cancellationToken);

        if (employee is null)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Only the self-owned half. Nothing here can touch department, job title,
        // employment type, manager, start date, status or assigned kit.
        ApplySelf(employee, profile);
        employee.UpdatedAt = now;

        await ApplyBlocksAsync(employee, profile, now, cancellationToken);
    }

    // The half of the form the person owns, shared by the manual wizard and
    // self-onboarding so the two cannot drift apart.
    private static void ApplySelf(Employee employee, SelfProfileRequest request)
    {
        employee.Title = Trimmed(request.Title);
        employee.PreferredName = Trimmed(request.PreferredName);
        employee.MaidenName = Trimmed(request.MaidenName);
        employee.Initials = Trimmed(request.Initials);
        employee.Phone = Trimmed(request.Phone);
        employee.DateOfBirth = request.DateOfBirth;
        employee.Gender = Trimmed(request.Gender);
        employee.Nationality = Trimmed(request.Nationality);
        employee.Ethnicity = Trimmed(request.Ethnicity);
        employee.MaritalStatus = Trimmed(request.MaritalStatus);
        employee.Address = Trimmed(request.Address);
        employee.State = Trimmed(request.State);
        employee.CountryOfEmployment = Trimmed(request.CountryOfEmployment);
        employee.EmergencyContactName = Trimmed(request.EmergencyContactName);
        employee.EmergencyContactRelationship = Trimmed(request.EmergencyContactRelationship);
        employee.EmergencyContactPhone = Trimmed(request.EmergencyContactPhone);
        employee.EmergencyContactEmail = Trimmed(request.EmergencyContactEmail);
    }

    private static void Apply(Employee employee, EmployeeRequest request)
    {
        ApplySelf(employee, request);

        employee.FirstName = request.FirstName.Trim();
        employee.MiddleName = Trimmed(request.MiddleName);
        employee.LastName = request.LastName.Trim();
        employee.Email = request.Email.Trim();
        employee.EmployeeNumber = Trimmed(request.EmployeeNumber);
        employee.JobTitle = Trimmed(request.JobTitle);
        employee.DepartmentId = request.DepartmentId;
        employee.EmploymentType = request.EmploymentType;
        employee.ManagerId = request.ManagerId;
        employee.StartDate = request.StartDate;
        employee.WorkLocation = Trimmed(request.WorkLocation);
        employee.WorkMode = request.WorkMode;
        employee.Grade = Trimmed(request.Grade);
        employee.Status = request.Status;
    }

    private IQueryable<EmployeeDto> Project(IQueryable<Employee> query) =>
        query.Select(employee => new EmployeeDto
        {
            Id = employee.Id,
            Title = employee.Title,
            FirstName = employee.FirstName,
            MiddleName = employee.MiddleName,
            LastName = employee.LastName,
            FullName = employee.MiddleName == null
                ? employee.FirstName + " " + employee.LastName
                : employee.FirstName + " " + employee.MiddleName + " " + employee.LastName,
            PreferredName = employee.PreferredName,
            MaidenName = employee.MaidenName,
            Initials = employee.Initials,
            AvatarFileId = employee.AvatarFileId,
            Email = employee.Email,
            Phone = employee.Phone,
            DateOfBirth = employee.DateOfBirth,
            Gender = employee.Gender,
            Nationality = employee.Nationality,
            Ethnicity = employee.Ethnicity,
            MaritalStatus = employee.MaritalStatus,
            Address = employee.Address,
            State = employee.State,
            CountryOfEmployment = employee.CountryOfEmployment,
            EmployeeNumber = employee.EmployeeNumber,
            JobTitle = employee.JobTitle,
            DepartmentId = employee.DepartmentId,
            Department = dbContext.Departments
                .Where(department => department.Id == employee.DepartmentId)
                .Select(department => department.Name)
                .FirstOrDefault(),
            EmploymentType = employee.EmploymentType,
            ManagerId = employee.ManagerId,
            ManagerName = dbContext.Employees
                .Where(manager => manager.Id == employee.ManagerId)
                .Select(manager => manager.FirstName + " " + manager.LastName)
                .FirstOrDefault(),
            DirectReportCount = dbContext.Employees.Count(report => report.ManagerId == employee.Id),
            Status = employee.Status,
            StartDate = employee.StartDate,
            WorkLocation = employee.WorkLocation,
            WorkMode = employee.WorkMode,
            Grade = employee.Grade,
            EmergencyContactName = employee.EmergencyContactName,
            EmergencyContactRelationship = employee.EmergencyContactRelationship,
            EmergencyContactPhone = employee.EmergencyContactPhone,
            EmergencyContactEmail = employee.EmergencyContactEmail,
            BankDetails = dbContext.EmployeeBankDetails
                .Where(details => details.EmployeeId == employee.Id)
                .Select(details => new BankDetailsDto
                {
                    BankName = details.BankName,
                    AccountNumber = details.AccountNumber,
                    SortCode = details.SortCode,
                    AccountHolderName = details.AccountHolderName,
                })
                .FirstOrDefault(),
            IdentityDocuments = dbContext.EmployeeIdentityDocuments
                .Where(documents => documents.EmployeeId == employee.Id)
                .Select(documents => new IdentityDocumentsDto
                {
                    NationalIdNumber = documents.NationalIdNumber,
                    TaxIdNumber = documents.TaxIdNumber,
                    PensionId = documents.PensionId,
                    HousingFundNumber = documents.HousingFundNumber,
                    DrivingLicenceNumber = documents.DrivingLicenceNumber,
                    DrivingLicenceExpiry = documents.DrivingLicenceExpiry,
                    PassportNumber = documents.PassportNumber,
                    PassportExpiry = documents.PassportExpiry,
                    PassportIssuingCountry = documents.PassportIssuingCountry,
                })
                .FirstOrDefault(),
            OnboardingMethod = employee.OnboardingMethod,
            CreatedAt = employee.CreatedAt,
            UpdatedAt = employee.UpdatedAt,
        });

    private static string EscapeLike(string value) => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
