using Motee.Application.Common;
using Motee.Domain.Offboarding;

namespace Motee.Application.Offboarding;

public interface IOffboardingService
{
    Task<PagedResult<OffboardingListItemDto>> ListAsync(
        OffboardingQuery query,
        CancellationToken cancellationToken = default);

    Task<OffboardingDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<OffboardingStatsDto> StatsAsync(CancellationToken cancellationToken = default);

    Task<OffboardingResult> InitiateAsync(
        InitiateOffboardingRequest request,
        CancellationToken cancellationToken = default);

    Task<OffboardingResult> UpdateAsync(
        Guid id,
        UpdateOffboardingRequest request,
        CancellationToken cancellationToken = default);

    // One entry point for every state change instead of five near-identical methods.
    // The lifecycle decides whether the move is legal; this applies what it means.
    Task<OffboardingResult> ApplyAsync(
        Guid id,
        OffboardingAction action,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<OffboardingResult> CompleteClearanceAsync(
        Guid id,
        Guid itemId,
        string? notes = null,
        CancellationToken cancellationToken = default);

    Task<OffboardingResult> RevokeAccessAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<OffboardingResult> ScheduleExitInterviewAsync(
        Guid id,
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken = default);

    Task<OffboardingResult> CompleteExitInterviewAsync(
        Guid id,
        string? notes,
        CancellationToken cancellationToken = default);

    Task<OffboardingResult> GenerateExitDocumentsAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<OffboardingOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public enum OffboardingOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,

    // Somebody is already leaving. A second live record would give one person two exit
    // dates and two clearance checklists, and payroll would have to guess.
    AlreadyOffboarding,

    // The lifecycle refuses the move: approving twice, completing a refused exit,
    // undoing a departure.
    NotAllowed,

    // Disapproving without saying why.
    ReasonRequired,

    ClearanceItemNotFound,
}

public sealed record OffboardingQuery : PagedQuery
{
    // Absent means every state, which is the "All" tab.
    public OffboardingStatus? Status { get; init; }

    // The tabs group several states — "Approved" covers approved, in progress and
    // completed — so the client sends the set rather than the backend hard-coding tabs
    // it would then have to keep in step with the screen.
    public IReadOnlyList<OffboardingStatus>? Statuses { get; init; }

    public Guid? DepartmentId { get; init; }

    public ExitReason? ExitReason { get; init; }

    public string? Search { get; init; }
}

public sealed record InitiateOffboardingRequest
{
    public required Guid EmployeeId { get; init; }

    public required ExitReason ExitReason { get; init; }

    public required DateOnly LastWorkingDate { get; init; }

    public string? Notes { get; init; }
}

public sealed record UpdateOffboardingRequest
{
    public required ExitReason ExitReason { get; init; }

    public required DateOnly LastWorkingDate { get; init; }

    public string? Notes { get; init; }
}

public sealed record OffboardingResult
{
    public required OffboardingOutcome Outcome { get; init; }

    public OffboardingDto? Record { get; init; }

    public bool Succeeded => Outcome == OffboardingOutcome.Succeeded;

    public static OffboardingResult Failed(OffboardingOutcome outcome) => new() { Outcome = outcome };

    public static OffboardingResult Ok(OffboardingDto record) =>
        new() { Outcome = OffboardingOutcome.Succeeded, Record = record };
}

public sealed record OffboardingListItemDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required string Initials { get; init; }

    public string? JobTitle { get; init; }

    public string? Department { get; init; }

    public required DateOnly LastWorkingDate { get; init; }

    public required ExitReason ExitReason { get; init; }

    public required OffboardingStatus Status { get; init; }

    // Counted, not stored — a stored total is wrong the moment an item is ticked.
    public required int ClearanceCompleted { get; init; }

    public required int ClearanceTotal { get; init; }

    // What the interface may offer from here, straight from the lifecycle. Sent so the
    // client never has to keep its own copy of the rules in step with the backend's.
    public required IReadOnlyList<OffboardingAction> AvailableActions { get; init; }

    public required DateTimeOffset InitiatedAt { get; init; }
}

public sealed record OffboardingDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required string Initials { get; init; }

    public string? JobTitle { get; init; }

    public string? Department { get; init; }

    public required OffboardingStatus Status { get; init; }

    public required ExitReason ExitReason { get; init; }

    public required DateOnly LastWorkingDate { get; init; }

    public string? Notes { get; init; }

    public required IReadOnlyList<ClearanceItemDto> Clearance { get; init; }

    public required IReadOnlyList<OffboardingAction> AvailableActions { get; init; }

    public Guid? InitiatedByUserId { get; init; }

    public required DateTimeOffset InitiatedAt { get; init; }

    public Guid? DecidedByUserId { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    public string? DecisionReason { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public DateTimeOffset? ReactivatedAt { get; init; }

    public DateTimeOffset? SystemAccessRevokedAt { get; init; }

    public DateTimeOffset? ExitInterviewScheduledAt { get; init; }

    public DateTimeOffset? ExitInterviewCompletedAt { get; init; }

    public string? ExitInterviewNotes { get; init; }

    public DateTimeOffset? ExitDocumentsGeneratedAt { get; init; }
}

public sealed record ClearanceItemDto
{
    public required Guid Id { get; init; }

    public required string Label { get; init; }

    public required string Department { get; init; }

    public required int Sequence { get; init; }

    public required bool Completed { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public Guid? CompletedByUserId { get; init; }

    public string? Notes { get; init; }
}

public sealed record OffboardingStatsDto
{
    public required int Total { get; init; }

    public required int Pending { get; init; }

    public required int InProgress { get; init; }

    public required int Completed { get; init; }

    // Live exits with clearance still outstanding — the number HR chases.
    public required int ClearancePending { get; init; }
}
