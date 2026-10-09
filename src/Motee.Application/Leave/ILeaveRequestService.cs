using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Domain.Leave;

namespace Motee.Application.Leave;

// Booking time off, and the rules that decide whether it can be booked.
//
// The approval itself is not implemented here — it is a chain, and the engine owns it.
// This service checks what the engine cannot know: whether the notice was long enough,
// whether the days exist, and whether the request collides with something.
public interface ILeaveRequestService
{
    Task<PagedResult<LeaveRequestDto>> ListAsync(
        LeaveRequestQuery query,
        CancellationToken cancellationToken = default);

    Task<LeaveRequestDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LeaveRequestResult> SubmitAsync(
        LeaveRequestSubmission request,
        CancellationToken cancellationToken = default);

    // Withdrawing. Allowed after approval as well as before — plans change, and the
    // alternative is somebody's balance permanently down for a holiday they never took.
    Task<LeaveRequestResult> CancelAsync(
        Guid id,
        string? reason = null,
        CancellationToken cancellationToken = default);

    // What a request would cost before anybody commits to it, so the form can show the
    // number as the dates are picked rather than after it is refused.
    Task<LeaveQuoteDto> QuoteAsync(
        LeaveQuoteRequest request,
        CancellationToken cancellationToken = default);
}

public enum LeaveRequestOutcome
{
    Succeeded,
    NotFound,

    // No such leave type here, or it has been deactivated.
    UnknownLeaveType,

    // No policy configured for the type, so there is no entitlement to draw on and no
    // rules to check against.
    NoPolicy,

    // End before start, or a half day claimed across a range.
    InvalidDates,

    // Every day in the range is a weekend or a public holiday, so the request costs
    // nothing and means nothing.
    NoWorkingDays,

    // Less notice than the policy asks for.
    InsufficientNotice,

    // Longer than the policy's maximum unbroken stretch.
    TooLong,

    // More days than are left, counting what is already pending.
    InsufficientBalance,

    // The same person already has leave booked over these dates.
    Overlaps,

    // The dates fall in a period the company does not approve planned leave over. The
    // blackout's own reason is returned with it, because "refused" without "the warehouse
    // is closed to leave over Christmas" sends the person to HR to ask.
    Blackout,

    // Already decided, or already withdrawn.
    NotCancellable,
}

public sealed record LeaveRequestQuery : PagedQuery
{
    public Guid? EmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }

    public Guid? LeaveTypeId { get; init; }

    public LeaveRequestStatus? Status { get; init; }

    // Anything overlapping this window, which is what a calendar asks for — not requests
    // that start inside it, since a fortnight beginning last Friday is still absence
    // during this week.
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public string? Search { get; init; }
}

public sealed record LeaveRequestSubmission
{
    // Whose leave. HR book on behalf of people, so this is not always the caller — and
    // when it is not, the audit trail records both.
    public required Guid EmployeeId { get; init; }

    public required Guid LeaveTypeId { get; init; }

    public required DateOnly StartDate { get; init; }

    public required DateOnly EndDate { get; init; }

    public bool IsHalfDay { get; init; }

    public string? HalfDayPeriod { get; init; }

    public string? Reason { get; init; }

    public string? Notes { get; init; }

    public Guid? ReliefEmployeeId { get; init; }

    // Evidence for the chain — a fit note. Passed through to the approval engine, which
    // enforces whether the workflow requires one.
    public IReadOnlyList<Guid> FileIds { get; init; } = [];
}

public sealed record LeaveQuoteRequest
{
    public required Guid EmployeeId { get; init; }

    public required Guid LeaveTypeId { get; init; }

    public required DateOnly StartDate { get; init; }

    public required DateOnly EndDate { get; init; }

    public bool IsHalfDay { get; init; }
}

// What the form needs to show while somebody is still choosing dates: the cost, what
// they would have left, and anything that would stop it.
public sealed record LeaveQuoteDto
{
    public required decimal TotalDays { get; init; }

    public required decimal AvailableBefore { get; init; }

    public required decimal AvailableAfter { get; init; }

    // The days inside the range that cost nothing — weekends and closures. Listed so the
    // form can explain why ten calendar days cost six, rather than leaving somebody to
    // wonder whether it is broken.
    public required IReadOnlyList<LeaveNonWorkingDayDto> NonWorkingDays { get; init; }

    // Empty when the request would be accepted.
    public required IReadOnlyList<LeaveRequestOutcome> Problems { get; init; }

    public string? Message { get; init; }
}

public sealed record LeaveNonWorkingDayDto
{
    public required DateOnly Date { get; init; }

    // "Weekend", or the holiday's name.
    public required string Reason { get; init; }
}

public sealed record LeaveRequestResult
{
    public required LeaveRequestOutcome Outcome { get; init; }

    public LeaveRequestDto? Request { get; init; }

    // Why, when the outcome alone cannot say it — "You have 3 days left and asked for 5."
    public string? Reason { get; init; }

    public bool Succeeded => Outcome == LeaveRequestOutcome.Succeeded;

    public static LeaveRequestResult Failed(LeaveRequestOutcome outcome, string? reason = null) =>
        new() { Outcome = outcome, Reason = reason };

    public static LeaveRequestResult Ok(LeaveRequestDto request) =>
        new() { Outcome = LeaveRequestOutcome.Succeeded, Request = request };
}

public sealed record LeaveRequestDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? JobTitle { get; init; }

    public string? DepartmentName { get; init; }

    public required Guid LeaveTypeId { get; init; }

    public required string LeaveTypeName { get; init; }

    public required DateOnly StartDate { get; init; }

    public required DateOnly EndDate { get; init; }

    public required decimal TotalDays { get; init; }

    public required bool IsHalfDay { get; init; }

    public string? HalfDayPeriod { get; init; }

    public required LeaveRequestStatus Status { get; init; }

    public string? Reason { get; init; }

    public string? Notes { get; init; }

    public Guid? ReliefEmployeeId { get; init; }

    public string? ReliefEmployeeName { get; init; }

    // The chain, straight from the engine — who it is with, what has been decided, the
    // history and any attachments. Null when no workflow was configured.
    public ApprovalDto? Approval { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    public DateTimeOffset? CancelledAt { get; init; }

    public string? CancellationReason { get; init; }
}
