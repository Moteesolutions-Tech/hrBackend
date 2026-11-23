using Motee.Application.Common;
using Motee.Domain.Workflows;

namespace Motee.Application.Workflows;

// Workflow templates.
//
// A workflow is an ordered list of work for one action. Unlike the approvals
// engine, each task separates the doer from an optional reviewer — approvals
// ask somebody to decide, a workflow asks somebody to do and sometimes another
// to check.
public interface IWorkflowService
{
    Task<PagedResult<WorkflowRowDto>> ListAsync(
        WorkflowQuery query,
        CancellationToken cancellationToken = default);

    Task<WorkflowDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<WorkflowSummaryDto> SummaryAsync(CancellationToken cancellationToken = default);

    Task<WorkflowResult> CreateAsync(
        WorkflowRequest request,
        CancellationToken cancellationToken = default);

    // The seeded flows are read-only: a company cannot rewrite the ones its own
    // recruitment and onboarding screens fire.
    Task<WorkflowResult> UpdateAsync(
        Guid id,
        WorkflowRequest request,
        CancellationToken cancellationToken = default);

    // Activate or archive without going through the whole builder.
    Task<WorkflowResult> SetStatusAsync(
        Guid id,
        WorkflowStatus status,
        CancellationToken cancellationToken = default);

    Task<WorkflowResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public enum WorkflowOutcome
{
    Succeeded,
    NotFound,
    Refused,

    // One of the seeded flows.
    Seeded,

    EmployeeNotFound,
    AccessLevelNotFound,
    DepartmentNotFound,
}

public sealed record WorkflowResult
{
    public required WorkflowOutcome Outcome { get; init; }

    public WorkflowDto? Workflow { get; init; }

    public WorkflowRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == WorkflowOutcome.Succeeded;

    public static WorkflowResult Failed(WorkflowOutcome outcome) => new() { Outcome = outcome };

    public static WorkflowResult Refused(WorkflowRefusal refusal) =>
        new() { Outcome = WorkflowOutcome.Refused, Refusal = refusal };

    public static WorkflowResult Ok(WorkflowDto workflow) =>
        new() { Outcome = WorkflowOutcome.Succeeded, Workflow = workflow };

    public static WorkflowResult Done() => new() { Outcome = WorkflowOutcome.Succeeded };
}

public sealed record WorkflowQuery : PagedQuery
{
    public WorkflowStatus? Status { get; init; }

    public WorkflowKind? WorkflowKind { get; init; }

    public WorkflowTriggerMode? TriggerMode { get; init; }

    public WorkflowTriggerEvent? TriggerEvent { get; init; }

    public Guid? DepartmentId { get; init; }

    // Live and live yet, which is the set the scheduler would actually fire.
    public bool? RunnableOnly { get; init; }

    public string? Search { get; init; }
}

public sealed record WorkflowRowDto
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required WorkflowTriggerMode TriggerMode { get; init; }

    public WorkflowTriggerEvent? TriggerEvent { get; init; }

    public ScheduleKind? ScheduleKind { get; init; }

    public DateOnly? ScheduleOn { get; init; }

    public TimeOnly? ScheduleAt { get; init; }

    public int? OffsetValue { get; init; }

    public ScheduleOffsetUnit? OffsetUnit { get; init; }

    public required WorkflowScopeKind ScopeKind { get; init; }

    public Guid? ScopeDepartmentId { get; init; }

    public string? ScopeDepartmentName { get; init; }

    public required WorkflowKind WorkflowKind { get; init; }

    public required WorkflowStatus Status { get; init; }

    public required int Version { get; init; }

    public DateOnly? EffectiveFrom { get; init; }

    // Live, and live yet. The screen can say "will not fire until the 1st"
    // rather than leaving somebody to work it out.
    public required bool IsRunnable { get; init; }

    public Guid? OwnerEmployeeId { get; init; }

    public string? OwnerName { get; init; }

    public string? EmploymentType { get; init; }

    public required int TaskCount { get; init; }

    // Runs still going, so the screen can warn before an edit.
    public required int ActiveRuns { get; init; }

