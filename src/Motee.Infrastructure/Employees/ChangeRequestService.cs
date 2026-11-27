using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Domain.Audit;
using Motee.Domain.Employees;
using Motee.Domain.Files;
using Motee.Infrastructure.Approvals;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Employees;

// Employees asking for their own details to be corrected.
//
// The approval writes the field. That is the whole difference from what this replaces:
// the profile screen approved a change into a client-side override map laid over the
// employee it had loaded, so the record never moved and the next reader saw the old
// value. Here the decision and the write happen in one transaction, because a request
// recorded as approved whose field never changed is worse than a failure — nothing
// afterwards will notice.
internal sealed class ChangeRequestService(
    MoteeDbContext dbContext,
    ICurrentEmployee currentEmployee,
    IRequestContext requestContext,
    ApprovalAttachmentLinker evidenceLinks,
    IAuditTrail audit,
    TimeProvider timeProvider) : IChangeRequestService
{
    private const string Module = "organization.employees";

    private Guid? CurrentUser() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    public async Task<IReadOnlyList<RequestableFieldDto>> FieldsAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return [];
        }

        EmployeeProfile? profile = await ProfileAsync(me, tracked: false, cancellationToken);

        if (profile is null)
        {
            return [];
        }

        return
        [
            .. ProfileFields.Catalogue.Select(field => new RequestableFieldDto
            {
                Key = field.Key,
                Label = field.Label,
                Group = field.Group,
                CurrentValue = field.Read(profile),
            }),
        ];
    }

    public async Task<ChangeRequestResult> RaiseAsync(
        RaiseChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.NoEmployeeRecord);
        }

        ProfileField? field = ProfileFields.Find(request.Field);

        if (field is null)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.FieldNotRequestable);
        }

        string? reason = Trimmed(request.Reason);

        if (reason is null)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.ReasonRequired);
        }

        if (reason.Length > ChangeRequests.LongestReason)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.ReasonTooLong);
        }

        EmployeeProfile? profile = await ProfileAsync(me, tracked: false, cancellationToken);

        if (profile is null)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.NoEmployeeRecord);
        }

        string? current = field.Read(profile);
        string? wanted = Trimmed(request.RequestedValue);

        // Nothing to decide. Without this the queue fills with requests whose approval
        // changes nothing, and HR has to open each one to find that out.
        if (string.Equals(current, wanted, StringComparison.Ordinal))
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.NoChange);
        }

        // One pending request per field per person. Two would leave the queue with the
        // same decision to make twice, and whichever was approved second would silently
        // overwrite the first.
        bool already = await dbContext.EmployeeChangeRequests.AnyAsync(
            row => row.EmployeeId == me
                && row.Field == field.Key
                && row.Status == ChangeRequestStatus.Pending,
            cancellationToken);

        if (already)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.AlreadyPending);
        }

        if (request.FileId is Guid fileId && !await IsEvidenceAsync(fileId, cancellationToken))
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.UnknownFile);
        }

        EmployeeChangeRequest row = new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = me,
            Field = field.Key,

            // The key, not what the caller sent: "FirstName" and "firstName" are the
            // same field, and storing the caller's spelling would make the history
            // depend on which client wrote it.
            RequestedValue = wanted,
            CurrentValue = current,
            Reason = reason,
            FileId = request.FileId,
            Status = ChangeRequestStatus.Pending,
            RequestedByUserId = CurrentUser(),
            RequestedAt = timeProvider.GetUtcNow(),
        };

        dbContext.EmployeeChangeRequests.Add(row);

        // The values are deliberately not in the description. A change request is about
        // somebody's own details — a maiden name, a home address — and spelling the old
        // and new values into an audit line would put them somewhere with different
        // reading rules from the record itself.
        audit.Record(
            AuditAction.Create,
            Module,
            $"Change requested to {field.Label}",
            entityId: row.Id,
            entityType: nameof(EmployeeChangeRequest));

        await dbContext.SaveChangesAsync(cancellationToken);

        return ChangeRequestResult.Ok(await DescribeAsync(row, cancellationToken));
    }

    public async Task<ChangeRequestResult> WithdrawAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EmployeeChangeRequest? row = await dbContext.EmployeeChangeRequests
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (row is null)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.NotFound);
        }

        // Theirs to withdraw, and only theirs. Checked against the employee rather than
        // the account, so somebody who signed in on a new account still owns the
        // requests made about their own record.
        if (await currentEmployee.IdAsync(cancellationToken) != row.EmployeeId)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.NotTheRequester);
        }

        if (!ChangeRequests.IsOpen(row.Status))
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.AlreadyDecided);
        }

        row.Status = ChangeRequestStatus.Withdrawn;
        row.DecidedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ChangeRequestResult.Ok(await DescribeAsync(row, cancellationToken));
    }

    public async Task<IReadOnlyList<ChangeRequestDto>> MineAsync(
        CancellationToken cancellationToken = default) =>
        await currentEmployee.IdAsync(cancellationToken) is Guid me
            ? await ForEmployeeAsync(me, cancellationToken)
            : [];

    public async Task<IReadOnlyList<ChangeRequestDto>> ForEmployeeAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        List<EmployeeChangeRequest> rows = await dbContext.EmployeeChangeRequests
            .AsNoTracking()
            .Where(row => row.EmployeeId == employeeId)
            .OrderByDescending(row => row.RequestedAt)
            .ThenByDescending(row => row.Id)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(rows, cancellationToken);
    }

    public async Task<PagedResult<ChangeRequestDto>> ListAsync(
        ChangeRequestQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<EmployeeChangeRequest> matching = dbContext.EmployeeChangeRequests
            .AsNoTracking();

        if (query.Status is ChangeRequestStatus status)
        {
            matching = matching.Where(row => row.Status == status);
        }

        if (query.EmployeeId is Guid employeeId)
        {
            matching = matching.Where(row => row.EmployeeId == employeeId);
        }

        int total = await matching.CountAsync(cancellationToken);

        // Oldest first, unlike the register: this is a queue to work through, and the
        // request that has been waiting longest is the one to decide next.
        List<EmployeeChangeRequest> page = await matching
            .OrderBy(row => row.RequestedAt)
            .ThenBy(row => row.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<ChangeRequestDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<ChangeRequestResult> ApproveAsync(
        Guid id,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        EmployeeChangeRequest? row = await dbContext.EmployeeChangeRequests
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (row is null)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.NotFound);
        }

        if (!ChangeRequests.IsOpen(row.Status))
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.AlreadyDecided);
        }

        // Re-found in the catalogue rather than trusted from the row. A field removed
        // from the whitelist since the request was raised must not be writable by
        // approving something old.
        ProfileField? field = ProfileFields.Find(row.Field);

        if (field is null)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.FieldNotRequestable);
        }

        EmployeeProfile? profile = await ProfileAsync(
            row.EmployeeId, tracked: true, cancellationToken);

        if (profile is null)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.NotFound);
        }

        ProfileFieldOutcome applied = field.Apply(profile, row.RequestedValue);

        if (applied != ProfileFieldOutcome.Applied)
        {
            // Nothing is saved. The request stays pending, because a refused value is
            // something to go back to the employee about rather than a decision.
            return ChangeRequestResult.Failed(
                ChangeRequestOutcome.ValueRefused, Explain(field, applied));
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        profile.Employee.UpdatedAt = now;
        profile.Bank.UpdatedAt = now;
        profile.Identity.UpdatedAt = now;

        row.Status = ChangeRequestStatus.Approved;
        row.DecidedByUserId = CurrentUser();
        row.DecidedAt = timeProvider.GetUtcNow();
        row.DecisionNote = Trimmed(note);

        audit.Record(
            AuditAction.Approve,
            Module,
            $"Change to {field.Label} approved",
            entityId: row.Id,
            entityType: nameof(EmployeeChangeRequest));

        // One SaveChanges for the decision and the write together. Two would leave a
        // window where the request reads approved and the record still holds the old
        // value, and nothing would ever come back to finish it.
        await dbContext.SaveChangesAsync(cancellationToken);

        return ChangeRequestResult.Ok(await DescribeAsync(row, cancellationToken));
    }

    public async Task<ChangeRequestResult> RejectAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default)
    {
        string? note = Trimmed(reason);

        if (note is null)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.NoteRequired);
        }

        if (note.Length > ChangeRequests.LongestNote)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.ReasonTooLong);
        }

        EmployeeChangeRequest? row = await dbContext.EmployeeChangeRequests
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (row is null)
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.NotFound);
        }

        if (!ChangeRequests.IsOpen(row.Status))
        {
            return ChangeRequestResult.Failed(ChangeRequestOutcome.AlreadyDecided);
        }

        row.Status = ChangeRequestStatus.Rejected;
        row.DecidedByUserId = CurrentUser();
        row.DecidedAt = timeProvider.GetUtcNow();
        row.DecisionNote = note;

        audit.Record(
            AuditAction.Reject,
            Module,
            $"Change to {ProfileFields.Find(row.Field)?.Label ?? row.Field} rejected",
            entityId: row.Id,
            entityType: nameof(EmployeeChangeRequest));

        await dbContext.SaveChangesAsync(cancellationToken);

        return ChangeRequestResult.Ok(await DescribeAsync(row, cancellationToken));
    }

    private static string Explain(ProfileField field, ProfileFieldOutcome outcome) =>
        outcome switch
        {
            ProfileFieldOutcome.Required =>
                $"{field.Label} cannot be left empty.",
            ProfileFieldOutcome.TooLong =>
                $"{field.Label} is longer than the record allows.",
            ProfileFieldOutcome.NotADate =>
                $"{field.Label} has to be a date, written as YYYY-MM-DD.",
            ProfileFieldOutcome.OutOfRange =>
                $"{field.Label} cannot be in the future.",
            _ => $"{field.Label} could not be updated.",
        };

    private async Task<bool> IsEvidenceAsync(Guid fileId, CancellationToken cancellationToken) =>
        await dbContext.StoredFiles.AnyAsync(
            file => file.Id == fileId && file.Purpose == FilePurpose.ProfileChangeEvidence,
            cancellationToken);

    private async Task<ChangeRequestDto> DescribeAsync(
        EmployeeChangeRequest row,
        CancellationToken cancellationToken) =>
        (await DescribeAsync([row], cancellationToken))[0];

    private async Task<List<ChangeRequestDto>> DescribeAsync(
        IReadOnlyList<EmployeeChangeRequest> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        List<Guid> employeeIds = [.. rows.Select(row => row.EmployeeId).Distinct()];

        Dictionary<Guid, string> people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .ToDictionaryAsync(
                employee => employee.Id,
                employee => employee.FirstName + " " + employee.LastName,
                cancellationToken);

        // Names read from the accounts rather than stored on the row, so a name change
        // follows the person instead of freezing at the moment they decided.
        List<Guid> userIds =
        [
            .. rows.Where(row => row.RequestedByUserId is not null)
                .Select(row => row.RequestedByUserId!.Value),
            .. rows.Where(row => row.DecidedByUserId is not null)
                .Select(row => row.DecidedByUserId!.Value),
        ];

        Dictionary<Guid, string> accounts = userIds.Count == 0
            ? []
            : await dbContext.Users
                .AsNoTracking()
                .Where(user => userIds.Contains(user.Id))
                .ToDictionaryAsync(
                    user => user.Id,
                    user => user.FirstName + " " + user.LastName,
                    cancellationToken);

        IReadOnlyDictionary<Guid, string> urls = await evidenceLinks.UrlsForAsync(
            rows.Where(row => row.FileId is not null).Select(row => row.FileId!.Value),
            cancellationToken);

        return
        [
            .. rows.Select(row => new ChangeRequestDto
            {
                Id = row.Id,
                EmployeeId = row.EmployeeId,
                EmployeeName = people.GetValueOrDefault(row.EmployeeId, "Unknown"),
                Field = row.Field,

                // From the catalogue, falling back to the stored key for a field that
                // has since been withdrawn from it — the history still has to read.
                Label = ProfileFields.Find(row.Field)?.Label ?? row.Field,
                CurrentValue = row.CurrentValue,
                RequestedValue = row.RequestedValue,
                Reason = row.Reason,
                FileId = row.FileId,
                FileUrl = row.FileId is Guid fileId
                    ? urls.GetValueOrDefault(fileId)
                    : null,
                Status = row.Status,
                RequestedByName = row.RequestedByUserId is Guid requester
                    ? accounts.GetValueOrDefault(requester)
                    : null,
                RequestedAt = row.RequestedAt,
                DecidedByName = row.DecidedByUserId is Guid decider
                    ? accounts.GetValueOrDefault(decider)
                    : null,
                DecidedAt = row.DecidedAt,
                DecisionNote = row.DecisionNote,
            }),
        ];
    }

    // An employee and the two one-per-person blocks beside them.
    //
    // Bank details and identity documents are created on demand, the same way the
    // profile form creates them, so every field in the catalogue has somewhere to read
    // from and write to. Created rather than treated as missing: a request to set a
    // national ID number on somebody who has never had one is the ordinary case, not an
    // error, and a null block would make the catalogue's reads conditional.
    //
    // `tracked` because reading for a form and writing on approval want different
    // things: an untracked read for the field list, a tracked one when a value is about
    // to be applied and saved.
    private async Task<EmployeeProfile?> ProfileAsync(
        Guid employeeId,
        bool tracked,
        CancellationToken cancellationToken)
    {
        IQueryable<Employee> employees = tracked
            ? dbContext.Employees
            : dbContext.Employees.AsNoTracking();

        Employee? employee = await employees
            .FirstOrDefaultAsync(row => row.Id == employeeId, cancellationToken);

        if (employee is null)
        {
            return null;
        }

        IQueryable<EmployeeBankDetails> banks = tracked
            ? dbContext.EmployeeBankDetails
            : dbContext.EmployeeBankDetails.AsNoTracking();

        EmployeeBankDetails bank =
            await banks.FirstOrDefaultAsync(
                row => row.EmployeeId == employeeId, cancellationToken)
            ?? NewBank(employeeId, tracked);

        IQueryable<EmployeeIdentityDocuments> identities = tracked
            ? dbContext.EmployeeIdentityDocuments
            : dbContext.EmployeeIdentityDocuments.AsNoTracking();

        EmployeeIdentityDocuments identity =
            await identities.FirstOrDefaultAsync(
                row => row.EmployeeId == employeeId, cancellationToken)
            ?? NewIdentity(employeeId, tracked);

        return new EmployeeProfile(employee, bank, identity);
    }

    // Added to the context only on the tracked path. An untracked read is answering
    // "what does this field hold" for a form, and inserting an empty block as a
    // side effect of drawing a page would leave a row per employee who ever opened it.
    private EmployeeBankDetails NewBank(Guid employeeId, bool tracked)
    {
        EmployeeBankDetails row = new() { Id = Guid.NewGuid(), EmployeeId = employeeId };

        if (tracked)
        {
            dbContext.EmployeeBankDetails.Add(row);
        }

        return row;
    }

    private EmployeeIdentityDocuments NewIdentity(Guid employeeId, bool tracked)
    {
        EmployeeIdentityDocuments row = new() { Id = Guid.NewGuid(), EmployeeId = employeeId };

        if (tracked)
        {
            dbContext.EmployeeIdentityDocuments.Add(row);
        }

        return row;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
