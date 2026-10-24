using Motee.Domain.Common;

namespace Motee.Domain.Attendance;

public enum TimesheetStatus
{
    // The week exists but nobody has handed it in. The state most weeks are in, and
    // the reason a timesheet can be read before one has ever been created.
    Pending,

    Submitted,
    Approved,
    Rejected,
}

// A week, handed in and signed off.
//
// Stores the lifecycle and almost nothing else. Hours, overtime and the daily rows are
// derived from the attendance records underneath, because those already are the truth —
// copying them here would make a second answer that stops agreeing the first time a
// day is corrected.
//
// The exception is the pair captured at approval. What somebody approved is a fact
// about a moment, and a correction landing afterwards must not silently rewrite it:
// without the snapshot, a week signed off at 37 hours quietly becomes 41 and nothing
// on the screen admits the figure moved.
public class Timesheet : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    // Monday. ISO-8601's week start, and the one payroll runs on in both countries.
    public DateOnly WeekStart { get; set; }

    public TimesheetStatus Status { get; set; } = TimesheetStatus.Pending;

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    // Who approved or rejected it. A user rather than an employee: the owner may
    // legitimately be doing it at a company with no HR admin yet, and they are no
    // employee.
    public Guid? ResolvedByUserId { get; set; }

    // Why it came back. Required on rejection — a week returned without a reason is a
    // week the person has to guess about, and they will hand it back unchanged.
    public string? RejectionReason { get; set; }

    // The totals as they stood when it was approved. Null until then.
    public decimal? ApprovedTotalHours { get; set; }

    public decimal? ApprovedOvertimeHours { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum TimesheetAction
{
    Submit,
    Approve,
    Reject,
}

// What a week will accept next.
//
// Written down rather than grown as a chain of ifs, for the same reason the clock's is:
// every refusal here is one somebody will hit, and each needs its own sentence.
public static class TimesheetLifecycle
{
    private static readonly Dictionary<TimesheetStatus, TimesheetAction[]> Allowed = new()
    {
        [TimesheetStatus.Pending] = [TimesheetAction.Submit],

        // Nothing to resubmit while it is with somebody. Approve and reject both
        // belong to whoever is reviewing it.
        [TimesheetStatus.Submitted] = [TimesheetAction.Approve, TimesheetAction.Reject],

        // Rejected goes back round, which is the entire point of rejecting rather than
        // deleting: the person fixes their week and hands it in again.
        [TimesheetStatus.Rejected] = [TimesheetAction.Submit],

        // Terminal. Reopening an approved week is a correction to the days inside it,
        // which has its own path — not a button that quietly unapproves somebody's pay.
        [TimesheetStatus.Approved] = [],
    };

    public static bool Can(TimesheetStatus from, TimesheetAction action) =>
        Allowed[from].Contains(action);

    public static string Refusal(TimesheetStatus from, TimesheetAction action) =>
        (from, action) switch
        {
            (TimesheetStatus.Submitted, TimesheetAction.Submit) =>
                "This week has already been handed in and is waiting to be reviewed.",
            (TimesheetStatus.Approved, TimesheetAction.Submit) =>
                "This week has already been approved.",
            (TimesheetStatus.Approved, _) =>
                "This week has already been approved. Correct the days inside it instead.",
            (TimesheetStatus.Pending, _) => "This week has not been handed in yet.",
            (TimesheetStatus.Rejected, TimesheetAction.Approve) =>
                "This week was sent back and has not been handed in again.",
            (TimesheetStatus.Rejected, TimesheetAction.Reject) =>
                "This week has already been sent back.",
            _ => "That is not a move this timesheet can make.",
        };
}
