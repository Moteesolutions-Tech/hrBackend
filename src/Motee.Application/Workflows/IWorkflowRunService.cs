using Motee.Application.Common;
using Motee.Domain.Workflows;

namespace Motee.Application.Workflows;

// Workflows that are actually running.
//
// The run owns assignment, dates and progress; the workflow stays the template.
// Every "who has this?" question reads the run, so editing a template does not
// silently move work somebody has already started.
public interface IWorkflowRunService
{
    Task<PagedResult<RunRowDto>> ListAsync(
        RunQuery query,
        CancellationToken cancellationToken = default);

    Task<RunDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // Everything one person owes, across approvals-style review work and their
    // own tasks. Overdue first, then most urgent, then longest waiting.
    Task<MyWorkDto> MyWorkAsync(CancellationToken cancellationToken = default);

    // Starting the same workflow for the same subject twice is a no-op that
    // hands back the run already there, so a trigger that fires twice does not
    // duplicate somebody's work.
    Task<RunResult> StartAsync(
        StartRunRequest request,
        CancellationToken cancellationToken = default);

    Task<RunResult> StartTaskAsync(
        Guid runId,
        Guid taskId,
        CancellationToken cancellationToken = default);

    // Handing work in. With no reviewer it completes outright — making somebody
    // approve their own work is how a checklist becomes theatre.
    Task<RunResult> SubmitTaskAsync(
        Guid runId,
        Guid taskId,
        string? note,
        CancellationToken cancellationToken = default);

    Task<RunResult> ApproveTaskAsync(
        Guid runId,
        Guid taskId,
        string? note,
        CancellationToken cancellationToken = default);

    Task<RunResult> RejectTaskAsync(
        Guid runId,
        Guid taskId,
        string? note,
        CancellationToken cancellationToken = default);

    // It did not apply to this run after all.
    Task<RunResult> SkipTaskAsync(
        Guid runId,
        Guid taskId,
        string? note,
        CancellationToken cancellationToken = default);

    // The first control anybody reaches for when work lands on the wrong desk.
    Task<RunResult> ReassignTaskAsync(
        Guid runId,
        Guid taskId,
        Guid employeeId,
        CancellationToken cancellationToken = default);

    Task<RunResult> CancelAsync(Guid runId, CancellationToken cancellationToken = default);
}

public sealed record RunResult
{
    public required WorkflowOutcome Outcome { get; init; }

    public RunDto? Run { get; init; }

    public WorkflowRefusal Refusal { get; init; }

    // Why a move was turned down, in a sentence somebody can act on.
    public string? Reason { get; init; }

    public bool Succeeded => Outcome == WorkflowOutcome.Succeeded;

    public static RunResult Failed(WorkflowOutcome outcome) => new() { Outcome = outcome };

    public static RunResult Refused(WorkflowRefusal refusal, string? reason = null) =>
        new() { Outcome = WorkflowOutcome.Refused, Refusal = refusal, Reason = reason };

    public static RunResult Ok(RunDto run) =>
        new() { Outcome = WorkflowOutcome.Succeeded, Run = run };
}

public sealed record RunQuery : PagedQuery
{
    public Guid? WorkflowId { get; init; }

    public RunState? State { get; init; }

    public RunSubjectKind? SubjectKind { get; init; }

    public Guid? SubjectId { get; init; }

    public Guid? AssigneeEmployeeId { get; init; }

    // Runs with something past its date.
    public bool? OverdueOnly { get; init; }

    public string? Search { get; init; }
}

public sealed record RunRowDto
{
    public required Guid Id { get; init; }

    public required Guid WorkflowId { get; init; }

    public required string WorkflowTitle { get; init; }

    public required int WorkflowVersion { get; init; }

    public WorkflowTriggerEvent? Trigger { get; init; }

    public required RunSubjectKind SubjectKind { get; init; }

    public required Guid SubjectId { get; init; }

