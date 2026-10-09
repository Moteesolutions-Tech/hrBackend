using Microsoft.EntityFrameworkCore;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Offboarding;
using Motee.Domain.Employees;
using Motee.Domain.Offboarding;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Offboarding;

internal sealed class OffboardingService(
    MoteeDbContext dbContext,
    IRequestContext requestContext,
    IRefreshTokenService refreshTokens,
    TimeProvider timeProvider) : IOffboardingService
{
    public async Task<PagedResult<OffboardingListItemDto>> ListAsync(
        OffboardingQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<OffboardingRecord> matching = Filter(query);

        int total = await matching.CountAsync(cancellationToken);

        List<OffboardingListItemDto> items = await matching
            .OrderByDescending(record => record.InitiatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(record => new OffboardingListItemDto
            {
                Id = record.Id,
                EmployeeId = record.EmployeeId,
                // Read through from the employee rather than copied onto the record, so
                // a leaver who married between resigning and going is not filed under
                // the wrong name.
                EmployeeName = dbContext.Employees
                    .Where(employee => employee.Id == record.EmployeeId)
                    .Select(employee => employee.FirstName + " " + employee.LastName)
                    .FirstOrDefault() ?? "Unknown",
                Initials = dbContext.Employees
                    .Where(employee => employee.Id == record.EmployeeId)
                    .Select(employee =>
                        employee.FirstName.Substring(0, 1) + employee.LastName.Substring(0, 1))
                    .FirstOrDefault() ?? "?",
                JobTitle = dbContext.Employees
                    .Where(employee => employee.Id == record.EmployeeId)
                    .Select(employee => employee.JobTitle)
                    .FirstOrDefault(),
                Department = dbContext.Employees
                    .Where(employee => employee.Id == record.EmployeeId)
                    .SelectMany(employee => dbContext.Departments
                        .Where(department => department.Id == employee.DepartmentId)
                        .Select(department => department.Name))
                    .FirstOrDefault(),
                LastWorkingDate = record.LastWorkingDate,
                ExitReason = record.ExitReason,
                Status = record.Status,
                ClearanceCompleted = dbContext.OffboardingClearance
                    .Count(item => item.OffboardingRecordId == record.Id && item.Completed),
                ClearanceTotal = dbContext.OffboardingClearance
                    .Count(item => item.OffboardingRecordId == record.Id),

                // Filled after materialising: the lifecycle is a dictionary lookup and
                // has no SQL translation. Array.Empty rather than a collection
                // expression, which an expression tree cannot contain.
                AvailableActions = Array.Empty<OffboardingAction>(),
                InitiatedAt = record.InitiatedAt,
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<OffboardingListItemDto>
        {
            Items = [.. items.Select(item => item with
            {
                AvailableActions = OffboardingLifecycle.AvailableFrom(item.Status),
            })],
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<OffboardingDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        OffboardingRecord? record = await dbContext.OffboardingRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return record is null ? null : await ProjectAsync(record, cancellationToken);
    }

    public async Task<OffboardingStatsDto> StatsAsync(CancellationToken cancellationToken = default)
    {
        List<OffboardingRecord> records = await dbContext.OffboardingRecords
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // The number HR chases: still leaving, and something is outstanding.
        HashSet<Guid> withOutstanding = [.. await dbContext.OffboardingClearance
            .AsNoTracking()
            .Where(item => !item.Completed)
            .Select(item => item.OffboardingRecordId)
            .Distinct()
            .ToListAsync(cancellationToken)];

        return new OffboardingStatsDto
        {
            Total = records.Count,
            Pending = records.Count(record => record.Status == OffboardingStatus.Pending),
            InProgress = records.Count(record => record.Status == OffboardingStatus.InProgress),
            Completed = records.Count(record => record.Status == OffboardingStatus.Completed),
            ClearancePending = records.Count(record =>
                OffboardingLifecycle.IsOpen(record.Status) && withOutstanding.Contains(record.Id)),
        };
    }

    public async Task<OffboardingResult> InitiateAsync(
        InitiateOffboardingRequest request,
        CancellationToken cancellationToken = default)
    {
        Employee? employee = await dbContext.Employees
            .FirstOrDefaultAsync(candidate => candidate.Id == request.EmployeeId, cancellationToken);

        if (employee is null)
        {
            return OffboardingResult.Failed(OffboardingOutcome.EmployeeNotFound);
        }

        // A second live record would give one person two exit dates and two checklists,
        // and payroll would have to guess which one to pay against.
        bool alreadyLeaving = await dbContext.OffboardingRecords
            .AnyAsync(
                record => record.EmployeeId == request.EmployeeId
                    && (record.Status == OffboardingStatus.Pending
                        || record.Status == OffboardingStatus.Approved
                        || record.Status == OffboardingStatus.InProgress),
                cancellationToken);

        if (alreadyLeaving)
        {
            return OffboardingResult.Failed(OffboardingOutcome.AlreadyOffboarding);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        OffboardingRecord created = new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = request.EmployeeId,
            Status = OffboardingStatus.Pending,
            ExitReason = request.ExitReason,
            LastWorkingDate = request.LastWorkingDate,
            Notes = Trimmed(request.Notes),
            InitiatedByUserId = CurrentUser(),
            InitiatedAt = now,
            UpdatedAt = now,
        };

        dbContext.OffboardingRecords.Add(created);

        // The checklist exists from the start rather than on approval: HR begins
        // acknowledging the resignation before anybody has decided anything.
        for (int index = 0; index < DefaultClearance.Items.Count; index++)
        {
            (string label, string department) = DefaultClearance.Items[index];

            dbContext.OffboardingClearance.Add(new OffboardingClearanceItem
            {
                Id = Guid.NewGuid(),
                OffboardingRecordId = created.Id,
                Label = label,
                Department = department,
                Sequence = index,
            });
        }

        // Notice served is a real change to their employment, visible on the employee
        // record rather than only inside this module.
        if (EmployeeLifecycle.CanMove(employee.Status, EmployeeStatus.Offboarding))
        {
            employee.Status = EmployeeStatus.Offboarding;
            employee.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return OffboardingResult.Ok((await GetAsync(created.Id, cancellationToken))!);
    }

    public async Task<OffboardingResult> UpdateAsync(
        Guid id,
        UpdateOffboardingRequest request,
        CancellationToken cancellationToken = default)
    {
        OffboardingRecord? record = await FindAsync(id, cancellationToken);

        if (record is null)
        {
            return OffboardingResult.Failed(OffboardingOutcome.NotFound);
        }

        // A finished record is history. Editing the exit date of a departure that has
        // already happened rewrites what payroll acted on.
        if (OffboardingLifecycle.IsFinal(record.Status))
        {
            return OffboardingResult.Failed(OffboardingOutcome.NotAllowed);
        }

        record.ExitReason = request.ExitReason;
        record.LastWorkingDate = request.LastWorkingDate;
        record.Notes = Trimmed(request.Notes);
        record.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return OffboardingResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    // Every state change goes through here. The lifecycle says whether the move is
    // legal; this applies what it means for the employee and the record.
    public async Task<OffboardingResult> ApplyAsync(
        Guid id,
        OffboardingAction action,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        OffboardingRecord? record = await FindAsync(id, cancellationToken);

        if (record is null)
        {
            return OffboardingResult.Failed(OffboardingOutcome.NotFound);
        }

        if (OffboardingLifecycle.Next(record.Status, action) is not OffboardingStatus next)
        {
            return OffboardingResult.Failed(OffboardingOutcome.NotAllowed);
        }

        // An exit refused without a reason is a decision nobody can defend later, and
        // the person it was refused for is entitled to know why.
        if (action == OffboardingAction.Disapprove && string.IsNullOrWhiteSpace(reason))
        {
            return OffboardingResult.Failed(OffboardingOutcome.ReasonRequired);
        }

        Employee? employee = await dbContext.Employees
            .FirstOrDefaultAsync(candidate => candidate.Id == record.EmployeeId, cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        record.Status = next;
        record.UpdatedAt = now;

        switch (action)
        {
            case OffboardingAction.Approve:
            case OffboardingAction.Disapprove:
                record.DecidedByUserId = CurrentUser();
                record.DecidedAt = now;
                record.DecisionReason = Trimmed(reason);
                break;

            case OffboardingAction.Complete:
                record.CompletedAt = now;

                // The point of the whole record. They have gone: the employee is
                // inactive and the date they left is on their row, where payroll and
                // every report reads it from.
                if (employee is not null
                    && EmployeeLifecycle.CanMove(employee.Status, EmployeeStatus.Inactive))
                {
                    employee.Status = EmployeeStatus.Inactive;
                    employee.DateOfLeaving = record.LastWorkingDate;
                    employee.UpdatedAt = now;
                }

                break;

            case OffboardingAction.Reactivate:
                record.ReactivatedAt = now;
                record.ReactivatedByUserId = CurrentUser();

                // They are staying. Back to Active from Offboarding, which the employee
                // lifecycle permits precisely because notice can be withdrawn.
                if (employee is not null
                    && EmployeeLifecycle.CanMove(employee.Status, EmployeeStatus.Active))
                {
                    employee.Status = EmployeeStatus.Active;
                    employee.UpdatedAt = now;
                }

                break;

            case OffboardingAction.StartClearance:
            default:
                break;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return OffboardingResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<OffboardingResult> CompleteClearanceAsync(
        Guid id,
        Guid itemId,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        OffboardingRecord? record = await FindAsync(id, cancellationToken);

        if (record is null)
        {
            return OffboardingResult.Failed(OffboardingOutcome.NotFound);
        }

        OffboardingClearanceItem? item = await dbContext.OffboardingClearance
            .FirstOrDefaultAsync(
                candidate => candidate.Id == itemId && candidate.OffboardingRecordId == id,
                cancellationToken);

        if (item is null)
        {
            return OffboardingResult.Failed(OffboardingOutcome.ClearanceItemNotFound);
        }

        // Clearance on a refused or withdrawn exit is work nobody should be doing —
        // the person is staying.
        if (!OffboardingLifecycle.IsOpen(record.Status))
        {
            return OffboardingResult.Failed(OffboardingOutcome.NotAllowed);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        item.Completed = true;
        item.CompletedAt = now;
        item.CompletedByUserId = CurrentUser();
        item.Notes = Trimmed(notes);

        // The first tick is what moves an approved exit into clearance. Done here rather
        // than expecting a caller to remember: a state nothing enters is a state that
        // does not exist.
        if (OffboardingLifecycle.Next(record.Status, OffboardingAction.StartClearance)
            is OffboardingStatus started)
        {
            record.Status = started;
        }

        record.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return OffboardingResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<OffboardingResult> RevokeAccessAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        OffboardingRecord? record = await FindAsync(id, cancellationToken);

        if (record is null)
        {
            return OffboardingResult.Failed(OffboardingOutcome.NotFound);
        }

        // Only once the exit is agreed. Cutting someone's access while their resignation
        // is still being decided is a decision taken by accident.
        if (record.Status is not (OffboardingStatus.Approved or OffboardingStatus.InProgress))
        {
            return OffboardingResult.Failed(OffboardingOutcome.NotAllowed);
        }

        Guid? userId = await dbContext.Users
            .Where(user => user.EmployeeId == record.EmployeeId)
            .Select(user => (Guid?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (userId is Guid account)
        {
            // Revokes every refresh token. Their access token still works until it
            // expires — fifteen minutes — because the alternative is checking a
            // revocation list on every request for the sake of one rare event.
            await refreshTokens.RevokeAllAsync(account, cancellationToken);
        }

        record.SystemAccessRevokedAt = timeProvider.GetUtcNow();
        record.UpdatedAt = record.SystemAccessRevokedAt.Value;

        await dbContext.SaveChangesAsync(cancellationToken);

        return OffboardingResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public Task<OffboardingResult> ScheduleExitInterviewAsync(
        Guid id,
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken = default) =>
        StampAsync(id, record => record.ExitInterviewScheduledAt = scheduledAt, cancellationToken);

    public Task<OffboardingResult> CompleteExitInterviewAsync(
        Guid id,
        string? notes,
        CancellationToken cancellationToken = default) =>
        StampAsync(
            id,
            record =>
            {
                record.ExitInterviewCompletedAt = timeProvider.GetUtcNow();
                record.ExitInterviewNotes = Trimmed(notes);
            },
            cancellationToken);

    public Task<OffboardingResult> GenerateExitDocumentsAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        StampAsync(
            id,
            record => record.ExitDocumentsGeneratedAt = timeProvider.GetUtcNow(),
            cancellationToken);

    public async Task<OffboardingOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        OffboardingRecord? record = await FindAsync(id, cancellationToken);

        if (record is null)
        {
            return OffboardingOutcome.NotFound;
        }

        // A completed exit is the record of someone leaving. Removing it would erase
        // why they went and what was returned, which is the one thing this table is for.
        if (record.Status == OffboardingStatus.Completed)
        {
            return OffboardingOutcome.NotAllowed;
        }

        dbContext.OffboardingClearance.RemoveRange(
            dbContext.OffboardingClearance.Where(item => item.OffboardingRecordId == id));

        dbContext.OffboardingRecords.Remove(record);

        await dbContext.SaveChangesAsync(cancellationToken);

        return OffboardingOutcome.Succeeded;
    }

    private async Task<OffboardingResult> StampAsync(
        Guid id,
        Action<OffboardingRecord> apply,
        CancellationToken cancellationToken)
    {
        OffboardingRecord? record = await FindAsync(id, cancellationToken);

        if (record is null)
        {
            return OffboardingResult.Failed(OffboardingOutcome.NotFound);
        }

        if (OffboardingLifecycle.IsFinal(record.Status))
        {
            return OffboardingResult.Failed(OffboardingOutcome.NotAllowed);
        }

        apply(record);
        record.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return OffboardingResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    private Task<OffboardingRecord?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.OffboardingRecords
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

    private IQueryable<OffboardingRecord> Filter(OffboardingQuery query)
    {
        IQueryable<OffboardingRecord> matching = dbContext.OffboardingRecords.AsNoTracking();

        if (query.Status is OffboardingStatus status)
        {
            matching = matching.Where(record => record.Status == status);
        }

        if (query.Statuses is { Count: > 0 } statuses)
        {
            matching = matching.Where(record => statuses.Contains(record.Status));
        }

        if (query.ExitReason is ExitReason reason)
        {
            matching = matching.Where(record => record.ExitReason == reason);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            matching = matching.Where(record => dbContext.Employees
                .Any(employee => employee.Id == record.EmployeeId
                    && employee.DepartmentId == departmentId));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escape(query.Search.Trim())}%";

            matching = matching.Where(record => dbContext.Employees
                .Any(employee => employee.Id == record.EmployeeId
                    && (EF.Functions.ILike(employee.FirstName, pattern, @"\")
                        || EF.Functions.ILike(employee.LastName, pattern, @"\")
                        || EF.Functions.ILike(employee.Email, pattern, @"\"))));
        }

        return matching;
    }

    private async Task<OffboardingDto> ProjectAsync(
        OffboardingRecord record,
        CancellationToken cancellationToken)
    {
        List<ClearanceItemDto> clearance = await dbContext.OffboardingClearance
            .AsNoTracking()
            .Where(item => item.OffboardingRecordId == record.Id)
            .OrderBy(item => item.Sequence)
            .Select(item => new ClearanceItemDto
            {
                Id = item.Id,
                Label = item.Label,
                Department = item.Department,
                Sequence = item.Sequence,
                Completed = item.Completed,
                CompletedAt = item.CompletedAt,
                CompletedByUserId = item.CompletedByUserId,
                Notes = item.Notes,
            })
            .ToListAsync(cancellationToken);

        Employee? employee = await dbContext.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == record.EmployeeId, cancellationToken);

        string? department = employee?.DepartmentId is Guid departmentId
            ? await dbContext.Departments
                .AsNoTracking()
                .Where(candidate => candidate.Id == departmentId)
                .Select(candidate => candidate.Name)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return new OffboardingDto
        {
            Id = record.Id,
            EmployeeId = record.EmployeeId,
            EmployeeName = employee is null ? "Unknown" : $"{employee.FirstName} {employee.LastName}",
            Initials = employee is null
                ? "?"
                : $"{employee.FirstName[..1]}{employee.LastName[..1]}",
            JobTitle = employee?.JobTitle,
            Department = department,
            Status = record.Status,
            ExitReason = record.ExitReason,
            LastWorkingDate = record.LastWorkingDate,
            Notes = record.Notes,
            Clearance = clearance,
            AvailableActions = OffboardingLifecycle.AvailableFrom(record.Status),
            InitiatedByUserId = record.InitiatedByUserId,
            InitiatedAt = record.InitiatedAt,
            DecidedByUserId = record.DecidedByUserId,
            DecidedAt = record.DecidedAt,
            DecisionReason = record.DecisionReason,
            CompletedAt = record.CompletedAt,
            ReactivatedAt = record.ReactivatedAt,
            SystemAccessRevokedAt = record.SystemAccessRevokedAt,
            ExitInterviewScheduledAt = record.ExitInterviewScheduledAt,
            ExitInterviewCompletedAt = record.ExitInterviewCompletedAt,
            ExitInterviewNotes = record.ExitInterviewNotes,
            ExitDocumentsGeneratedAt = record.ExitDocumentsGeneratedAt,
        };
    }

    private Guid? CurrentUser() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Escape(string value) => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);
}
