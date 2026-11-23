using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Application.Workflows;
using Motee.Domain.Authorization;
using Motee.Domain.Employees;
using Motee.Domain.Workflows;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Workflows;

// Workflow templates.
//
// Two things the client cannot do, both of which it needs. A task says it is
// assigned to an access level, but nothing tells the builder whether anybody
// holds it — so a workflow can go live with work that lands on nobody. And
// whether a workflow will actually fire is a question about its status and its
// effective date together, which is answered here rather than left to the
// caller to remember.
internal sealed class WorkflowService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IWorkflowService
{
    private readonly TenantCalendar _calendar = new(dbContext, currentTenant, timeProvider);

    public async Task<PagedResult<WorkflowRowDto>> ListAsync(
        WorkflowQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Workflow> workflows = dbContext.Workflows.AsNoTracking();

        if (query.Status is WorkflowStatus status)
        {
            workflows = workflows.Where(workflow => workflow.Status == status);
        }

        if (query.WorkflowKind is WorkflowKind kind)
        {
            workflows = workflows.Where(workflow => workflow.WorkflowKind == kind);
        }

        if (query.TriggerMode is WorkflowTriggerMode mode)
        {
            workflows = workflows.Where(workflow => workflow.TriggerMode == mode);
        }

        if (query.TriggerEvent is WorkflowTriggerEvent triggerEvent)
        {
            workflows = workflows.Where(workflow => workflow.TriggerEvent == triggerEvent);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            workflows = workflows.Where(workflow =>
                workflow.ScopeDepartmentId == departmentId
                || workflow.ScopeKind == WorkflowScopeKind.All);
        }

        if (query.RunnableOnly == true)
        {
            DateOnly today = await _calendar.TodayAsync(cancellationToken);

            workflows = workflows.Where(workflow =>
                workflow.Status == WorkflowStatus.Active
                && (workflow.EffectiveFrom == null || workflow.EffectiveFrom <= today));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            workflows = workflows.Where(workflow =>
                EF.Functions.ILike(workflow.Title, pattern, "\\")
                || (workflow.Description != null
                    && EF.Functions.ILike(workflow.Description, pattern, "\\")));
        }

        int total = await workflows.CountAsync(cancellationToken);

        List<Workflow> page = await workflows
            // The seeded flows first: they are the ones a company recognises.
            .OrderBy(workflow => workflow.WorkflowKind)
            .ThenBy(workflow => workflow.Title)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<WorkflowRowDto>
        {
            Items = await RowsAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<WorkflowDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Workflow? workflow = await dbContext.Workflows
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        return workflow is null ? null : await DescribeAsync(workflow, cancellationToken);
    }

    public async Task<WorkflowSummaryDto> SummaryAsync(
        CancellationToken cancellationToken = default)
    {
        List<Workflow> workflows = await dbContext.Workflows
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        List<RunTask> openTasks = await dbContext.RunTasks
            .AsNoTracking()
            .Where(task => task.State != RunTaskState.Completed
                && task.State != RunTaskState.Skipped)
            .ToListAsync(cancellationToken);

        List<Guid> activeRunIds = await ActiveRunIdsAsync(cancellationToken);

        // Work assigned to an access level nobody in scope holds. The count
        // that tells somebody their workflow will go nowhere.
        int ownerless = 0;

        foreach (WorkflowTask task in await dbContext.WorkflowTasks
            .AsNoTracking()
            .Where(task => task.AssigneeKind == AssigneeKind.AccessLevel)
            .ToListAsync(cancellationToken))
        {
            Workflow? owner = workflows.Find(workflow => workflow.Id == task.WorkflowId);

            if (owner is null || owner.Status == WorkflowStatus.Archived)
            {
                continue;
            }

            if (task.AssigneeAccessLevelId is not Guid accessLevelId)
            {
                ownerless++;
                continue;
            }

            IReadOnlyList<Guid> holders = await AccessLevelHolders.EmployeeIdsAsync(
                dbContext,
                accessLevelId,
                owner.ScopeKind == WorkflowScopeKind.Department
                    ? owner.ScopeDepartmentId
                    : null,
                cancellationToken);

            if (holders.Count == 0)
            {
                ownerless++;
            }
        }

        return new WorkflowSummaryDto
        {
            Total = workflows.Count,
            Draft = workflows.Count(workflow => workflow.Status == WorkflowStatus.Draft),
            Active = workflows.Count(workflow => workflow.Status == WorkflowStatus.Active),
            Archived = workflows.Count(workflow =>
                workflow.Status == WorkflowStatus.Archived),
            NotYetEffective = workflows.Count(workflow =>
                workflow.Status == WorkflowStatus.Active
                && workflow.EffectiveFrom is DateOnly from
                && from > today),
            ActiveRuns = activeRunIds.Count,
            OverdueTasks = openTasks.Count(task =>
                activeRunIds.Contains(task.RunId)
                && task.State != RunTaskState.AwaitingApproval
                && task.DueOn < today
                && RunProgress.Blockers(task, [.. openTasks
                    .Where(other => other.RunId == task.RunId)]).Count == 0),
            TasksWithNoOwner = ownerless,
        };
    }

    public async Task<WorkflowResult> CreateAsync(
        WorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        Workflow workflow = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Title = string.Empty,
            WorkflowKind = WorkflowKind.Custom,
            Version = 1,
            CreatedAt = now,
        };

        Apply(workflow, request, now);

        // A new workflow is a draft unless it was explicitly activated, so
        // nothing goes live the moment it is saved.
        workflow.Status = request.Status ?? WorkflowStatus.Draft;

        WorkflowOutcome references = await ReferencesAsync(request, cancellationToken);

        if (references != WorkflowOutcome.Succeeded)
        {
            return WorkflowResult.Failed(references);
        }

        WorkflowRefusal dependencies = WorkflowRules.DependencyRefusal(Positions(request));

        if (dependencies != WorkflowRefusal.None)
        {
            return WorkflowResult.Refused(dependencies);
        }

        List<WorkflowTask> tasks = Build(workflow, request);

        WorkflowRefusal refusal = WorkflowRules.Refusal(workflow, tasks);

        if (refusal != WorkflowRefusal.None)
        {
            return WorkflowResult.Refused(refusal);
        }

        dbContext.Workflows.Add(workflow);
        dbContext.WorkflowTasks.AddRange(tasks);

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkflowResult.Ok(await DescribeAsync(workflow, cancellationToken));
    }