    public required string SubjectName { get; init; }

    public required DateOnly AnchorOn { get; init; }

    public required IReadOnlyList<WorkflowCondition> Context { get; init; }

    // Derived: cancelled is recorded, finished is read off the work.
    public required RunState State { get; init; }

    public required RunTally Tally { get; init; }

    public Guid? StartedByUserId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    // The latest date on anything still outstanding, so a list can be ordered
    // by what is most at risk.
    public DateOnly? NextDueOn { get; init; }
}

public sealed record RunDto
{
    public required RunRowDto Run { get; init; }

    public required IReadOnlyList<RunTaskDto> Tasks { get; init; }
}

public sealed record RunTaskDto
{
    public required Guid Id { get; init; }

    public required Guid TemplateTaskId { get; init; }

    public required int Sequence { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required WorkflowTaskPriority Priority { get; init; }

    // What is stored.
    public required RunTaskState State { get; init; }

    // What a screen shows: the stored state, plus blocked and overdue, which
    // are questions about other rows and about today.
    public required RunTaskStatus Status { get; init; }

    public required IReadOnlyList<string> WaitingOn { get; init; }

    public required int DaysOverdue { get; init; }

    public required bool HasEscalated { get; init; }

    public required AssigneeKind AssigneeKind { get; init; }

    public Guid? AssigneeEmployeeId { get; init; }

    public required string AssigneeName { get; init; }

    public required bool HasReviewer { get; init; }

    public Guid? ReviewerEmployeeId { get; init; }

    public string? ReviewerName { get; init; }

    public required DateOnly DueOn { get; init; }

    public int? ExpectedDurationDays { get; init; }

    public int? EscalateAfterDays { get; init; }

    public required IReadOnlyList<Guid> DependsOn { get; init; }

    public string? ParallelGroup { get; init; }

    public WorkflowCondition? Condition { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public Guid? CompletedByUserId { get; init; }

    public string? Note { get; init; }

    public string? ReassignedFrom { get; init; }

    // Which moves the caller can make, so a screen withholds a button rather
    // than offering one that is refused.
    public required IReadOnlyList<RunTaskAction> Available { get; init; }
}

// One person's work, in one list.
public sealed record MyWorkDto
{
    public Guid? EmployeeId { get; init; }

    // Mine to do.
    public required IReadOnlyList<WorkItemDto> Todo { get; init; }

    // Mine to sign off.
    public required IReadOnlyList<WorkItemDto> Review { get; init; }

    // Work the run is waiting on from somebody else, so a line manager can see
    // what is holding their hire up.
    public required IReadOnlyList<WorkItemDto> Others { get; init; }
}

public sealed record WorkItemDto
{
    public required Guid RunId { get; init; }

    public required Guid TaskId { get; init; }

    public required string Title { get; init; }

    // The workflow and who it is for.
    public required string Subtitle { get; init; }

    public required RunSubjectKind SubjectKind { get; init; }

    public required Guid SubjectId { get; init; }

    public required WorkflowTaskPriority Priority { get; init; }

    public required RunTaskStatus Status { get; init; }

    public required DateOnly DueOn { get; init; }

    public required int DaysOverdue { get; init; }

    public required bool HasEscalated { get; init; }

    public required string Owner { get; init; }
}

public sealed record StartRunRequest
{
    public required Guid WorkflowId { get; init; }

    public required RunSubjectKind SubjectKind { get; init; }

    public required Guid SubjectId { get; init; }

    // Null takes the subject's own name.
    public string? SubjectName { get; init; }

    // Null anchors to today in the company's own time zone.
    public DateOnly? AnchorOn { get; init; }

    // The facts conditional work is judged against.
    public IReadOnlyList<WorkflowCondition>? Context { get; init; }

    // Set when a lifecycle event fired it rather than a person.
    public WorkflowTriggerEvent? Trigger { get; init; }
}
