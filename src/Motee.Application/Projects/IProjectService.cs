using Motee.Application.Common;
using Motee.Domain.Projects;

namespace Motee.Application.Projects;

// Projects, their work, their dates and their people.
//
// The detail call returns the project with everything derived off it already
// worked out — progress, the critical path, conflicting dates, health, what is
// due next and the money. The screen reads eight panels from one response, and
// none of those figures is stored anywhere.
public interface IProjectService
{
    Task<PagedResult<ProjectRowDto>> ListAsync(
        ProjectQuery query,
        CancellationToken cancellationToken = default);

    Task<ProjectDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // The landing page's figures, including who is committed beyond full time.
    Task<ProjectSummaryDto> SummaryAsync(CancellationToken cancellationToken = default);

    Task<ProjectResult> CreateAsync(
        ProjectRequest request,
        CancellationToken cancellationToken = default);

    Task<ProjectResult> UpdateAsync(
        Guid id,
        ProjectRequest request,
        CancellationToken cancellationToken = default);

    Task<ProjectResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    // ── work ────────────────────────────────────────────────────────────────

    Task<ProjectResult> AddTaskAsync(
        Guid projectId,
        ProjectTaskRequest request,
        CancellationToken cancellationToken = default);

    Task<ProjectResult> UpdateTaskAsync(
        Guid projectId,
        Guid taskId,
        ProjectTaskRequest request,
        CancellationToken cancellationToken = default);

    // The status moved on its own, so progress follows it. Refused while the
    // task is waiting on something else.
    Task<ProjectResult> SetTaskStatusAsync(
        Guid projectId,
        Guid taskId,
        ProjectTaskStatus status,
        CancellationToken cancellationToken = default);

    // Progress moved on its own, so the status follows it.
    Task<ProjectResult> SetTaskProgressAsync(
        Guid projectId,
        Guid taskId,
        int percentComplete,
        CancellationToken cancellationToken = default);

    // Also strips the task out of whatever depended on it: a dependency on a
    // task that is gone would otherwise sit in the data forever.
    Task<ProjectResult> DeleteTaskAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default);

    // ── dates ───────────────────────────────────────────────────────────────

    Task<ProjectResult> AddMilestoneAsync(
        Guid projectId,
        MilestoneRequest request,
        CancellationToken cancellationToken = default);

    Task<ProjectResult> UpdateMilestoneAsync(
        Guid projectId,
        Guid milestoneId,
        MilestoneRequest request,
        CancellationToken cancellationToken = default);

    // Reached or not reached, in one click.
    Task<ProjectResult> ToggleMilestoneAsync(
        Guid projectId,
        Guid milestoneId,
        CancellationToken cancellationToken = default);

    Task<ProjectResult> SetMilestoneApprovalAsync(
        Guid projectId,
        Guid milestoneId,
        bool approved,
        CancellationToken cancellationToken = default);

    Task<ProjectResult> DeleteMilestoneAsync(
        Guid projectId,
        Guid milestoneId,
        CancellationToken cancellationToken = default);

    // ── people ──────────────────────────────────────────────────────────────

    // One row per person per project, so setting it twice moves the figure
    // rather than booking them twice.
    Task<ProjectResult> SetAllocationAsync(
        Guid projectId,
        AllocationRequest request,
        CancellationToken cancellationToken = default);

    Task<ProjectResult> RemoveAllocationAsync(
        Guid projectId,
        Guid employeeId,
        CancellationToken cancellationToken = default);
}

public enum ProjectOutcome
{
    Succeeded,
    NotFound,
    Refused,

    // Another project in the company already uses that code.
    CodeTaken,

    EmployeeNotFound,
}

public sealed record ProjectResult
{
    public required ProjectOutcome Outcome { get; init; }

    public ProjectDto? Project { get; init; }

    public ProjectRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == ProjectOutcome.Succeeded;

    public static ProjectResult Failed(ProjectOutcome outcome) => new() { Outcome = outcome };

