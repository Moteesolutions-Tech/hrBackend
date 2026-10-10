namespace Motee.Application.Approvals;

// A manager arranging cover for their own approvals while they are away.
//
// Self-service by design. The alternative is what companies without it do: work piles up
// behind somebody on holiday until HR is asked to step in, and that intervention means
// an administrator approving things they were never meant to see.
public interface IApprovalDelegationService
{
    // The signed-in person's own arrangements, past and present. Theirs to see and theirs
    // to cancel — nobody else's.
    Task<IReadOnlyList<ApprovalDelegationDto>> MineAsync(
        CancellationToken cancellationToken = default);

    // Everything currently in force, for an administrator. A separate call because it
    // answers a different question — "who is covering for whom this week" — and needs the
    // workflows permission rather than being somebody's own panel.
    Task<IReadOnlyList<ApprovalDelegationDto>> ActiveAsync(
        CancellationToken cancellationToken = default);

    Task<ApprovalDelegationResult> CreateAsync(
        ApprovalDelegationRequest request,
        CancellationToken cancellationToken = default);

    // Cancelling, not editing. A delegation that has already redirected decisions should
    // not have its dates rewritten underneath them — the steps it touched record the
    // period they were told about, and changing it afterwards would make those records
    // describe something that never happened.
    Task<ApprovalDelegationOutcome> CancelAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public enum ApprovalDelegationOutcome
{
    Succeeded,
    NotFound,

    // Somebody else's arrangement. Cancelling it is not theirs to do.
    NotTheirs,

    EndBeforeStart,

    // Longer than a few months is a reassignment of duties, which is an org-chart change
    // rather than something to arrange from a side panel.
    TooLong,

    DelegatingToSelf,

    // Two arrangements covering the same day leave the resolver no way to choose.
    Overlapping,

    UnknownDelegate,

    // The delegate has left, or has no account to act with.
    DelegateUnavailable,

    // The caller has no employee record, so there is nothing to delegate from.
    NoEmployeeRecord,
}

public sealed record ApprovalDelegationRequest
{
    public required Guid DelegateEmployeeId { get; init; }

    public required DateOnly StartDate { get; init; }

    public required DateOnly EndDate { get; init; }

    public string? Reason { get; init; }
}

public sealed record ApprovalDelegationResult
{
    public required ApprovalDelegationOutcome Outcome { get; init; }

    public ApprovalDelegationDto? Delegation { get; init; }

    public bool Succeeded => Outcome == ApprovalDelegationOutcome.Succeeded;

    public static ApprovalDelegationResult Failed(ApprovalDelegationOutcome outcome) =>
        new() { Outcome = outcome };

    public static ApprovalDelegationResult Ok(ApprovalDelegationDto delegation) =>
        new() { Outcome = ApprovalDelegationOutcome.Succeeded, Delegation = delegation };
}

public sealed record ApprovalDelegationDto
{
    public required Guid Id { get; init; }

    public required Guid DelegatorEmployeeId { get; init; }

    public required string DelegatorName { get; init; }

    public required Guid DelegateEmployeeId { get; init; }

    public required string DelegateName { get; init; }

    public required DateOnly StartDate { get; init; }

    public required DateOnly EndDate { get; init; }

    public string? Reason { get; init; }

    // In force today. Computed rather than stored, because "active" is a fact about the
    // date and a stored flag would need something to come along and flip it.
    public required bool IsActive { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
