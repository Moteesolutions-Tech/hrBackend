using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Application.Workflows;
using Motee.Domain.Workflows;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Workflows;

// Workflows that are running.
//
// Nothing here writes a blocked flag or a run status: both are read off the
// task rows on the way out. The client keeps them in the rows and re-derives
// them with a cascade after every change, which is a figure that goes stale and
// a step every future call site has to remember.
internal sealed class WorkflowRunService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IWorkflowRunService
{
    private readonly TenantCalendar _calendar = new(dbContext, currentTenant, timeProvider);

    public async Task<PagedResult<RunRowDto>> ListAsync(
        RunQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<WorkflowRun> runs = dbContext.WorkflowRuns.AsNoTracking();

        if (query.WorkflowId is Guid workflowId)
        {
            runs = runs.Where(run => run.WorkflowId == workflowId);
        }

        if (query.SubjectKind is RunSubjectKind subjectKind)
        {
            runs = runs.Where(run => run.SubjectKind == subjectKind);
        }

        if (query.SubjectId is Guid subjectId)
        {
            runs = runs.Where(run => run.SubjectId == subjectId);
        }

        if (query.AssigneeEmployeeId is Guid assignee)
        {
            runs = runs.Where(run => dbContext.RunTasks.Any(task =>
                task.RunId == run.Id && task.AssigneeEmployeeId == assignee));
        }

        // Cancelled is a column; finished is a question about the work, so it
        // is asked of the tasks rather than of a stored status.
        if (query.State is RunState state)
        {
            runs = state switch
            {
                RunState.Cancelled => runs.Where(run => run.CancelledAt != null),

                RunState.Completed => runs.Where(run =>
                    run.CancelledAt == null
                    && !dbContext.RunTasks.Any(task =>
                        task.RunId == run.Id
                        && task.State != RunTaskState.Completed
                        && task.State != RunTaskState.Skipped)),

                _ => runs.Where(run =>
                    run.CancelledAt == null
                    && dbContext.RunTasks.Any(task =>
                        task.RunId == run.Id
                        && task.State != RunTaskState.Completed
                        && task.State != RunTaskState.Skipped)),
            };
        }

        if (query.OverdueOnly == true)
        {
            DateOnly today = await _calendar.TodayAsync(cancellationToken);

            runs = runs.Where(run =>
                run.CancelledAt == null
                && dbContext.RunTasks.Any(task =>
                    task.RunId == run.Id
                    && task.State != RunTaskState.Completed
                    && task.State != RunTaskState.Skipped
                    && task.State != RunTaskState.AwaitingApproval
                    && task.DueOn < today));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            runs = runs.Where(run =>
                EF.Functions.ILike(run.SubjectName, pattern, "\\")
                || EF.Functions.ILike(run.WorkflowTitle, pattern, "\\"));
        }

        int total = await runs.CountAsync(cancellationToken);

        List<WorkflowRun> page = await runs
            .OrderByDescending(run => run.StartedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<RunRowDto>
        {
            Items = await RowsAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<RunDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        WorkflowRun? run = await dbContext.WorkflowRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        return run is null ? null : await DescribeAsync(run, cancellationToken);
    }

    public async Task<MyWorkDto> MyWorkAsync(CancellationToken cancellationToken = default)
    {
        Guid? me = await currentEmployee.IdAsync(cancellationToken);

        if (me is not Guid employeeId)
        {
            return new MyWorkDto { Todo = [], Review = [], Others = [] };
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        // Only runs still going: finished and cancelled work is not somebody's
        // to do.
        List<WorkflowRun> runs = await dbContext.WorkflowRuns
            .AsNoTracking()
            .Where(run => run.CancelledAt == null)
            .ToListAsync(cancellationToken);

        List<Guid> runIds = [.. runs.Select(run => run.Id)];

        List<RunTask> tasks = await dbContext.RunTasks
            .AsNoTracking()
            .Where(task => runIds.Contains(task.RunId))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, WorkflowRun> byId = [];

        foreach (WorkflowRun run in runs)
        {
            byId[run.Id] = run;
        }

        List<WorkItemDto> todo = [];
        List<WorkItemDto> review = [];
        List<WorkItemDto> others = [];

        foreach (RunTask task in tasks)
        {
            if (RunProgress.IsSettled(task.State))
            {
                continue;
            }

            if (byId.GetValueOrDefault(task.RunId) is not WorkflowRun run)
            {
                continue;
            }

            IReadOnlyList<RunTask> siblings =
                [.. tasks.Where(other => other.RunId == task.RunId)];

            WorkItemDto item = Item(run, task, siblings, today);

            // Work handed in is the reviewer's, not the doer's. It leaves the
            // list of whoever did it rather than sitting there looking
            // outstanding, which is the whole point of having a reviewer.
            if (task.State == RunTaskState.AwaitingApproval)
            {
                if (task.ReviewerEmployeeId == employeeId)
                {
                    review.Add(item);
                }
                else if (task.AssigneeEmployeeId == employeeId)
                {
                    // Theirs to watch rather than to do: they can see it is
                    // waiting without being told to act on it.
                    others.Add(item);
                }

                continue;
            }

            if (task.AssigneeEmployeeId == employeeId)
            {
                todo.Add(item);
            }
            else if (IsInvolved(siblings, employeeId))
            {
                // Somebody else's step on a run this person is also on, so a
                // line manager can see what is holding their hire up.
                others.Add(item);
            }
        }

        return new MyWorkDto
        {
            EmployeeId = employeeId,
            Todo = Ordered(todo),
            Review = Ordered(review),
            Others = Ordered(others),
        };
    }

    public async Task<RunResult> StartAsync(
        StartRunRequest request,
        CancellationToken cancellationToken = default)
    {
        Workflow? workflow = await dbContext.Workflows
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.WorkflowId, cancellationToken);

        if (workflow is null)
        {
            return RunResult.Failed(WorkflowOutcome.NotFound);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        // The status exists so a half-written workflow is not live, and the
        // effective date so a version written for next quarter does not fire
        // today. The client's listener checks neither.
        if (!WorkflowRules.IsRunnable(workflow, today))
        {
            return RunResult.Refused(
                WorkflowRefusal.NotRunnable,
                workflow.Status == WorkflowStatus.Active
                    ? $"This workflow does not take effect until {workflow.EffectiveFrom:d MMMM yyyy}."
                    : "Only an active workflow can be started.");
        }

        // Starting the same workflow for the same subject twice hands back the
        // run already there, so a trigger that fires twice does not duplicate
        // somebody's work.
        WorkflowRun? existing = await dbContext.WorkflowRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.WorkflowId == request.WorkflowId
                    && row.SubjectKind == request.SubjectKind
                    && row.SubjectId == request.SubjectId,
                cancellationToken);

        if (existing is not null)
        {
            return RunResult.Ok(await DescribeAsync(existing, cancellationToken));
        }

        List<WorkflowTask> template = await dbContext.WorkflowTasks
            .AsNoTracking()
            .Where(task => task.WorkflowId == workflow.Id)
            .OrderBy(task => task.Sequence)
            .ToListAsync(cancellationToken);

        if (template.Count == 0)
        {
            return RunResult.Refused(
                WorkflowRefusal.NoTasks, "This workflow has no work in it.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateOnly anchor = request.AnchorOn ?? today;

        IReadOnlyList<WorkflowCondition> context = [.. (request.Context ?? []).Distinct()];

        WorkflowRun run = new()
        {
            Id = Guid.NewGuid(),
            TenantId = workflow.TenantId,
            WorkflowId = workflow.Id,

            // Captured, so a template edit cannot rewrite what this run was
            // planned against.
            WorkflowTitle = workflow.Title,
            WorkflowVersion = workflow.Version,
            Trigger = request.Trigger,
            SubjectKind = request.SubjectKind,
            SubjectId = request.SubjectId,
            SubjectName = await SubjectNameAsync(request, cancellationToken),
            AnchorOn = anchor,
            Context = [.. context],
            StartedByUserId = UserId(),
            StartedAt = now,
            UpdatedAt = now,
        };

        Guid? scopeDepartmentId = workflow.ScopeKind == WorkflowScopeKind.Department
            ? workflow.ScopeDepartmentId
            : null;

        List<RunTask> tasks = await MaterialiseAsync(
            run, workflow, template, context, scopeDepartmentId, now, cancellationToken);

        dbContext.WorkflowRuns.Add(run);
        dbContext.RunTasks.AddRange(tasks);

        await dbContext.SaveChangesAsync(cancellationToken);

        return RunResult.Ok(await DescribeAsync(run, cancellationToken));
    }

    public Task<RunResult> StartTaskAsync(
        Guid runId,
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        MoveAsync(runId, taskId, RunTaskAction.Start, null, cancellationToken);

    public Task<RunResult> SubmitTaskAsync(
        Guid runId,
        Guid taskId,
        string? note,
        CancellationToken cancellationToken = default) =>
        MoveAsync(runId, taskId, RunTaskAction.Submit, note, cancellationToken);

    public Task<RunResult> ApproveTaskAsync(
        Guid runId,
        Guid taskId,
        string? note,
        CancellationToken cancellationToken = default) =>
        MoveAsync(runId, taskId, RunTaskAction.Approve, note, cancellationToken);

    public Task<RunResult> RejectTaskAsync(
        Guid runId,
        Guid taskId,
        string? note,
        CancellationToken cancellationToken = default) =>
        MoveAsync(runId, taskId, RunTaskAction.Reject, note, cancellationToken);

    public Task<RunResult> SkipTaskAsync(
        Guid runId,
        Guid taskId,
        string? note,
        CancellationToken cancellationToken = default) =>
        MoveAsync(runId, taskId, RunTaskAction.Skip, note, cancellationToken);

    public async Task<RunResult> ReassignTaskAsync(
        Guid runId,
        Guid taskId,
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        (WorkflowRun? run, RunTask? task) =
            await FindAsync(runId, taskId, cancellationToken);

        if (run is null || task is null)
        {
            return RunResult.Failed(WorkflowOutcome.NotFound);
        }

        if (run.CancelledAt is not null)
        {
            return RunResult.Refused(
                WorkflowRefusal.NotRunnable, "This run was cancelled.");
        }

        if (!RunTaskLifecycle.Can(task.State, RunTaskAction.Reassign))
        {
            return RunResult.Refused(
                WorkflowRefusal.NotYours,
                RunTaskLifecycle.Refusal(task.State, RunTaskAction.Reassign));
        }

        Domain.Employees.Employee? employee = await dbContext.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Id == employeeId
                    && row.Status != Domain.Employees.EmployeeStatus.Deleted,
                cancellationToken);

        if (employee is null)
        {
            return RunResult.Failed(WorkflowOutcome.EmployeeNotFound);
        }

        // Kept so the move is visible as a change rather than looking like the
        // template always said so.
        task.ReassignedFrom = task.AssigneeName;
        task.AssigneeKind = AssigneeKind.Employee;
        task.AssigneeAccessLevelId = null;
        task.AssigneeEmployeeId = employeeId;
        task.AssigneeName = $"{employee.FirstName} {employee.LastName}";
        task.UpdatedAt = timeProvider.GetUtcNow();

        run.UpdatedAt = task.UpdatedAt;

        await dbContext.SaveChangesAsync(cancellationToken);

        return RunResult.Ok(await DescribeAsync(run, cancellationToken));
    }

    public async Task<RunResult> CancelAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        WorkflowRun? run = await dbContext.WorkflowRuns
            .FirstOrDefaultAsync(row => row.Id == runId, cancellationToken);

        if (run is null)
        {
            return RunResult.Failed(WorkflowOutcome.NotFound);
        }

        if (run.CancelledAt is not null)
        {
            return RunResult.Refused(
                WorkflowRefusal.NotRunnable, "This run has already been cancelled.");
        }

        run.CancelledAt = timeProvider.GetUtcNow();
        run.CancelledByUserId = UserId();
        run.UpdatedAt = run.CancelledAt.Value;

        await dbContext.SaveChangesAsync(cancellationToken);

        return RunResult.Ok(await DescribeAsync(run, cancellationToken));
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private async Task<RunResult> MoveAsync(
        Guid runId,
        Guid taskId,
        RunTaskAction action,
        string? note,
        CancellationToken cancellationToken)
    {
        (WorkflowRun? run, RunTask? task) =
            await FindAsync(runId, taskId, cancellationToken);

        if (run is null || task is null)
        {
            return RunResult.Failed(WorkflowOutcome.NotFound);
        }

        if (run.CancelledAt is not null)
        {
            return RunResult.Refused(
                WorkflowRefusal.NotRunnable, "This run was cancelled.");
        }

        if (!RunTaskLifecycle.Can(task.State, action))
        {
            return RunResult.Refused(
                WorkflowRefusal.NotRunnable, RunTaskLifecycle.Refusal(task.State, action));
        }

        List<RunTask> siblings = await dbContext.RunTasks
            .Where(row => row.RunId == runId)
            .ToListAsync(cancellationToken);

        // Starting or handing in work that is waiting on something else would
        // make the dependency arrows decorative. Skipping is allowed: deciding
        // something does not apply does not need its predecessor finished.
        if (action is RunTaskAction.Start or RunTaskAction.Submit)
        {
            IReadOnlyList<RunTask> blockers = RunProgress.Blockers(task, siblings);

            if (blockers.Count > 0)
            {
                return RunResult.Refused(
                    WorkflowRefusal.NotRunnable,
                    "This is waiting on "
                    + string.Join(", ", blockers.Select(blocker => blocker.Title))
                    + ".");
            }
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        switch (action)
        {
            case RunTaskAction.Start:
                task.State = RunTaskState.InProgress;
                task.StartedAt = now;
                break;

            case RunTaskAction.Submit:
                task.State = RunTaskLifecycle.Submitted(task.HasReviewer);
                task.SubmittedAt = task.HasReviewer ? now : task.SubmittedAt;
                task.StartedAt ??= now;

                if (!task.HasReviewer)
                {
                    task.CompletedAt = now;
                    task.CompletedByUserId = UserId();
                }

                break;

            case RunTaskAction.Approve:
                task.State = RunTaskState.Completed;
                task.CompletedAt = now;
                task.CompletedByUserId = UserId();
                break;

            case RunTaskAction.Reject:
                // Back with the doer, and the submission is cleared so it does
                // not read as still waiting on a reviewer.
                task.State = RunTaskState.InProgress;
                task.SubmittedAt = null;
                break;

            case RunTaskAction.Skip:
                task.State = RunTaskState.Skipped;
                break;

            default:
                return RunResult.Refused(
                    WorkflowRefusal.NotRunnable, "That is not a move this task can make.");
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            task.Note = note.Trim();
        }

        task.UpdatedAt = now;
        run.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return RunResult.Ok(await DescribeAsync(run, cancellationToken));
    }

    private async Task<(WorkflowRun? Run, RunTask? Task)> FindAsync(
        Guid runId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        WorkflowRun? run = await dbContext.WorkflowRuns
            .FirstOrDefaultAsync(row => row.Id == runId, cancellationToken);

        if (run is null)
        {
            return (null, null);
        }

        RunTask? task = await dbContext.RunTasks
            .FirstOrDefaultAsync(
                row => row.Id == taskId && row.RunId == runId, cancellationToken);

        return (run, task);
    }

    // Template tasks become run tasks: the condition is evaluated once, the
    // doer is resolved to a person, and the day offset becomes a real date.
    private async Task<List<RunTask>> MaterialiseAsync(
        WorkflowRun run,
        Workflow workflow,
        IReadOnlyList<WorkflowTask> template,
        IReadOnlyList<WorkflowCondition> context,
        Guid? scopeDepartmentId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, Guid> runTaskIds = [];

        foreach (WorkflowTask task in template)
        {
            runTaskIds[task.Id] = Guid.NewGuid();
        }

        Dictionary<Guid, Resolved> byLevel = [];

        foreach (Guid accessLevelId in template
            .SelectMany(task => (Guid?[])
                [task.AssigneeAccessLevelId, task.ReviewerAccessLevelId])
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct())
        {
            byLevel[accessLevelId] = await ResolveAsync(
                accessLevelId, scopeDepartmentId, cancellationToken);
        }

        Dictionary<Guid, string> named = await NamesAsync(
            [
                .. template
                    .Where(task => task.AssigneeEmployeeId is not null)
                    .Select(task => task.AssigneeEmployeeId!.Value)
                    .Distinct(),
            ],
            cancellationToken);

        List<RunTask> tasks = [];

        foreach (WorkflowTask task in template)
        {
            Resolved assignee = task.AssigneeKind == AssigneeKind.Employee
                ? new Resolved(
                    task.AssigneeEmployeeId,
                    task.AssigneeEmployeeId is Guid id
                        ? named.GetValueOrDefault(id) ?? "Unassigned"
                        : "Unassigned")
                : byLevel.GetValueOrDefault(
                    task.AssigneeAccessLevelId ?? Guid.Empty,
                    new Resolved(null, "Unassigned"));

            Resolved? reviewer = task.HasReviewer
                && task.ReviewerAccessLevelId is Guid reviewerLevelId
                ? byLevel.GetValueOrDefault(
                    reviewerLevelId, new Resolved(null, "Reviewer"))
                : null;

            tasks.Add(new RunTask
            {
                Id = runTaskIds[task.Id],
                TenantId = run.TenantId,
                RunId = run.Id,
                TemplateTaskId = task.Id,
                Sequence = task.Sequence,
                Title = task.Title,
                Description = task.Description,
                Priority = task.Priority,

                // Work that does not apply to this run is settled from the
                // start, so everything waiting on it is free to begin.
                State = WorkflowPlan.Applies(task, context)
                    ? RunTaskState.NotStarted
                    : RunTaskState.Skipped,

                AssigneeKind = task.AssigneeKind,
                AssigneeAccessLevelId = task.AssigneeAccessLevelId,
                AssigneeEmployeeId = assignee.EmployeeId,
                AssigneeName = assignee.Name,
                HasReviewer = task.HasReviewer,
                ReviewerAccessLevelId = task.ReviewerAccessLevelId,
                ReviewerEmployeeId = reviewer?.EmployeeId,
                ReviewerName = reviewer?.Name,
                DueOn = run.AnchorOn.AddDays(task.DueDayOffset ?? 0),
                ExpectedDurationDays = task.ExpectedDurationDays,
                EscalateAfterDays = task.EscalateAfterDays,

                // Ids of the run's own tasks, so a template edit cannot
                // reach into a live run's dependencies.
                DependsOn =
                [
                    .. task.DependsOn
                        .Where(runTaskIds.ContainsKey)
                        .Select(id => runTaskIds[id]),
                ],

                ParallelGroup = task.ParallelGroup,
                Condition = task.Condition,
                UpdatedAt = now,
            });
        }

        return tasks;
    }

    // The first holder in scope. Where nobody holds it, the access level's own
    // name, so the task still says whose job it is rather than going nowhere
    // silently.
    private async Task<Resolved> ResolveAsync(
        Guid accessLevelId,
        Guid? scopeDepartmentId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> holders = await AccessLevelHolders.EmployeeIdsAsync(
            dbContext, accessLevelId, scopeDepartmentId, cancellationToken);

        if (holders.Count > 0)
        {
            Dictionary<Guid, string> names = await NamesAsync(holders, cancellationToken);

            return new Resolved(
                holders[0], names.GetValueOrDefault(holders[0]) ?? "Unassigned");
        }

        string? level = await dbContext.AccessLevels
            .AsNoTracking()
            .Where(row => row.Id == accessLevelId)
            .Select(row => row.Name)
            .FirstOrDefaultAsync(cancellationToken);

        return new Resolved(null, level ?? "Unassigned");
    }

    private async Task<string> SubjectNameAsync(
        StartRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.SubjectName))
        {
            return request.SubjectName.Trim();
        }

        if (request.SubjectKind == RunSubjectKind.Employee)
        {
            Dictionary<Guid, string> named = await NamesAsync(
                [request.SubjectId], cancellationToken);

            return named.GetValueOrDefault(request.SubjectId) ?? "Unknown";
        }

        return request.SubjectKind switch
        {
            RunSubjectKind.Requisition => "Requisition",
            RunSubjectKind.Candidate => "Candidate",
            _ => "New starter",
        };
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(
        IReadOnlyList<Guid> employeeIds,
        CancellationToken cancellationToken) =>
        employeeIds.Count == 0
            ? []
            : await dbContext.Employees
                .AsNoTracking()
                .Where(employee => employeeIds.Contains(employee.Id))
                .Select(employee => new
                {
                    employee.Id,
                    Name = employee.FirstName + " " + employee.LastName,
                })
                .ToDictionaryAsync(row => row.Id, row => row.Name, cancellationToken);

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    // On the run at all — doing a step or reviewing one. Reviewing counts:
    // somebody who signs off the contract has a stake in whether payroll was
    // set up, and a check on assignment alone leaves them unable to see it.
    private static bool IsInvolved(IReadOnlyList<RunTask> tasks, Guid employeeId) =>
        tasks.Any(task =>
            task.AssigneeEmployeeId == employeeId || task.ReviewerEmployeeId == employeeId);

    private static WorkItemDto Item(
        WorkflowRun run,
        RunTask task,
        IReadOnlyList<RunTask> siblings,
        DateOnly today) => new()
        {
            RunId = run.Id,
            TaskId = task.Id,
            Title = task.Title,
            Subtitle = $"{run.WorkflowTitle} · {run.SubjectName}",
            SubjectKind = run.SubjectKind,
            SubjectId = run.SubjectId,
            Priority = task.Priority,
            Status = RunProgress.StatusOf(task, siblings, today),
            DueOn = task.DueOn,
            DaysOverdue = RunProgress.DaysOverdue(task, today),
            HasEscalated = RunProgress.HasEscalated(task, today),
            Owner = task.AssigneeName,
        };

    // Overdue first, then the most urgent, then the longest waiting — the top
    // of the list should always be the thing to do next.
    private static IReadOnlyList<WorkItemDto> Ordered(IReadOnlyList<WorkItemDto> items) =>
    [
        .. items
            .OrderByDescending(item => item.Status == RunTaskStatus.Overdue)
            .ThenByDescending(item => item.Priority)
            .ThenByDescending(item => item.DaysOverdue)
            .ThenBy(item => item.DueOn),
    ];

    private async Task<List<RunRowDto>> RowsAsync(
        IReadOnlyList<WorkflowRun> runs,
        CancellationToken cancellationToken)
    {
        if (runs.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. runs.Select(run => run.Id)];

        List<RunTask> tasks = await dbContext.RunTasks
            .AsNoTracking()
            .Where(task => ids.Contains(task.RunId))
            .ToListAsync(cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return [.. runs.Select(run => Row(run, [.. tasks.Where(task => task.RunId == run.Id)], today))];
    }

    private static RunRowDto Row(
        WorkflowRun run,
        IReadOnlyList<RunTask> tasks,
        DateOnly today) => new()
        {
            Id = run.Id,
            WorkflowId = run.WorkflowId,
            WorkflowTitle = run.WorkflowTitle,
            WorkflowVersion = run.WorkflowVersion,
            Trigger = run.Trigger,
            SubjectKind = run.SubjectKind,
            SubjectId = run.SubjectId,
            SubjectName = run.SubjectName,
            AnchorOn = run.AnchorOn,
            Context = [.. run.Context],
            State = RunProgress.StateOf(tasks, run.CancelledAt),
            Tally = RunProgress.Tally(tasks, today),
            StartedByUserId = run.StartedByUserId,
            StartedAt = run.StartedAt,
            CancelledAt = run.CancelledAt,
            NextDueOn = tasks
                .Where(task => !RunProgress.IsSettled(task.State))
                .Select(task => (DateOnly?)task.DueOn)
                .Order()
                .FirstOrDefault(),
        };

    private async Task<RunDto> DescribeAsync(
        WorkflowRun run,
        CancellationToken cancellationToken)
    {
        List<RunTask> tasks = await dbContext.RunTasks
            .AsNoTracking()
            .Where(task => task.RunId == run.Id)
            .OrderBy(task => task.Sequence)
            .ToListAsync(cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        bool cancelled = run.CancelledAt is not null;

        return new RunDto
        {
            Run = Row(run, tasks, today),
            Tasks =
            [
                .. tasks.Select(task =>
                {
                    IReadOnlyList<RunTask> blockers = RunProgress.Blockers(task, tasks);

                    return new RunTaskDto
                    {
                        Id = task.Id,
                        TemplateTaskId = task.TemplateTaskId,
                        Sequence = task.Sequence,
                        Title = task.Title,
                        Description = task.Description,
                        Priority = task.Priority,
                        State = task.State,
                        Status = RunProgress.StatusOf(task, tasks, today),
                        WaitingOn = [.. blockers.Select(blocker => blocker.Title)],
                        DaysOverdue = RunProgress.DaysOverdue(task, today),
                        HasEscalated = RunProgress.HasEscalated(task, today),
                        AssigneeKind = task.AssigneeKind,
                        AssigneeEmployeeId = task.AssigneeEmployeeId,
                        AssigneeName = task.AssigneeName,
                        HasReviewer = task.HasReviewer,
                        ReviewerEmployeeId = task.ReviewerEmployeeId,
                        ReviewerName = task.ReviewerName,
                        DueOn = task.DueOn,
                        ExpectedDurationDays = task.ExpectedDurationDays,
                        EscalateAfterDays = task.EscalateAfterDays,
                        DependsOn = [.. task.DependsOn],
                        ParallelGroup = task.ParallelGroup,
                        Condition = task.Condition,
                        StartedAt = task.StartedAt,
                        SubmittedAt = task.SubmittedAt,
                        CompletedAt = task.CompletedAt,
                        CompletedByUserId = task.CompletedByUserId,
                        Note = task.Note,
                        ReassignedFrom = task.ReassignedFrom,

                        // So a screen withholds a button rather than offering
                        // one that is refused.
                        Available = cancelled
                            ? []
                            : [
                                .. Enum.GetValues<RunTaskAction>()
                                    .Where(action =>
                                        RunTaskLifecycle.Can(task.State, action)
                                        && (action
                                            is not (RunTaskAction.Start
                                                or RunTaskAction.Submit)
                                            || blockers.Count == 0)),
                            ],
                    };
                }),
            ],
        };
    }

    private static string Escaped(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private sealed record Resolved(Guid? EmployeeId, string Name);
}
