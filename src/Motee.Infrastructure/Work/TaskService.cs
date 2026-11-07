using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Application.Work;
using Motee.Domain.Employees;
using Motee.Domain.Tenants;
using Motee.Domain.Work;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Work;

// Tasks.
//
// Every route is about the signed-in person, resolved from the session rather
// than taken as a parameter — a task list is somebody's own work, and the only
// people who can see one are whoever has to do it and whoever gave it to them.
//
// Three things are derived rather than stored: done (from the completion
// date), which tab a task belongs on (from who wrote it), and whether it is
// overdue (against the company's clock, not the hardcoded date the client
// compares to).
internal sealed class TaskService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    TimeProvider timeProvider) : ITaskService
{
    private string? _zone;

    public async Task<PagedResult<TaskItemDto>> MineAsync(
        TaskQuery query,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return PagedResult<TaskItemDto>.Empty(query.Page, query.PageSize);
        }

        return await PageAsync(
            dbContext.TaskItems.AsNoTracking().Where(task => task.AssigneeEmployeeId == me),
            query,
            me,
            cancellationToken);
    }

    public async Task<PagedResult<TaskItemDto>> AssignedByMeAsync(
        TaskQuery query,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return PagedResult<TaskItemDto>.Empty(query.Page, query.PageSize);
        }

        // What they gave out, which is work for somebody else — their own
        // tasks are on the other list.
        return await PageAsync(
            dbContext.TaskItems
                .AsNoTracking()
                .Where(task => task.CreatedByEmployeeId == me
                    && task.AssigneeEmployeeId != me),
            query,
            me,
            cancellationToken);
    }

    public async Task<TaskTally> SummaryAsync(CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return TaskBoard.Of([]);
        }

        List<TaskItem> rows = await dbContext.TaskItems
            .AsNoTracking()
            .Where(task => task.AssigneeEmployeeId == me)
            .ToListAsync(cancellationToken);

        DateOnly today = await TodayAsync(cancellationToken);

        return TaskBoard.Of(
            [
                .. rows.Select(task => new TaskFacts
                {
                    IsDone = TaskItems.IsDone(task),
                    DueState = TaskItems.DueStateOf(task, today),
                    Source = TaskItems.SourceOf(task, me),
                }),
            ]);
    }

    public async Task<TaskItemDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return null;
        }

        TaskItem? task = await dbContext.TaskItems
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        // Null rather than a refusal: a reader who is neither party has no
        // business knowing the id resolves to anything.
        return task is null || !CanSee(task, me)
            ? null
            : (await DescribeAsync([task], me, cancellationToken))[0];
    }

    public async Task<TaskResult> CreateAsync(
        TaskItemRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return TaskResult.Failed(TaskOutcome.NoEmployeeRecord);
        }

        Guid assigneeId = request.AssigneeEmployeeId ?? me;

        if (assigneeId != me)
        {
            Guid? managerOfAssignee = await dbContext.Employees
                .AsNoTracking()
                .Where(employee => employee.Id == assigneeId
                    && employee.Status != EmployeeStatus.Deleted)
                .Select(employee => employee.ManagerId)
                .FirstOrDefaultAsync(cancellationToken);

            bool employed = await dbContext.Employees.AnyAsync(
                employee => employee.Id == assigneeId
                    && employee.Status != EmployeeStatus.Deleted,
                cancellationToken);

            if (!employed)
            {
                return TaskResult.Failed(TaskOutcome.EmployeeNotFound);
            }

            if (!TaskItems.CanAssign(me, assigneeId, managerOfAssignee))
            {
                return TaskResult.Refused(TaskRefusal.NotYoursToAssign);
            }
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        TaskItem task = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Title = string.Empty,
            AssigneeEmployeeId = assigneeId,
            CreatedByEmployeeId = me,

            // At the end of the assignee's own list.
            Sequence = await dbContext.TaskItems
                .Where(row => row.AssigneeEmployeeId == assigneeId)
                .Select(row => (int?)row.Sequence)
                .MaxAsync(cancellationToken) + 1 ?? 0,
            CreatedAt = now,
        };

        Apply(task, request, now);

        TaskRefusal refusal = TaskItems.Refusal(task);

        if (refusal != TaskRefusal.None)
        {
            return TaskResult.Refused(refusal);
        }

        dbContext.TaskItems.Add(task);

        await dbContext.SaveChangesAsync(cancellationToken);

        return TaskResult.Ok((await DescribeAsync([task], me, cancellationToken))[0]);
    }

    public async Task<TaskResult> UpdateAsync(
        Guid id,
        TaskItemRequest request,
        CancellationToken cancellationToken = default)
    {
        (TaskItem? task, Guid? me, TaskOutcome outcome) = await MineOrRefusedAsync(
            id, cancellationToken);

        if (task is null || me is not Guid viewer)
        {
            return TaskResult.Failed(outcome);
        }

        Apply(task, request, timeProvider.GetUtcNow());

        TaskRefusal refusal = TaskItems.Refusal(task);

        if (refusal != TaskRefusal.None)
        {
            return TaskResult.Refused(refusal);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return TaskResult.Ok((await DescribeAsync([task], viewer, cancellationToken))[0]);
    }

    public async Task<TaskResult> CompleteAsync(
        Guid id,
        DateOnly? completedOn = null,
        CancellationToken cancellationToken = default)
    {
        (TaskItem? task, Guid? me, TaskOutcome outcome) = await MineOrRefusedAsync(
            id, cancellationToken);

        if (task is null || me is not Guid viewer)
        {
            return TaskResult.Failed(outcome);
        }

        task.CompletedOn = completedOn ?? await TodayAsync(cancellationToken);
        task.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return TaskResult.Ok((await DescribeAsync([task], viewer, cancellationToken))[0]);
    }

    public async Task<TaskResult> ReopenAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        (TaskItem? task, Guid? me, TaskOutcome outcome) = await MineOrRefusedAsync(
            id, cancellationToken);

        if (task is null || me is not Guid viewer)
        {
            return TaskResult.Failed(outcome);
        }

        task.CompletedOn = null;
        task.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return TaskResult.Ok((await DescribeAsync([task], viewer, cancellationToken))[0]);
    }

    public async Task<TaskResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        (TaskItem? task, Guid? me, TaskOutcome outcome) = await MineOrRefusedAsync(
            id, cancellationToken);

        if (task is null || me is null)
        {
            return TaskResult.Failed(outcome);
        }

        dbContext.TaskItems.Remove(task);

        await dbContext.SaveChangesAsync(cancellationToken);

        return TaskResult.Done();
    }

    public async Task<TaskResult> ReorderAsync(
        IReadOnlyList<Guid> orderedIds,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return TaskResult.Failed(TaskOutcome.NoEmployeeRecord);
        }

        IReadOnlyDictionary<Guid, int> sequence = TaskItems.SequenceFor(orderedIds);

        List<Guid> ids = [.. sequence.Keys];

        // Narrowed to the caller's own list before anything is written: one
        // stale id in a drag must not throw the reorder away, and it certainly
        // must not move somebody else's task.
        List<TaskItem> mine = await dbContext.TaskItems
            .Where(task => task.AssigneeEmployeeId == me && ids.Contains(task.Id))
            .ToListAsync(cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (TaskItem task in mine)
        {
            task.Sequence = sequence[task.Id];
            task.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return TaskResult.Done();
    }

    // ── shared ──────────────────────────────────────────────────────────────

    // Whoever has to do it, and whoever gave it to them. Nobody else — a task
    // list is somebody's own work.
    private static bool CanSee(TaskItem task, Guid employeeId) =>
        task.AssigneeEmployeeId == employeeId || task.CreatedByEmployeeId == employeeId;

    private async Task<(TaskItem? Task, Guid? Me, TaskOutcome Outcome)> MineOrRefusedAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return (null, null, TaskOutcome.NoEmployeeRecord);
        }

        TaskItem? task = await dbContext.TaskItems
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (task is null)
        {
            return (null, me, TaskOutcome.NotFound);
        }

        // Forbidden rather than NotFound: the id is not the secret here, and
        // somebody following a stale link deserves to be told what happened.
        return CanSee(task, me)
            ? (task, me, TaskOutcome.Succeeded)
            : (null, me, TaskOutcome.Forbidden);
    }

    private async Task<PagedResult<TaskItemDto>> PageAsync(
        IQueryable<TaskItem> tasks,
        TaskQuery query,
        Guid me,
        CancellationToken cancellationToken)
    {
        if (query.Done is bool done)
        {
            tasks = done
                ? tasks.Where(task => task.CompletedOn != null)
                : tasks.Where(task => task.CompletedOn == null);
        }

        if (query.Priority is TaskPriority priority)
        {
            tasks = tasks.Where(task => task.Priority == priority);
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            string category = query.Category.Trim();

            tasks = tasks.Where(task => task.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            tasks = tasks.Where(task =>
                EF.Functions.ILike(task.Title, pattern, "\\")
                || (task.Notes != null && EF.Functions.ILike(task.Notes, pattern, "\\")));
        }

        List<TaskItem> rows = await tasks
            .OrderBy(task => task.Sequence)
            .ThenByDescending(task => task.CreatedAt)
            .ToListAsync(cancellationToken);

        List<TaskItemDto> described = await DescribeAsync(rows, me, cancellationToken);

        // The tab and the due state are derived, so neither filter can run in
        // SQL — which is also why the ordering above is the stored sequence
        // rather than anything computed.
        if (query.Source is TaskSource source)
        {
            described = [.. described.Where(task => task.Source == source)];
        }

        if (query.OverdueOnly == true)
        {
            described = [.. described.Where(task => task.DueState == TaskDueState.Overdue)];
        }

        return new PagedResult<TaskItemDto>
        {
            Items = [.. described.Skip(query.Skip).Take(query.PageSize)],
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = described.Count,
        };
    }

    private async Task<List<TaskItemDto>> DescribeAsync(
        IReadOnlyList<TaskItem> tasks,
        Guid viewerEmployeeId,
        CancellationToken cancellationToken)
    {
        if (tasks.Count == 0)
        {
            return [];
        }

        List<Guid> employeeIds =
        [
            .. tasks
                .SelectMany(task => new[] { task.AssigneeEmployeeId, task.CreatedByEmployeeId })
                .Where(id => id is not null && id != Guid.Empty)
                .Select(id => id!.Value)
                .Distinct(),
        ];

        Dictionary<Guid, string> names = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .ToDictionaryAsync(
                employee => employee.Id,
                employee => employee.FirstName + " " + employee.LastName,
                cancellationToken);

        DateOnly today = await TodayAsync(cancellationToken);

        return
        [
            .. tasks.Select(task => new TaskItemDto
            {
                Id = task.Id,
                Title = task.Title,
                Notes = task.Notes,
                AssigneeEmployeeId = task.AssigneeEmployeeId,
                AssigneeName = names.GetValueOrDefault(task.AssigneeEmployeeId),
                CreatedByEmployeeId = task.CreatedByEmployeeId,
                CreatedByName = task.CreatedByEmployeeId is Guid author
                    ? names.GetValueOrDefault(author)
                    : null,
                Priority = task.Priority,
                Category = task.Category,
                DueOn = task.DueOn,
                CompletedOn = task.CompletedOn,
                Done = TaskItems.IsDone(task),
                DueState = TaskItems.DueStateOf(task, today),
                Source = TaskItems.SourceOf(task, viewerEmployeeId),
                Link = task.Link,
                Sequence = task.Sequence,
                UpdatedAt = task.UpdatedAt,
            }),
        ];
    }

    private static void Apply(TaskItem task, TaskItemRequest request, DateTimeOffset now)
    {
        task.Title = request.Title?.Trim() ?? string.Empty;
        task.Notes = Blank(request.Notes);
        task.Priority = request.Priority;
        task.Category = Blank(request.Category);
        task.DueOn = request.DueOn;
        task.Link = Blank(request.Link);
        task.UpdatedAt = now;
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
}