    public async Task<WorkflowResult> UpdateAsync(
        Guid id,
        WorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        Workflow? workflow = await dbContext.Workflows
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (workflow is null)
        {
            return WorkflowResult.Failed(WorkflowOutcome.NotFound);
        }

        // The seeded flows drive the product's own recruitment and onboarding
        // screens. The client refuses this silently, which reads as a save that
        // did nothing.
        if (!WorkflowRules.CanEdit(workflow))
        {
            return WorkflowResult.Failed(WorkflowOutcome.Seeded);
        }

        WorkflowOutcome references = await ReferencesAsync(request, cancellationToken);

        if (references != WorkflowOutcome.Succeeded)
        {
            return WorkflowResult.Failed(references);
        }

        WorkflowRefusal dependencies = WorkflowRules.DependencyRefusal(Positions(request));

        if (dependencies != WorkflowRefusal.None)
        {
            return WorkflowResult.Refused(dependencies);
        }

        List<WorkflowTask> existing = await dbContext.WorkflowTasks
            .Where(task => task.WorkflowId == id)
            .OrderBy(task => task.Sequence)
            .ToListAsync(cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        Apply(workflow, request, now);

        if (request.Status is WorkflowStatus status)
        {
            workflow.Status = status;
        }

        List<WorkflowTask> replacement = Build(workflow, request);

        WorkflowRefusal refusal = WorkflowRules.Refusal(workflow, replacement);

        if (refusal != WorkflowRefusal.None)
        {
            return WorkflowResult.Refused(refusal);
        }

        // The version tracks what the workflow does, so retitling it or
        // rewording guidance does not invalidate a live run.
        if (WorkflowRules.DiffersMaterially(existing, replacement))
        {
            workflow.Version++;
        }

        dbContext.WorkflowTasks.RemoveRange(existing);
        dbContext.WorkflowTasks.AddRange(replacement);

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkflowResult.Ok(await DescribeAsync(workflow, cancellationToken));
    }

    public async Task<WorkflowResult> SetStatusAsync(
        Guid id,
        WorkflowStatus status,
        CancellationToken cancellationToken = default)
    {
        Workflow? workflow = await dbContext.Workflows
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (workflow is null)
        {
            return WorkflowResult.Failed(WorkflowOutcome.NotFound);
        }

        // Activating and archiving a seeded flow is allowed — a company that
        // does not recruit should be able to switch recruitment off. Rewriting
        // one is not.
        workflow.Status = status;
        workflow.LastModifiedByUserId = UserId();
        workflow.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkflowResult.Ok(await DescribeAsync(workflow, cancellationToken));
    }

    public async Task<WorkflowResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Workflow? workflow = await dbContext.Workflows
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (workflow is null)
        {
            return WorkflowResult.Failed(WorkflowOutcome.NotFound);
        }

        if (!WorkflowRules.CanDelete(workflow))
        {
            return WorkflowResult.Failed(WorkflowOutcome.Seeded);
        }

        // Runs go with it: a run whose template is gone can still be read,
        // because it captured the title and version it was planned against —
        // but there is nothing left to compare it to, and leaving the rows
        // behind means the list counts work against a workflow nobody can open.
        dbContext.Workflows.Remove(workflow);

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkflowResult.Done();
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private void Apply(Workflow workflow, WorkflowRequest request, DateTimeOffset now)
    {
        workflow.Title = request.Title?.Trim() ?? string.Empty;
        workflow.Description = Blank(request.Description);
        workflow.TriggerMode = request.TriggerMode;

        // A manual workflow is started by hand, so a schedule left behind by an
        // earlier edit is cleared rather than kept to confuse the next reader.
        bool automatic = request.TriggerMode == WorkflowTriggerMode.Automatic;

        workflow.ScheduleKind = automatic ? request.ScheduleKind : null;
        workflow.ScheduleOn = automatic ? request.ScheduleOn : null;
        workflow.ScheduleAt = automatic ? request.ScheduleAt : null;
        workflow.TriggerEvent = automatic ? request.TriggerEvent : null;
        workflow.OffsetValue = automatic ? request.OffsetValue : null;
        workflow.OffsetUnit = automatic ? request.OffsetUnit : null;

        workflow.ScopeKind = request.ScopeKind;
        workflow.ScopeDepartmentId = request.ScopeKind == WorkflowScopeKind.Department
            ? request.ScopeDepartmentId
            : null;

        workflow.EffectiveFrom = request.EffectiveFrom;
        workflow.OwnerEmployeeId = request.OwnerEmployeeId;
        workflow.EmploymentType = Blank(request.EmploymentType);
        workflow.LastModifiedByUserId = UserId();
        workflow.UpdatedAt = now;
    }

    private static IReadOnlyList<IReadOnlyList<int>> Positions(WorkflowRequest request) =>
    [
        .. request.Tasks.Select(task =>
            (IReadOnlyList<int>)[.. task.DependsOnPositions ?? []]),
    ];

    // Ids first, so dependencies resolve against the final list.
    private static List<WorkflowTask> Build(Workflow workflow, WorkflowRequest request)
    {
        IReadOnlyList<Guid> ids = [.. request.Tasks.Select(_ => Guid.NewGuid())];

        return
        [
            .. request.Tasks.Select((task, index) => new WorkflowTask
            {
                Id = ids[index],
                TenantId = workflow.TenantId,
                WorkflowId = workflow.Id,
                Sequence = index + 1,
                Title = task.Title?.Trim() ?? string.Empty,
                Description = Blank(task.Description),
                AssigneeKind = task.AssigneeKind,
                AssigneeAccessLevelId = task.AssigneeKind == AssigneeKind.AccessLevel
                    ? task.AssigneeAccessLevelId
                    : null,
                AssigneeEmployeeId = task.AssigneeKind == AssigneeKind.Employee
                    ? task.AssigneeEmployeeId
                    : null,
                HasReviewer = task.HasReviewer,
                ReviewerAccessLevelId = task.HasReviewer
                    ? task.ReviewerAccessLevelId
                    : null,
                DueDayOffset = task.DueDayOffset,
                ExpectedDurationDays = task.ExpectedDurationDays,
                Priority = task.Priority,
                EscalateAfterDays = task.EscalateAfterDays,
                DependsOn =
                [
                    .. (task.DependsOnPositions ?? [])
                        .Where(position => position >= 0 && position < index)
                        .Select(position => ids[position]),
                ],
                ParallelGroup = Blank(task.ParallelGroup),
                Condition = task.Condition,
                ConditionHolds = task.ConditionHolds,
            }),
        ];
    }

    // Everything the request points at has to exist, or the workflow names
    // people and places that do not.
    private async Task<WorkflowOutcome> ReferencesAsync(
        WorkflowRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ScopeKind == WorkflowScopeKind.Department
            && request.ScopeDepartmentId is Guid departmentId
            && !await dbContext.Departments.AnyAsync(
                department => department.Id == departmentId, cancellationToken))
        {
            return WorkflowOutcome.DepartmentNotFound;
        }

        List<Guid> employeeIds =
        [
            .. request.Tasks
                .Where(task => task.AssigneeKind == AssigneeKind.Employee
                    && task.AssigneeEmployeeId is not null)
                .Select(task => task.AssigneeEmployeeId!.Value),
            .. request.OwnerEmployeeId is Guid owner ? (Guid[])[owner] : [],
        ];

        if (employeeIds.Count > 0)
        {
            int found = await dbContext.Employees.CountAsync(
                employee => employeeIds.Distinct().Contains(employee.Id)
                    && employee.Status != EmployeeStatus.Deleted,
                cancellationToken);

            if (found != employeeIds.Distinct().Count())
            {
                return WorkflowOutcome.EmployeeNotFound;
            }
        }

        List<Guid> accessLevelIds =
        [
            .. request.Tasks
                .Where(task => task.AssigneeKind == AssigneeKind.AccessLevel
                    && task.AssigneeAccessLevelId is not null)
                .Select(task => task.AssigneeAccessLevelId!.Value),
            .. request.Tasks
                .Where(task => task.HasReviewer && task.ReviewerAccessLevelId is not null)
                .Select(task => task.ReviewerAccessLevelId!.Value),
        ];

        if (accessLevelIds.Count == 0)
        {
            return WorkflowOutcome.Succeeded;
        }

        List<Guid> distinct = [.. accessLevelIds.Distinct()];

        int levels = await dbContext.AccessLevels.CountAsync(
            level => distinct.Contains(level.Id), cancellationToken);

        return levels == distinct.Count
            ? WorkflowOutcome.Succeeded
            : WorkflowOutcome.AccessLevelNotFound;
    }

