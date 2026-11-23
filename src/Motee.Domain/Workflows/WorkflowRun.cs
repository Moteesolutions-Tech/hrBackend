using Motee.Domain.Common;

namespace Motee.Domain.Workflows;

// What a run is about: the record it was started for.
public enum RunSubjectKind
{
    Requisition,
    Candidate,
    OnboardingRecord,

    // Somebody started it by hand, against a person.
    Employee,
}

public enum RunState
{
    Active,
    Completed,
    Cancelled,
}

// The states a task is actually in.
//
// Blocked and overdue are not here. Blocked is a question about other rows, and
// overdue is a question about today — a stored answer to either is wrong by the
// next morning. See RunTaskStatus.
public enum RunTaskState
{
    NotStarted,
    InProgress,
    AwaitingApproval,
    Completed,

    // It did not apply to this run.
    Skipped,
}

// What a screen shows, which is the stored state plus the two derived ones.
public enum RunTaskStatus
{
    NotStarted,
    InProgress,
    AwaitingApproval,
    Completed,
    Blocked,
    Overdue,
    Skipped,
}

public enum RunTaskAction
{
    Start,
    Submit,
    Approve,
    Reject,
    Skip,
    Reassign,
}

// A workflow that is running, for one subject.
//
// The run owns assignment, dates and progress; the workflow stays the template.
// Every "who has this?" question reads the run, so an edit to the template does
// not silently move work somebody has already started.
public class WorkflowRun : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkflowId { get; set; }

    // Captured, so a template edit cannot rewrite what this run was planned
    // against.
    public required string WorkflowTitle { get; set; }

    public int WorkflowVersion { get; set; }

    // The event that fired it, or null when somebody started it by hand.
    public WorkflowTriggerEvent? Trigger { get; set; }

    public RunSubjectKind SubjectKind { get; set; }

    public Guid SubjectId { get; set; }

    // The person, or the role being filled when no person exists yet.
    public required string SubjectName { get; set; }

    // The day due dates are measured from.
    public DateOnly AnchorOn { get; set; }

    // The facts conditional work was judged against, kept so a run can be
    // explained after the fact.
    public List<WorkflowCondition> Context { get; set; } = [];

    // Cancelling is a decision somebody took, so it is recorded. Active and
    // completed are read off the tasks.
    public DateTimeOffset? CancelledAt { get; set; }

    public Guid? CancelledByUserId { get; set; }

    public Guid? StartedByUserId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One piece of work inside a run.
