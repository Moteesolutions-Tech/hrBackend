using Motee.Application.Common;
using Motee.Domain.Leave;

namespace Motee.Application.Leave;

// What everyone has left.
//
// Computed on every read rather than stored, because every input moves — entitlement
// accrues month by month, carried days lapse, and a pending request reserves days it may
// yet give back. A stored balance is correct on the day it is written and drifts from
// then on, invisibly, until somebody is told they have days they do not.
public interface ILeaveBalanceService
{
    // One person, one leave type. The question the request form asks before letting
    // somebody book anything.
    Task<LeaveBalanceDto?> ForAsync(
        Guid employeeId,
        Guid leaveTypeId,
        DateOnly? asAt = null,
        CancellationToken cancellationToken = default);

    // One person, every type they have entitlement to — the employee's own balance
    // screen.
    Task<IReadOnlyList<LeaveBalanceDto>> ForEmployeeAsync(
        Guid employeeId,
        DateOnly? asAt = null,
        CancellationToken cancellationToken = default);

    // Everyone, for the HR balances tab. Paged, because a company of any size has more
    // rows here than anywhere else in the product — one per person per leave type.
    Task<PagedResult<LeaveBalanceDto>> ListAsync(
        LeaveBalanceQuery query,
        CancellationToken cancellationToken = default);

    // HR granting or docking days by hand, with a reason. Kept as its own record rather
    // than an edit to a number, because "why do I have 23 days when the policy says 25"
    // has to be answerable.
    Task<LeaveAdjustmentResult> AdjustAsync(
        LeaveAdjustmentRequest request,
        CancellationToken cancellationToken = default);
}

public enum LeaveAdjustmentOutcome
{
    Succeeded,
    NotFound,
    UnknownLeaveType,

    // Zero days. Not an error worth a stack trace, but not something to write either —
    // a no-op adjustment is a row that explains nothing.
    NoChange,
}

public sealed record LeaveBalanceQuery : PagedQuery
{
    public Guid? DepartmentId { get; init; }

    public Guid? LeaveTypeId { get; init; }

    public string? Search { get; init; }

    // Which leave year to report on. Defaults to the one containing today.
    public DateOnly? AsAt { get; init; }
}

public sealed record LeaveAdjustmentRequest
{
    public required Guid EmployeeId { get; init; }

    public required Guid LeaveTypeId { get; init; }

    // Signed: negative docks days.
    public required decimal Days { get; init; }

    public required string Reason { get; init; }

    public DateOnly? AsAt { get; init; }
}

public sealed record LeaveAdjustmentResult
{
    public required LeaveAdjustmentOutcome Outcome { get; init; }

    public LeaveBalanceDto? Balance { get; init; }

    public bool Succeeded => Outcome == LeaveAdjustmentOutcome.Succeeded;

    public static LeaveAdjustmentResult Failed(LeaveAdjustmentOutcome outcome) =>
        new() { Outcome = outcome };

    public static LeaveAdjustmentResult Ok(LeaveBalanceDto balance) =>
        new() { Outcome = LeaveAdjustmentOutcome.Succeeded, Balance = balance };
}

// Every part shown separately rather than a single number, because "you have 12 days" is
// not answerable when somebody disputes it. Each line here is a thing HR can point at.
public sealed record LeaveBalanceDto
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? DepartmentName { get; init; }

    public required Guid LeaveTypeId { get; init; }

    public required string LeaveTypeName { get; init; }

    // The full-year figure from the policy, before any pro-rating.
    public required decimal Entitlement { get; init; }

    // What has actually been earned by now. Equal to Entitlement for a policy that grants
    // the year up front, less for one that accrues monthly or for a mid-year joiner.
    public required decimal Accrued { get; init; }

    public required decimal CarriedOver { get; init; }

    // Carried days that have not lapsed yet. Diverges from CarriedOver after the expiry
    // date, and that gap is exactly what somebody needs to see in October.
    public required decimal CarriedOverAvailable { get; init; }

    public DateOnly? CarryOverExpiresOn { get; init; }

    public required decimal Used { get; init; }

    public required decimal Pending { get; init; }

    public required decimal Adjustments { get; init; }

    // Accrued + carried + adjustments − used − pending. Can go negative: HR grant leave
    // in advance, and hiding that behind a floor of zero would make the debt invisible.
    public required decimal Available { get; init; }

    public required string LeaveYearLabel { get; init; }

    public required DateOnly LeaveYearStart { get; init; }
}
