using Microsoft.EntityFrameworkCore;
using Motee.Application.Checklists;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Domain.Checklists;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Checklists;

// The onboarding checklist.
//
// A starter's checklist is never materialised. It is the active template
// resolved against their start date with completions joined on, so editing the
// template reaches everybody still onboarding and moving a start date moves
// every deadline with it. Rows per task per hire would mean one edit to the
// template forced a decision about hundreds of rows nobody had touched — and
// half of them would be wrong by the time anybody looked.
internal sealed class ChecklistService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IChecklistService
{
    private string? _zone;

    public async Task<IReadOnlyList<ChecklistItemDto>> ListItemsAsync(
        bool includeRetired = true,
        CancellationToken cancellationToken = default)
    {
        List<ChecklistItem> items = await dbContext.ChecklistItems
            .AsNoTracking()
            .Where(item => includeRetired || item.IsActive)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.Title)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(items, cancellationToken);
    }

    public async Task<ChecklistResult> CreateItemAsync(
        ChecklistItemRequest request,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        ChecklistItem item = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Title = string.Empty,

            // At the end of the template, which is where the form adds it.
            Sequence = await dbContext.ChecklistItems
                .Select(row => (int?)row.Sequence)
                .MaxAsync(cancellationToken) + 1 ?? 0,
            CreatedAt = now,
        };

        Apply(item, request, now);

        ChecklistRefusal refusal = ChecklistRules.Refusal(item);

        if (refusal != ChecklistRefusal.None)
        {
            return ChecklistResult.Refused(refusal);
        }

        dbContext.ChecklistItems.Add(item);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ChecklistResult.Ok((await DescribeAsync([item], cancellationToken))[0]);
    }

    public async Task<ChecklistResult> UpdateItemAsync(
        Guid id,
        ChecklistItemRequest request,
        CancellationToken cancellationToken = default)
    {
        ChecklistItem? item = await FindAsync(id, cancellationToken);

        if (item is null)
        {
            return ChecklistResult.Failed(ChecklistOutcome.NotFound);
        }

        Apply(item, request, timeProvider.GetUtcNow());

        ChecklistRefusal refusal = ChecklistRules.Refusal(item);

        if (refusal != ChecklistRefusal.None)
        {
            return ChecklistResult.Refused(refusal);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ChecklistResult.Ok((await DescribeAsync([item], cancellationToken))[0]);
    }

    public async Task<ChecklistResult> MoveItemAsync(
        Guid id,
        bool up,
        CancellationToken cancellationToken = default)
    {
        ChecklistItem? item = await FindAsync(id, cancellationToken);

        if (item is null)
        {
            return ChecklistResult.Failed(ChecklistOutcome.NotFound);
        }

        // Swapped with the neighbour rather than written to an absolute
        // position: two people reordering at once can then only disagree about
        // the order, never leave a gap or a duplicate.
        ChecklistItem? neighbour = up
            ? await dbContext.ChecklistItems
                .Where(row => row.Sequence < item.Sequence)
                .OrderByDescending(row => row.Sequence)
                .FirstOrDefaultAsync(cancellationToken)
            : await dbContext.ChecklistItems
                .Where(row => row.Sequence > item.Sequence)
                .OrderBy(row => row.Sequence)
                .FirstOrDefaultAsync(cancellationToken);

        // Already at the end it was being moved towards, which is not a
        // failure — the arrow is simply a no-op there.
        if (neighbour is null)
        {
            return ChecklistResult.Ok((await DescribeAsync([item], cancellationToken))[0]);
        }

        (item.Sequence, neighbour.Sequence) = (neighbour.Sequence, item.Sequence);

        DateTimeOffset now = timeProvider.GetUtcNow();

        item.UpdatedAt = now;
        neighbour.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return ChecklistResult.Ok((await DescribeAsync([item], cancellationToken))[0]);
    }

    public Task<ChecklistResult> RetireItemAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        SetActiveAsync(id, false, cancellationToken);

    public Task<ChecklistResult> RestoreItemAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        SetActiveAsync(id, true, cancellationToken);

    public async Task<ChecklistResult> DeleteItemAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ChecklistItem? item = await FindAsync(id, cancellationToken);

        if (item is null)
        {
            return ChecklistResult.Failed(ChecklistOutcome.NotFound);
        }

        // Deleting it would take the record of what was asked with it, and
        // somebody's completed onboarding would quietly lose a task.
        bool completed = await dbContext.ChecklistCompletions
            .AnyAsync(row => row.ChecklistItemId == id, cancellationToken);

        if (completed)
        {
            return ChecklistResult.Refused(ChecklistRefusal.InUse);
        }

        dbContext.ChecklistItems.Remove(item);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ChecklistResult.Done();
    }

    // ── starters ────────────────────────────────────────────────────────────

    public async Task<PagedResult<ChecklistHireDto>> HiresAsync(
        ChecklistHireQuery query,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);

        IQueryable<Employee> starters = dbContext.Employees
            .AsNoTracking()
            .Where(employee => EmployeeLifecycle.InPost.Contains(employee.Status));

        if (query.StartedWithinDays is int days)
        {
            DateOnly earliest = today.AddDays(-days);

            // A future start date is in scope: somebody joining next month is
            // exactly who the pre-arrival tasks are for.
            starters = starters.Where(employee =>
                employee.StartDate == null || employee.StartDate >= earliest);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            starters = starters.Where(employee => employee.DepartmentId == departmentId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            starters = starters.Where(employee => EF.Functions.ILike(
                employee.FirstName + " " + employee.LastName, pattern, "\\"));
        }

        List<Starter> rows = await starters
            .OrderBy(employee => employee.StartDate)
            .ThenBy(employee => employee.FirstName)
            .Select(employee => new Starter(
                employee.Id,
                employee.FirstName + " " + employee.LastName,
                employee.JobTitle,
                employee.DepartmentId,
                employee.StartDate))
            .ToListAsync(cancellationToken);

        List<ChecklistItem> template = await ActiveTemplateAsync(cancellationToken);

        Dictionary<Guid, List<ChecklistCompletion>> completions =
            await CompletionsAsync([.. rows.Select(row => row.Id)], cancellationToken);

        Dictionary<Guid, string> departments = await DepartmentsAsync(cancellationToken);

        List<ChecklistHireDto> hires =
        [
            .. rows.Select(row => new ChecklistHireDto
            {
                EmployeeId = row.Id,
                EmployeeName = row.Name,
                JobTitle = row.JobTitle,
                DepartmentName = row.DepartmentId is Guid id
                    ? departments.GetValueOrDefault(id)
                    : null,
                StartDate = row.StartDate,
                Progress = ChecklistRules.Progress(
                    [
                        .. Resolve(
                            template,
                            row.StartDate,
                            completions.GetValueOrDefault(row.Id, []),
                            today)
                            .Select(task => new ChecklistTaskFacts
                            {
                                State = task.State,
                                IsRequired = task.Item.IsRequired,
                            }),
                    ]),
            }),
        ];

        // Progress is derived, so the state filter cannot run in SQL.
        if (query.State is ChecklistProgressState state)
        {
            hires = [.. hires.Where(hire => hire.Progress.State == state)];
        }

        return new PagedResult<ChecklistHireDto>
        {
            Items = [.. hires.Skip(query.Skip).Take(query.PageSize)],
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = hires.Count,
        };
    }

    public async Task<EmployeeChecklistDto?> ForEmployeeAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        Starter? starter = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId
                && employee.Status != EmployeeStatus.Deleted)
            .Select(employee => new Starter(
                employee.Id,
                employee.FirstName + " " + employee.LastName,
                employee.JobTitle,
                employee.DepartmentId,
                employee.StartDate))
            .FirstOrDefaultAsync(cancellationToken);

        if (starter is null)
        {
            return null;
        }

        DateOnly today = await TodayAsync(cancellationToken);

        List<ChecklistItem> template = await ActiveTemplateAsync(cancellationToken);

        List<ChecklistCompletion> done =
            (await CompletionsAsync([employeeId], cancellationToken))
                .GetValueOrDefault(employeeId, []);

        Dictionary<Guid, int> counts = await CompletionCountsAsync(cancellationToken);

        Dictionary<Guid, string> names = await NamesAsync(done, cancellationToken);

        List<ChecklistTaskDto> tasks =
        [
            .. Resolve(template, starter.StartDate, done, today)
                .Select(task => new ChecklistTaskDto
                {
                    Item = Describe(task.Item, counts.GetValueOrDefault(task.Item.Id)),
                    DueOn = task.DueOn,
                    State = task.State,
                    CompletedOn = task.Completion?.CompletedOn,
                    CompletedByName = task.Completion?.CompletedByUserId is Guid userId
                        ? names.GetValueOrDefault(userId)
                        : null,
                    Note = task.Completion?.Note,
                }),
        ];

        Dictionary<Guid, string> departments = await DepartmentsAsync(cancellationToken);

        return new EmployeeChecklistDto
        {
            Hire = new ChecklistHireDto
            {
                EmployeeId = starter.Id,
                EmployeeName = starter.Name,
                JobTitle = starter.JobTitle,
                DepartmentName = starter.DepartmentId is Guid id
                    ? departments.GetValueOrDefault(id)
                    : null,
                StartDate = starter.StartDate,
                Progress = ChecklistRules.Progress(
                    [
                        .. tasks.Select(task => new ChecklistTaskFacts
                        {
                            State = task.State,
                            IsRequired = task.Item.IsRequired,
                        }),
                    ]),
            },
            Tasks = tasks,
        };
    }

    public async Task<EmployeeChecklistDto?> MineAsync(
        CancellationToken cancellationToken = default)
    {
        // Resolved from the session rather than taken as a parameter, so there
        // is nothing to change to read somebody else's.
        Guid? mine = await currentEmployee.IdAsync(cancellationToken);

        return mine is Guid employeeId
            ? await ForEmployeeAsync(employeeId, cancellationToken)
            : null;
    }

    public async Task<ChecklistResult> CompleteAsync(
        Guid employeeId,
        Guid itemId,
        ChecklistCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        bool employed = await dbContext.Employees.AnyAsync(
            employee => employee.Id == employeeId
                && employee.Status != EmployeeStatus.Deleted,
            cancellationToken);

        if (!employed)
        {
            return ChecklistResult.Failed(ChecklistOutcome.EmployeeNotFound);
        }

        if (await FindAsync(itemId, cancellationToken) is null)
        {
            return ChecklistResult.Failed(ChecklistOutcome.NotFound);
        }

        ChecklistCompletion? existing = await dbContext.ChecklistCompletions
            .FirstOrDefaultAsync(
                row => row.EmployeeId == employeeId && row.ChecklistItemId == itemId,
                cancellationToken);

        DateOnly completedOn = request.CompletedOn ?? await TodayAsync(cancellationToken);

        Guid? actor = Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

        // Ticking twice is one completion. Two rows would put the progress
        // count ahead of the number of tasks, and the later note is the one
        // somebody meant.
        if (existing is not null)
        {
            existing.CompletedOn = completedOn;
            existing.Note = Blank(request.Note);
            existing.CompletedByUserId = actor;
        }
        else
        {
            dbContext.ChecklistCompletions.Add(new ChecklistCompletion
            {
                Id = Guid.NewGuid(),
                TenantId = currentTenant.TenantId ?? Guid.Empty,
                EmployeeId = employeeId,
                ChecklistItemId = itemId,
                CompletedOn = completedOn,
                CompletedByUserId = actor,
                Note = Blank(request.Note),
                CreatedAt = timeProvider.GetUtcNow(),
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ChecklistResult.Progressed(
            (await ForEmployeeAsync(employeeId, cancellationToken))!);
    }

    public async Task<ChecklistResult> ClearAsync(
        Guid employeeId,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        ChecklistCompletion? existing = await dbContext.ChecklistCompletions
            .FirstOrDefaultAsync(
                row => row.EmployeeId == employeeId && row.ChecklistItemId == itemId,
                cancellationToken);

        if (existing is not null)
        {
            dbContext.ChecklistCompletions.Remove(existing);

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        EmployeeChecklistDto? checklist = await ForEmployeeAsync(employeeId, cancellationToken);

        // Unticking something that was never ticked is the outcome the caller
        // asked for, so only a missing employee is a failure.
        return checklist is null
            ? ChecklistResult.Failed(ChecklistOutcome.EmployeeNotFound)
            : ChecklistResult.Progressed(checklist);
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private Task<ChecklistItem?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.ChecklistItems.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

    private async Task<ChecklistResult> SetActiveAsync(
        Guid id,
        bool active,
        CancellationToken cancellationToken)
    {
        ChecklistItem? item = await FindAsync(id, cancellationToken);

        if (item is null)
        {
            return ChecklistResult.Failed(ChecklistOutcome.NotFound);
        }

        item.IsActive = active;
        item.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ChecklistResult.Ok((await DescribeAsync([item], cancellationToken))[0]);
    }

    private Task<List<ChecklistItem>> ActiveTemplateAsync(CancellationToken cancellationToken) =>
        dbContext.ChecklistItems
            .AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.Title)
            .ToListAsync(cancellationToken);

    // The resolved checklist: every active task, its deadline against this
    // starter's date, and the completion if there is one.
    private static IEnumerable<ResolvedTask> Resolve(
        IReadOnlyList<ChecklistItem> template,
        DateOnly? startDate,
        IReadOnlyList<ChecklistCompletion> completions,
        DateOnly today)
    {
        Dictionary<Guid, ChecklistCompletion> byItem = completions
            .GroupBy(row => row.ChecklistItemId)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (ChecklistItem item in template)
        {
            ChecklistCompletion? completion = byItem.GetValueOrDefault(item.Id);

            yield return new ResolvedTask(
                item,
                ChecklistRules.DueOn(item, startDate),
                ChecklistRules.StateOf(item, startDate, completion?.CompletedOn, today),
                completion);
        }
    }

    private async Task<Dictionary<Guid, List<ChecklistCompletion>>> CompletionsAsync(
        IReadOnlyList<Guid> employeeIds,
        CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
        {
            return [];
        }

        List<ChecklistCompletion> rows = await dbContext.ChecklistCompletions
            .AsNoTracking()
            .Where(row => employeeIds.Contains(row.EmployeeId))
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.EmployeeId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    // How many starters have completed each task. One grouped count rather
    // than one query per task.
    private async Task<Dictionary<Guid, int>> CompletionCountsAsync(
        CancellationToken cancellationToken) =>
        await dbContext.ChecklistCompletions
            .AsNoTracking()
            .GroupBy(row => row.ChecklistItemId)
            .Select(group => new { ItemId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.ItemId, row => row.Count, cancellationToken);

    private async Task<List<ChecklistItemDto>> DescribeAsync(
        IReadOnlyList<ChecklistItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, int> counts = await CompletionCountsAsync(cancellationToken);

        return [.. items.Select(item => Describe(item, counts.GetValueOrDefault(item.Id)))];
    }

    private static ChecklistItemDto Describe(ChecklistItem item, int completions) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Description = item.Description,
        Category = item.Category,
        ResponsibleParty = item.ResponsibleParty,
        DueDayOffset = item.DueDayOffset,
        IsRequired = item.IsRequired,
        IsActive = item.IsActive,
        Sequence = item.Sequence,
        Completions = completions,

        // Retired instead, so the record of what was asked keeps reading
        // correctly — and the screen can withhold the button rather than
        // offering one that fails.
        CanDelete = completions == 0,
        UpdatedAt = item.UpdatedAt,
    };

    private static void Apply(
        ChecklistItem item,
        ChecklistItemRequest request,
        DateTimeOffset now)
    {
        item.Title = request.Title?.Trim() ?? string.Empty;
        item.Description = Blank(request.Description);
        item.Category = Blank(request.Category);
        item.ResponsibleParty = request.ResponsibleParty;
        item.DueDayOffset = request.DueDayOffset;
        item.IsRequired = request.IsRequired;
        item.UpdatedAt = now;
    }

    private async Task<Dictionary<Guid, string>> DepartmentsAsync(
        CancellationToken cancellationToken) =>
        await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id,
                department => department.Name,
                cancellationToken);

    // Who ticked each task. Users are not tenant-scoped, so this carries its
    // own tenant clause — an unfiltered read would name somebody at another
    // company.
    private async Task<Dictionary<Guid, string>> NamesAsync(
        IReadOnlyList<ChecklistCompletion> completions,
        CancellationToken cancellationToken)
    {
        List<Guid> userIds =
        [
            .. completions
                .Where(row => row.CompletedByUserId is not null)
                .Select(row => row.CompletedByUserId!.Value)
                .Distinct(),
        ];

        if (userIds.Count == 0 || currentTenant.TenantId is not Guid tenantId)
        {
            return [];
        }

        return await dbContext.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id) && user.TenantId == tenantId)
            .ToDictionaryAsync(
                user => user.Id,
                user => (user.FirstName + " " + user.LastName).Trim(),
                cancellationToken);
    }

    private static string Escaped(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        TenantClock.DayOf(timeProvider.GetUtcNow(), await ZoneAsync(cancellationToken));

    private async Task<string?> ZoneAsync(CancellationToken cancellationToken)
    {
        if (_zone is not null)
        {
            return _zone;
        }

        if (currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        _zone = settings?.TimeZoneId;

        return _zone;
    }

    private sealed record Starter(
        Guid Id,
        string Name,
        string? JobTitle,
        Guid? DepartmentId,
        DateOnly? StartDate);

    private sealed record ResolvedTask(
        ChecklistItem Item,
        DateOnly? DueOn,
        ChecklistTaskState State,
        ChecklistCompletion? Completion);
}
