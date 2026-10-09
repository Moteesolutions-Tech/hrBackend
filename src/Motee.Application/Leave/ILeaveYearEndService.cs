namespace Motee.Application.Leave;

// Closes a leave year: works out what each person had left, writes forward what policy
// allows, and reports what lapsed.
//
// Runs once a year per company, which is exactly why it must be idempotent. A job that
// fires unattended and cannot safely be re-run is one nobody can retry after a partial
// failure — and the first time anyone looks closely at brought-forward days is when
// somebody disputes them.
public interface ILeaveYearEndService
{
    // Whether today is the first day of a new leave year for this company. The daily job
    // asks before doing anything, because companies do not share a year end.
    Task<bool> IsFirstDayOfYearAsync(
        DateOnly today,
        CancellationToken cancellationToken = default);

    // Carries forward for the year containing the given date. Null means the year
    // containing today.
    //
    // Safe to call twice. Anything already written is left exactly as it was, rather than
    // recalculated: by the time a second run happens somebody may have started spending
    // those days, and moving them underneath a booking is worse than a stale figure.
    Task<LeaveYearEndResult> CloseAsync(
        DateOnly? yearContaining = null,
        CancellationToken cancellationToken = default);

    // The same calculation, writing nothing. HR need the numbers before the year turns:
    // finding out in January that a carry-over cap was wrong for everybody at once is not
    // a recoverable situation.
    Task<LeaveYearEndResult> PreviewAsync(
        DateOnly? yearContaining = null,
        CancellationToken cancellationToken = default);
}

public sealed record LeaveYearEndResult
{
    // The year being closed, not the one being carried into.
    public required string ClosedYearLabel { get; init; }

    public required DateOnly ClosedYearStart { get; init; }

    public required DateOnly NextYearStart { get; init; }

    public required IReadOnlyList<LeaveCarryOverDto> CarriedOver { get; init; }

    // Everything that did not carry, across every type that banks days. The number HR
    // open the screen for: how many days the company is about to lose.
    //
    // Per-occasion entitlements take no part in this. Maternity leave is a ceiling for one
    // occasion, not a bank anybody runs down, and counting it would report hundreds of
    // days lost that nobody was ever going to take.
    public required decimal DaysLapsed { get; init; }

    // This year was already closed, so nothing was written. Distinct from closing a year
    // in which nobody happened to have days left.
    public required bool AlreadyClosed { get; init; }

    public required bool Applied { get; init; }
}

public sealed record LeaveCarryOverDto
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required Guid LeaveTypeId { get; init; }

    public required string LeaveTypeName { get; init; }

    // What they had left before the cap.
    public required decimal Available { get; init; }

    public required decimal Carried { get; init; }

    // The difference, which is the figure somebody will want explained: "you had fifteen
    // left, five carried" is a conversation, and it should not start with a surprise.
    public required decimal Lapsed { get; init; }

    public DateOnly? ExpiresOn { get; init; }
}