    public static ProjectResult Refused(ProjectRefusal refusal) =>
        new() { Outcome = ProjectOutcome.Refused, Refusal = refusal };

    public static ProjectResult Ok(ProjectDto project) =>
        new() { Outcome = ProjectOutcome.Succeeded, Project = project };

    public static ProjectResult Done() => new() { Outcome = ProjectOutcome.Succeeded };
}

public sealed record ProjectQuery : PagedQuery
{
    public ProjectStatus? Status { get; init; }

    public ProjectPriority? Priority { get; init; }

    public Guid? OwnerEmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }

    // Planning and active, for the tab that leads the landing page.
    public bool? OpenOnly { get; init; }

    // Completed and cancelled.
    public bool? ClosedOnly { get; init; }

    // Projects with somebody on them.
    public bool? StaffedOnly { get; init; }

    public bool? BudgetedOnly { get; init; }

    public string? Search { get; init; }
}

public sealed record ProjectRowDto
{
    public required Guid Id { get; init; }

    public required string Code { get; init; }

    public required string Name { get; init; }

    public string? Client { get; init; }

    public required ProjectStatus Status { get; init; }

    public required ProjectPriority Priority { get; init; }

    public Guid? OwnerEmployeeId { get; init; }

    public string? OwnerName { get; init; }

    public Guid? DepartmentId { get; init; }

    public string? DepartmentName { get; init; }

    public required DateOnly StartOn { get; init; }

    public required DateOnly EndOn { get; init; }

    public decimal? Budget { get; init; }

    public string? CostCentreCode { get; init; }

    public required int ProgressPercent { get; init; }

    public required int TaskCount { get; init; }

    public required int CompletedTaskCount { get; init; }

    public required int MilestoneCount { get; init; }

    public required int TeamSize { get; init; }

    // Full-time equivalents: all of one person is 1.0.
    public required decimal TotalAllocationFte { get; init; }

    public required decimal Spend { get; init; }
}

public sealed record ProjectDto
{
    public required ProjectRowDto Project { get; init; }

    public string? Description { get; init; }

    public Guid? CreatedByUserId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required IReadOnlyList<ProjectTaskDto> Tasks { get; init; }

    public required IReadOnlyList<ProjectMilestoneDto> Milestones { get; init; }

    public required IReadOnlyList<ProjectAllocationDto> Allocations { get; init; }

    public required int RiskCount { get; init; }

    public required int OpenRiskCount { get; init; }

    // ── derived ─────────────────────────────────────────────────────────────

    public required int CriticalPathProgressPercent { get; init; }

    public required IReadOnlyList<Guid> CriticalPathTaskIds { get; init; }

    public required IReadOnlyList<ScheduleConflict> ScheduleConflicts { get; init; }

    public required ProjectHealth Health { get; init; }

    public CriticalPathAlert? Alert { get; init; }

    public required IReadOnlyList<NextAction> NextActions { get; init; }

    public required decimal ApprovedHours { get; init; }

    public required decimal CommittedSpend { get; init; }

    public required decimal ForecastFinalCost { get; init; }

    public required decimal RemainingBudget { get; init; }

    // Unrounded, so a real spend that rounds to nought is not shown as
    // nothing spent.
    public required decimal BudgetUtilisationPercent { get; init; }
}

public sealed record ProjectTaskDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required ProjectTaskStatus Status { get; init; }

    public ProjectPriority? Priority { get; init; }

    public required DateOnly StartOn { get; init; }

    public required DateOnly EndOn { get; init; }

    public required int PercentComplete { get; init; }

    public Guid? AssigneeEmployeeId { get; init; }

    public string? AssigneeName { get; init; }

    public required IReadOnlyList<Guid> DependsOn { get; init; }

    public decimal? EstimatedHours { get; init; }

    public string? Phase { get; init; }

    // Derived from the dependency graph, so the screen can withhold a button
    // that would be refused.
    public required bool IsBlocked { get; init; }

    public required IReadOnlyList<string> WaitingOn { get; init; }

    public required bool IsOnCriticalPath { get; init; }

    public required bool IsOverdue { get; init; }

    public required decimal ApprovedHours { get; init; }
}

