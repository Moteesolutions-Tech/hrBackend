using Motee.Domain.Common;

namespace Motee.Domain.Leave;

// Where a request stands, from the leave module's point of view.
//
// Deliberately coarser than the approval chain's own status. The chain knows it is
// waiting on the department head at step two; a balance only needs to know whether these
// days are spoken for. Storing the coarse answer is what lets a balance be a sum rather
// than a walk through every chain.
public enum LeaveRequestStatus
{
    // With approvers. The days are reserved: somebody with ten days left and eight
    // awaiting a decision has two to spend, not ten.
    Pending,

    Approved,

    Rejected,

    // Withdrawn — by the employee before it was decided, or by either side afterwards
    // when the trip is called off. Distinct from rejected: nobody refused it.
    Cancelled,
}

public static class LeaveRequestLifecycle
{
    // Days that count against a balance. Pending and approved both do; the difference is
    // only whether they are certain.
    public static bool ReservesDays(LeaveRequestStatus status) =>
        status is LeaveRequestStatus.Pending or LeaveRequestStatus.Approved;

    public static bool IsFinal(LeaveRequestStatus status) =>
        status is LeaveRequestStatus.Rejected or LeaveRequestStatus.Cancelled;

    // Approved leave can still be cancelled — plans change, and the alternative is
    // somebody's balance permanently down for a holiday they did not take.
    //
    // A rejected request cannot: there is nothing to withdraw, and allowing it would let
    // somebody quietly relabel a refusal as a change of mind.
    public static bool CanCancel(LeaveRequestStatus status) =>
        status is LeaveRequestStatus.Pending or LeaveRequestStatus.Approved;
}

public class LeaveRequest : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid LeaveTypeId { get; set; }

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public bool IsHalfDay { get; set; }

    // Which half, when it is one. Informational — it does not change the cost — but a
    // colleague covering needs to know whether somebody is in this morning.
    public string? HalfDayPeriod { get; set; }

    // Worked out at submission from the policy and the company's holidays, then stored.
    //
    // Snapshotted rather than recomputed on read, because the inputs move: a public
    // holiday added later would silently reduce leave somebody already took, and their
    // balance would change with no event to explain it.
    public decimal TotalDays { get; set; }

    // Which leave year these days are charged to. Stored for the same reason — the
    // company's year start can be changed, and leave already taken must not migrate
    // between years when it is.
    public DateOnly LeaveYearStart { get; set; }

    public LeaveRequestStatus Status { get; set; } = LeaveRequestStatus.Pending;

    // The chain deciding it. Null only when no chain was configured, in which case the
    // request is approved on submission — a company with no leave workflow is not a
    // company where leave should be impossible.
    public Guid? ApprovalInstanceId { get; set; }

    // Why they are going. Distinct from Notes, which is what HR write about it.
    public string? Reason { get; set; }

    public string? Notes { get; set; }

    // Nominated cover. Purely informational: the colleague is not asked to accept, and
    // their own leave is not blocked by it — but a manager approving two overlapping
    // absences should be able to see the clash.
    public Guid? ReliefEmployeeId { get; set; }

    public Guid? SubmittedByUserId { get; set; }

    public DateTimeOffset SubmittedAt { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public Guid? CancelledByUserId { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public string? CancellationReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// A correction HR make by hand: days bought out, granted in advance, or docked. Kept as
// its own rows rather than a column on the balance, because "why do I have 23 days when
// the policy says 25" has to be answerable.
public class LeaveAdjustment : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid LeaveTypeId { get; set; }

    public DateOnly LeaveYearStart { get; set; }

    // Signed: negative takes days away. One column rather than a kind plus a magnitude,
    // so summing is arithmetic and cannot be got the wrong way round.
    public decimal Days { get; set; }

    public required string Reason { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

// Days brought forward from last year, worked out once at the year end and then fixed.
//
// Stored rather than recomputed, because the number depends on what the policy said at
// the time — a company that raises its carry-over cap in March must not retroactively
// hand everybody more days for a year that already closed.
public class LeaveCarryOver : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid LeaveTypeId { get; set; }

    // The year the days were carried *into*.
    public DateOnly LeaveYearStart { get; set; }

    public decimal Days { get; set; }

    // When they lapse. Null means they last the year.
    public DateOnly? ExpiresOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