    public Guid? LastModifiedByUserId { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record WorkflowDto
{
    public required WorkflowRowDto Workflow { get; init; }

    public required IReadOnlyList<WorkflowTaskDto> Tasks { get; init; }
}

public sealed record WorkflowTaskDto
{
    public required Guid Id { get; init; }

    public required int Sequence { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required AssigneeKind AssigneeKind { get; init; }

    public Guid? AssigneeAccessLevelId { get; init; }

    public string? AssigneeAccessLevelName { get; init; }

    public Guid? AssigneeEmployeeId { get; init; }

    public string? AssigneeEmployeeName { get; init; }

    // Who it would actually land on today: the people in scope holding the
    // access level. Empty when nobody does, which is the one thing a builder
    // needs to be told before the workflow goes live.
    public required IReadOnlyList<string> WouldLandOn { get; init; }

    public required bool HasReviewer { get; init; }

    public Guid? ReviewerAccessLevelId { get; init; }

    public string? ReviewerAccessLevelName { get; init; }

    public int? DueDayOffset { get; init; }

    public int? ExpectedDurationDays { get; init; }

    public required WorkflowTaskPriority Priority { get; init; }

    public int? EscalateAfterDays { get; init; }

    public required IReadOnlyList<Guid> DependsOn { get; init; }

    // Positions in the list, from nought — the form the builder submits.
    public required IReadOnlyList<int> DependsOnPositions { get; init; }

    public string? ParallelGroup { get; init; }

    public WorkflowCondition? Condition { get; init; }

    public required bool ConditionHolds { get; init; }
}

public sealed record WorkflowSummaryDto
{
    public required int Total { get; init; }

    public required int Draft { get; init; }

    public required int Active { get; init; }

    public required int Archived { get; init; }

    // Active, but dated for later.
    public required int NotYetEffective { get; init; }

    public required int ActiveRuns { get; init; }

    public required int OverdueTasks { get; init; }

    // Tasks that would land on nobody, because no one in scope holds the access
    // level they are assigned to.
    public required int TasksWithNoOwner { get; init; }
}

public sealed record WorkflowRequest
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public WorkflowTriggerMode TriggerMode { get; init; }

    public ScheduleKind? ScheduleKind { get; init; }

    public DateOnly? ScheduleOn { get; init; }

    public TimeOnly? ScheduleAt { get; init; }

    public WorkflowTriggerEvent? TriggerEvent { get; init; }

    public int? OffsetValue { get; init; }

    public ScheduleOffsetUnit? OffsetUnit { get; init; }

    public WorkflowScopeKind ScopeKind { get; init; }

    public Guid? ScopeDepartmentId { get; init; }

    // Left out, a new workflow is a draft, so nothing goes live the moment it
    // is saved.
    public WorkflowStatus? Status { get; init; }

    public DateOnly? EffectiveFrom { get; init; }

    public Guid? OwnerEmployeeId { get; init; }

    public string? EmploymentType { get; init; }

    public required IReadOnlyList<WorkflowTaskRequest> Tasks { get; init; }
}

public sealed record WorkflowTaskRequest
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public AssigneeKind AssigneeKind { get; init; }

    public Guid? AssigneeAccessLevelId { get; init; }

    public Guid? AssigneeEmployeeId { get; init; }

    public bool HasReviewer { get; init; }

    public Guid? ReviewerAccessLevelId { get; init; }

    public int? DueDayOffset { get; init; }

    public int? ExpectedDurationDays { get; init; }

    public WorkflowTaskPriority Priority { get; init; }

    public int? EscalateAfterDays { get; init; }

    // Positions in this list, from nought, rather than ids: the ids are minted
    // on save, so an id-based dependency would dangle the moment anybody edited
    // the workflow.
    public IReadOnlyList<int>? DependsOnPositions { get; init; }

    public string? ParallelGroup { get; init; }

    public WorkflowCondition? Condition { get; init; }

    // False where the work applies when the fact is absent — "unless a
    // contractor".
    public bool ConditionHolds { get; init; } = true;
}