public sealed record ProjectMilestoneDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required DateOnly TargetOn { get; init; }

    public DateOnly? ActualOn { get; init; }

    public required MilestoneStatus Status { get; init; }

    public required int PercentComplete { get; init; }

    // Derived from the status rather than stored beside it.
    public required bool IsReached { get; init; }

    public required bool IsOverdue { get; init; }

    public Guid? ResponsibleEmployeeId { get; init; }

    public string? ResponsibleName { get; init; }

    public required IReadOnlyList<Guid> TaskIds { get; init; }

    public required IReadOnlyList<Guid> RiskIds { get; init; }

    public required bool IsApproved { get; init; }

    public Guid? ApprovedByUserId { get; init; }

    public DateTimeOffset? ApprovedAt { get; init; }
}

public sealed record ProjectAllocationDto
{
    public required Guid EmployeeId { get; init; }

    public string? EmployeeName { get; init; }

    public string? JobTitle { get; init; }

    public string? DepartmentName { get; init; }

    public required string ProjectRole { get; init; }

    public required decimal AllocationPercent { get; init; }

    public required DateOnly StartOn { get; init; }

    public DateOnly? EndOn { get; init; }

    public decimal? HourlyRate { get; init; }

    public required int TaskCount { get; init; }

    public required decimal ApprovedHours { get; init; }

    // What they are committed to across every project still running, so the
    // team panel can say who is over-booked without a second call.
    public required decimal CommittedPercentEverywhere { get; init; }
}

public sealed record ProjectSummaryDto
{
    public required int TotalProjects { get; init; }

    public required int OpenProjects { get; init; }

    public required int StaffedProjects { get; init; }

    public required int BudgetedProjects { get; init; }

    // Distinct people with a claim on their time by any project still
    // running.
    public required int PeopleAssigned { get; init; }

    public required decimal TotalBudget { get; init; }

    public required decimal TotalSpend { get; init; }

    // Averaged across the open projects, which is what the card says.
    public required int AverageProgressPercent { get; init; }

    public required IReadOnlyList<OverAllocation> OverAllocations { get; init; }
}

public sealed record ProjectRequest
{
    public required string Code { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public ProjectStatus Status { get; init; }

    public ProjectPriority Priority { get; init; }

    public string? Client { get; init; }

    public Guid? OwnerEmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }

    public required DateOnly StartOn { get; init; }

    public required DateOnly EndOn { get; init; }

    public decimal? Budget { get; init; }

    public string? CostCentreCode { get; init; }
}

public sealed record ProjectTaskRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public ProjectTaskStatus Status { get; init; }

    public ProjectPriority? Priority { get; init; }

    public required DateOnly StartOn { get; init; }

    public required DateOnly EndOn { get; init; }

    public int PercentComplete { get; init; }

    public Guid? AssigneeEmployeeId { get; init; }

    public IReadOnlyList<Guid>? DependsOn { get; init; }

    public decimal? EstimatedHours { get; init; }

    public string? Phase { get; init; }
}

public sealed record MilestoneRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required DateOnly TargetOn { get; init; }

    public DateOnly? ActualOn { get; init; }

    public MilestoneStatus Status { get; init; }

    public int PercentComplete { get; init; }

    public Guid? ResponsibleEmployeeId { get; init; }

    public IReadOnlyList<Guid>? TaskIds { get; init; }

    public IReadOnlyList<Guid>? RiskIds { get; init; }
}

public sealed record AllocationRequest
{
    public required Guid EmployeeId { get; init; }

    public string? ProjectRole { get; init; }

    public required decimal AllocationPercent { get; init; }

    public DateOnly? StartOn { get; init; }

    public DateOnly? EndOn { get; init; }

    public decimal? HourlyRate { get; init; }
}