    private async Task<List<Guid>> ActiveRunIdsAsync(CancellationToken cancellationToken) =>
        await dbContext.WorkflowRuns
            .AsNoTracking()
            .Where(run => run.CancelledAt == null
                && dbContext.RunTasks.Any(task =>
                    task.RunId == run.Id
                    && task.State != RunTaskState.Completed
                    && task.State != RunTaskState.Skipped))
            .Select(run => run.Id)
            .ToListAsync(cancellationToken);

    private async Task<List<WorkflowRowDto>> RowsAsync(
        IReadOnlyList<Workflow> workflows,
        CancellationToken cancellationToken)
    {
        if (workflows.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. workflows.Select(workflow => workflow.Id)];

        Dictionary<Guid, int> taskCounts = await dbContext.WorkflowTasks
            .AsNoTracking()
            .Where(task => ids.Contains(task.WorkflowId))
            .GroupBy(task => task.WorkflowId)
            .Select(group => new { WorkflowId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.WorkflowId, row => row.Count, cancellationToken);

        List<Guid> activeRunIds = await ActiveRunIdsAsync(cancellationToken);

        Dictionary<Guid, int> activeRuns = await dbContext.WorkflowRuns
            .AsNoTracking()
            .Where(run => ids.Contains(run.WorkflowId) && activeRunIds.Contains(run.Id))
            .GroupBy(run => run.WorkflowId)
            .Select(group => new { WorkflowId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.WorkflowId, row => row.Count, cancellationToken);

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        List<Guid> ownerIds =
        [
            .. workflows
                .Where(workflow => workflow.OwnerEmployeeId is not null)
                .Select(workflow => workflow.OwnerEmployeeId!.Value)
                .Distinct(),
        ];

        Dictionary<Guid, string> owners = ownerIds.Count == 0
            ? []
            : await dbContext.Employees
                .AsNoTracking()
                .Where(employee => ownerIds.Contains(employee.Id))
                .Select(employee => new
                {
                    employee.Id,
                    Name = employee.FirstName + " " + employee.LastName,
                })
                .ToDictionaryAsync(row => row.Id, row => row.Name, cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return
        [
            .. workflows.Select(workflow => new WorkflowRowDto
            {
                Id = workflow.Id,
                Title = workflow.Title,
                Description = workflow.Description,
                TriggerMode = workflow.TriggerMode,
                TriggerEvent = workflow.TriggerEvent,
                ScheduleKind = workflow.ScheduleKind,
                ScheduleOn = workflow.ScheduleOn,
                ScheduleAt = workflow.ScheduleAt,
                OffsetValue = workflow.OffsetValue,
                OffsetUnit = workflow.OffsetUnit,
                ScopeKind = workflow.ScopeKind,
                ScopeDepartmentId = workflow.ScopeDepartmentId,
                ScopeDepartmentName = workflow.ScopeDepartmentId is Guid departmentId
                    ? departments.GetValueOrDefault(departmentId)
                    : null,
                WorkflowKind = workflow.WorkflowKind,
                Status = workflow.Status,
                Version = workflow.Version,
                EffectiveFrom = workflow.EffectiveFrom,
                IsRunnable = WorkflowRules.IsRunnable(workflow, today),
                OwnerEmployeeId = workflow.OwnerEmployeeId,
                OwnerName = workflow.OwnerEmployeeId is Guid ownerId
                    ? owners.GetValueOrDefault(ownerId)
                    : null,
                EmploymentType = workflow.EmploymentType,
                TaskCount = taskCounts.GetValueOrDefault(workflow.Id),
                ActiveRuns = activeRuns.GetValueOrDefault(workflow.Id),
                LastModifiedByUserId = workflow.LastModifiedByUserId,
                UpdatedAt = workflow.UpdatedAt,
            }),
        ];
    }

    private async Task<WorkflowDto> DescribeAsync(
        Workflow workflow,
        CancellationToken cancellationToken)
    {
        List<WorkflowTask> tasks = await dbContext.WorkflowTasks
            .AsNoTracking()
            .Where(task => task.WorkflowId == workflow.Id)
            .OrderBy(task => task.Sequence)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, int> positions = [];

        for (int index = 0; index < tasks.Count; index++)
        {
            positions[tasks[index].Id] = index;
        }

        List<Guid> accessLevelIds =
        [
            .. tasks
                .Where(task => task.AssigneeAccessLevelId is not null)
                .Select(task => task.AssigneeAccessLevelId!.Value)
                .Concat(tasks
                    .Where(task => task.ReviewerAccessLevelId is not null)
                    .Select(task => task.ReviewerAccessLevelId!.Value))
                .Distinct(),
        ];

        Dictionary<Guid, string> levels = accessLevelIds.Count == 0
            ? []
            : await dbContext.AccessLevels
                .AsNoTracking()
                .Where(level => accessLevelIds.Contains(level.Id))
                .ToDictionaryAsync(level => level.Id, level => level.Name, cancellationToken);

        List<Guid> employeeIds =
        [
            .. tasks
                .Where(task => task.AssigneeEmployeeId is not null)
                .Select(task => task.AssigneeEmployeeId!.Value)
                .Distinct(),
        ];

        Guid? scopeDepartmentId = workflow.ScopeKind == WorkflowScopeKind.Department
            ? workflow.ScopeDepartmentId
            : null;

        // Who the work would actually land on today. A builder that cannot say
        // this lets a workflow go live with tasks assigned to nobody.
        //
        // Looked up once per access level rather than once per task: a flow
        // typically assigns a dozen tasks across four or five levels.
        Dictionary<Guid, IReadOnlyList<Guid>> holdersByLevel = [];

        foreach (Guid accessLevelId in tasks
            .Where(task => task.AssigneeKind == AssigneeKind.AccessLevel
                && task.AssigneeAccessLevelId is not null)
            .Select(task => task.AssigneeAccessLevelId!.Value)
            .Distinct())
        {
            IReadOnlyList<Guid> holders = await AccessLevelHolders.EmployeeIdsAsync(
                dbContext, accessLevelId, scopeDepartmentId, cancellationToken);

            holdersByLevel[accessLevelId] = holders;
            employeeIds.AddRange(holders);
        }

        List<Guid> everybody = [.. employeeIds.Distinct()];

        Dictionary<Guid, string> people = everybody.Count == 0
            ? []
            : await dbContext.Employees
                .AsNoTracking()
                .Where(employee => everybody.Contains(employee.Id))
                .Select(employee => new
                {
                    employee.Id,
                    Name = employee.FirstName + " " + employee.LastName,
                })
                .ToDictionaryAsync(row => row.Id, row => row.Name, cancellationToken);

        Dictionary<Guid, IReadOnlyList<string>> wouldLandOn = [];

        foreach (WorkflowTask task in tasks)
        {
            if (task.AssigneeKind != AssigneeKind.AccessLevel
                || task.AssigneeAccessLevelId is not Guid accessLevelId)
            {
                continue;
            }

            wouldLandOn[task.Id] =
            [
                .. holdersByLevel
                    .GetValueOrDefault(accessLevelId, [])
                    .Select(id => people.GetValueOrDefault(id) ?? "Unknown"),
            ];
        }

        return new WorkflowDto
        {
            Workflow = (await RowsAsync([workflow], cancellationToken))[0],
            Tasks =
            [
                .. tasks.Select(task => new WorkflowTaskDto
                {
                    Id = task.Id,
                    Sequence = task.Sequence,
                    Title = task.Title,
                    Description = task.Description,
                    AssigneeKind = task.AssigneeKind,
                    AssigneeAccessLevelId = task.AssigneeAccessLevelId,
                    AssigneeAccessLevelName = task.AssigneeAccessLevelId is Guid levelId
                        ? levels.GetValueOrDefault(levelId)
                        : null,
                    AssigneeEmployeeId = task.AssigneeEmployeeId,
                    AssigneeEmployeeName = task.AssigneeEmployeeId is Guid employeeId
                        ? people.GetValueOrDefault(employeeId)
                        : null,
                    WouldLandOn = wouldLandOn.GetValueOrDefault(task.Id)
                        ?? (task.AssigneeEmployeeId is Guid named
                            ? [people.GetValueOrDefault(named) ?? "Unknown"]
                            : []),
                    HasReviewer = task.HasReviewer,
                    ReviewerAccessLevelId = task.ReviewerAccessLevelId,
                    ReviewerAccessLevelName = task.ReviewerAccessLevelId is Guid reviewerId
                        ? levels.GetValueOrDefault(reviewerId)
                        : null,
                    DueDayOffset = task.DueDayOffset,
                    ExpectedDurationDays = task.ExpectedDurationDays,
                    Priority = task.Priority,
                    EscalateAfterDays = task.EscalateAfterDays,
                    DependsOn = [.. task.DependsOn],
                    DependsOnPositions =
                    [
                        .. task.DependsOn
                            .Where(positions.ContainsKey)
                            .Select(id => positions[id])
                            .Order(),
                    ],
                    ParallelGroup = task.ParallelGroup,
                    Condition = task.Condition,
                    ConditionHolds = task.ConditionHolds,
                }),
            ],
        };
    }

    private static string Escaped(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