public class RunTask : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid RunId { get; set; }

    // The template task this came from, so the two can be lined up.
    public Guid TemplateTaskId { get; set; }

    public int Sequence { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public WorkflowTaskPriority Priority { get; set; } = WorkflowTaskPriority.Normal;

    public RunTaskState State { get; set; } = RunTaskState.NotStarted;

    // The template's intent, kept so a reassignment can be seen as a change.
    public AssigneeKind AssigneeKind { get; set; }

    public Guid? AssigneeAccessLevelId { get; set; }

    // The resolved doer. Null when nobody in scope holds the access level, in
    // which case the name below says whose job it is instead.
    public Guid? AssigneeEmployeeId { get; set; }

    // A person's name, or the access level's when nobody holds it — so the task
    // still says something useful rather than going nowhere silently.
    public required string AssigneeName { get; set; }

    public bool HasReviewer { get; set; }

    public Guid? ReviewerAccessLevelId { get; set; }

    public Guid? ReviewerEmployeeId { get; set; }

    public string? ReviewerName { get; set; }

    // A real date, from the run's anchor plus the template's day offset.
    public DateOnly DueOn { get; set; }

    public int? ExpectedDurationDays { get; set; }

    public int? EscalateAfterDays { get; set; }

    // Ids of other run tasks, not template tasks.
    public List<Guid> DependsOn { get; set; } = [];

    public string? ParallelGroup { get; set; }

    public WorkflowCondition? Condition { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? CompletedByUserId { get; set; }

    public string? Note { get; set; }

    // Who it was taken off, set when somebody moved it.
    public string? ReassignedFrom { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record RunTally
{
    public required int Tasks { get; init; }

    public required int Completed { get; init; }

    public required int Skipped { get; init; }

    public required int AwaitingApproval { get; init; }

    public required int Blocked { get; init; }

    public required int Overdue { get; init; }

    // Settled work over all of it.
    public required int PercentComplete { get; init; }
}

// Where a run has got to.
//
// All of this is read off the task rows. The client rewrites a blocked flag and
// a run status into the rows after every change, which is a figure that goes
// stale and a cascade that has to be remembered at every call site.
public static class RunProgress
{
    public static RunTaskStatus StatusOf(
        RunTask task,
        IReadOnlyList<RunTask> tasks,
        DateOnly today)
    {
        if (task.State is RunTaskState.Completed or RunTaskState.Skipped)
        {
            return task.State == RunTaskState.Completed
                ? RunTaskStatus.Completed
                : RunTaskStatus.Skipped;
        }

        // Blocked outranks overdue: whoever holds it cannot start it, so
        // chasing them is the wrong thing for a screen to do.
        if (Blockers(task, tasks).Count > 0)
        {
            return RunTaskStatus.Blocked;
        }

        // Once it is with the reviewer it is no longer the doer's to chase.
        if (task.State == RunTaskState.AwaitingApproval)
        {
            return RunTaskStatus.AwaitingApproval;
        }

        if (task.DueOn < today)
        {
            return RunTaskStatus.Overdue;
        }

        return task.State == RunTaskState.InProgress
            ? RunTaskStatus.InProgress
            : RunTaskStatus.NotStarted;
    }

    // Work this one is waiting on. Skipped counts as settled: if shipping a
    // laptop does not apply to an office-based hire, everything waiting on it
    // would otherwise wait forever.
    public static IReadOnlyList<RunTask> Blockers(
        RunTask task,
        IReadOnlyList<RunTask> tasks)
    {
        if (task.DependsOn.Count == 0)
        {
            return [];
        }

        return
        [
            .. tasks.Where(other =>
                task.DependsOn.Contains(other.Id) && !IsSettled(other.State)),
        ];
    }

    public static int DaysOverdue(RunTask task, DateOnly today) =>
        IsSettled(task.State) || task.DueOn >= today
            ? 0
            : today.DayNumber - task.DueOn.DayNumber;

    // Past due by more than its grace period. Work with no escalation set never
    // escalates, however late it is — somebody chose not to set one.
    public static bool HasEscalated(RunTask task, DateOnly today) =>
        task.EscalateAfterDays is int grace && DaysOverdue(task, today) > grace;

    // Cancelling is recorded and outranks the work; otherwise a run is finished
    // when every task is settled.
    public static RunState StateOf(
        IReadOnlyList<RunTask> tasks,
        DateTimeOffset? cancelledAt)
    {
        if (cancelledAt is not null)
        {
            return RunState.Cancelled;
        }

        return tasks.All(task => IsSettled(task.State))
            ? RunState.Completed
            : RunState.Active;
    }

    public static RunTally Tally(IReadOnlyList<RunTask> tasks, DateOnly today)
    {
        List<RunTaskStatus> statuses = [.. tasks.Select(task => StatusOf(task, tasks, today))];

        int settled = tasks.Count(task => IsSettled(task.State));

        return new RunTally
        {
            Tasks = tasks.Count,
            Completed = tasks.Count(task => task.State == RunTaskState.Completed),
            Skipped = tasks.Count(task => task.State == RunTaskState.Skipped),
            AwaitingApproval = statuses.Count(status =>
                status == RunTaskStatus.AwaitingApproval),
            Blocked = statuses.Count(status => status == RunTaskStatus.Blocked),
            Overdue = statuses.Count(status => status == RunTaskStatus.Overdue),
            PercentComplete = tasks.Count == 0
                ? 0
                : (int)Math.Round(
                    settled / (decimal)tasks.Count * 100m, MidpointRounding.AwayFromZero),
        };
    }

    public static bool IsSettled(RunTaskState state) =>
        state is RunTaskState.Completed or RunTaskState.Skipped;
}

// What a task will accept next.
//
// Written down rather than grown as a chain of ifs, for the same reason the
// timesheet's is: every refusal here is one somebody will hit, and each needs
// its own sentence.
public static class RunTaskLifecycle
{
    private static readonly Dictionary<RunTaskState, RunTaskAction[]> Allowed = new()
    {
        // Handing work in without picking it up first is normal — a two-minute
        // task does not need a start and a finish.
        [RunTaskState.NotStarted] =
            [RunTaskAction.Start, RunTaskAction.Submit, RunTaskAction.Skip,
                RunTaskAction.Reassign],

        [RunTaskState.InProgress] =
            [RunTaskAction.Submit, RunTaskAction.Skip, RunTaskAction.Reassign],

        // With the reviewer. Theirs to sign off or send back; still movable,
        // because a reviewer who has left should not hold it up.
        [RunTaskState.AwaitingApproval] =
            [RunTaskAction.Approve, RunTaskAction.Reject, RunTaskAction.Skip,
                RunTaskAction.Reassign],

        // Terminal. Reopening finished work is a new run, not a button.
        [RunTaskState.Completed] = [],

        [RunTaskState.Skipped] = [],
    };

    public static bool Can(RunTaskState from, RunTaskAction action) =>
        Allowed[from].Contains(action);

    public static string Refusal(RunTaskState from, RunTaskAction action) =>
        (from, action) switch
        {
            (RunTaskState.NotStarted, RunTaskAction.Approve) =>
                "This has not been handed in yet, so there is nothing to sign off.",
            (RunTaskState.NotStarted, RunTaskAction.Reject) =>
                "This has not been handed in yet, so there is nothing to send back.",

            (RunTaskState.InProgress, RunTaskAction.Start) =>
                "Somebody has already started this.",
            (RunTaskState.InProgress, RunTaskAction.Approve) =>
                "This has not been handed in yet, so there is nothing to sign off.",
            (RunTaskState.InProgress, RunTaskAction.Reject) =>
                "This has not been handed in yet, so there is nothing to send back.",

            (RunTaskState.AwaitingApproval, RunTaskAction.Start) =>
                "This has been handed in and is waiting to be signed off.",
            (RunTaskState.AwaitingApproval, RunTaskAction.Submit) =>
                "This has already been handed in and is waiting to be signed off.",

            (RunTaskState.Completed, RunTaskAction.Approve) =>
                "This has already been signed off.",
            (RunTaskState.Completed, _) =>
                "This has already been signed off. Start a new run to redo it.",

            (RunTaskState.Skipped, _) =>
                "This did not apply to this run, so there is nothing to do.",

            _ => "That is not a move this task can make.",
        };

    // Making somebody approve their own work is how a checklist becomes
    // theatre, so work with no reviewer completes outright.
    public static RunTaskState Submitted(bool hasReviewer) =>
        hasReviewer ? RunTaskState.AwaitingApproval : RunTaskState.Completed;
}

public sealed record WorkflowRunPlan
{
    // Starting now: nothing unmet in front of them, and their condition holds.
    public required IReadOnlyList<WorkflowTask> Activated { get; init; }

    // Applicable, but waiting on something.
    public required IReadOnlyList<WorkflowTask> Blocked { get; init; }

    // Does not apply to this run.
    public required IReadOnlyList<WorkflowTask> Skipped { get; init; }

    // Group name to the work starting together in it.
    public required IReadOnlyDictionary<string, IReadOnlyList<WorkflowTask>> ParallelGroups
    {
        get;
        init;
    }
}

// What happens when a workflow starts.
public static class WorkflowPlan
{
    // Work with no condition always applies. With one, it applies when the
    // fact is present — or when it is absent, where the task is phrased as
    // "unless".
    public static bool Applies(
        WorkflowTask task,
        IReadOnlyList<WorkflowCondition> context) =>
        task.Condition is not WorkflowCondition condition
            || context.Contains(condition) == task.ConditionHolds;

    public static WorkflowRunPlan Of(
        IReadOnlyList<WorkflowTask> tasks,
        IReadOnlyList<WorkflowCondition> context,
        IReadOnlyList<Guid>? settled = null)
    {
        List<WorkflowTask> applicable = [];
        List<WorkflowTask> skipped = [];

        foreach (WorkflowTask task in tasks)
        {
            (Applies(task, context) ? applicable : skipped).Add(task);
        }

        // Work that does not apply counts as satisfied: everything waiting on
        // it would otherwise wait forever.
        HashSet<Guid> met = [.. skipped.Select(task => task.Id), .. settled ?? []];

        List<WorkflowTask> activated = [];
        List<WorkflowTask> blocked = [];

        foreach (WorkflowTask task in applicable)
        {
            if (met.Contains(task.Id))
            {
                continue;
            }

            (task.DependsOn.All(met.Contains) ? activated : blocked).Add(task);
        }

        Dictionary<string, IReadOnlyList<WorkflowTask>> groups = [];

        foreach (IGrouping<string, WorkflowTask> group in activated
            .Where(task => !string.IsNullOrWhiteSpace(task.ParallelGroup))
            .GroupBy(task => task.ParallelGroup!, StringComparer.Ordinal))
        {
            groups[group.Key] = [.. group];
        }

        return new WorkflowRunPlan
        {
            Activated = activated,
            Blocked = blocked,
            Skipped = skipped,
            ParallelGroups = groups,
        };
    }
}
