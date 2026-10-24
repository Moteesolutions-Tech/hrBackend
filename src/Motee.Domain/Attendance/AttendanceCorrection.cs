using Motee.Domain.Common;

namespace Motee.Domain.Attendance;

public enum CorrectionStatus
{
    Pending,
    Approved,
    Rejected,
}

// A claim that a recorded day is wrong.
//
// The employee never edits their own punches. A forgotten clock-out is an assertion
// about what happened, not a fact the person gets to enter — so it carries a reason,
// somebody signs it off, and the original reading survives the change.
//
// That last part is the whole reason this is a record rather than an edit. Amending
// the day in place would leave the corrected time looking exactly like a real punch,
// and nothing to say a human decided it. Anybody auditing a disputed week needs to
// see what the clock said, what was asked for, who agreed, and why.
public class AttendanceCorrection : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    // The day being corrected, rather than the attendance row's id. A day with no
    // punches at all is the commonest thing to correct — somebody who forgot
    // entirely — and there is no row to point at until the correction creates one.
    public DateOnly Date { get; set; }

    public DateTimeOffset? RequestedClockInAt { get; set; }

    public DateTimeOffset? RequestedClockOutAt { get; set; }

    // Required. A request with no reason gives whoever reviews it nothing to decide
    // on, and they will either refuse everything or wave everything through.
    public required string Reason { get; set; }

    public CorrectionStatus Status { get; set; } = CorrectionStatus.Pending;

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    // A user rather than an employee, like every other administrative act here: the
    // owner may be the one doing it at a company with no HR admin yet.
    public Guid? ResolvedByUserId { get; set; }

    public string? ResolutionNote { get; set; }

    // What the clock actually said, captured when the correction was applied.
    //
    // Null until approval, and null afterwards for a day that had no punches — which
    // is itself the record that there was nothing there before.
    public DateTimeOffset? OriginalClockInAt { get; set; }

    public DateTimeOffset? OriginalClockOutAt { get; set; }
}

public static class CorrectionLifecycle
{
    // Pending is the only state that moves. A resolved correction is a decision
    // somebody made on a day that has since been changed by it — reversing that is a
    // new request about the new times, not an edit to the old decision.
    public static bool Can(CorrectionStatus from) => from == CorrectionStatus.Pending;

    public static string Refusal(CorrectionStatus from) => from switch
    {
        CorrectionStatus.Approved =>
            "That correction has already been applied. Raise a new one against the "
            + "corrected times.",
        CorrectionStatus.Rejected => "That correction has already been turned down.",
        _ => "That correction cannot be resolved.",
    };
}
