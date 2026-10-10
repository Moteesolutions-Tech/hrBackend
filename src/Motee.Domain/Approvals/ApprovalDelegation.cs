using Motee.Domain.Common;

namespace Motee.Domain.Approvals;

// "I am away between these dates — send my approvals to her."
//
// Self-service, set by the manager themselves. The alternative is what every company
// without it does: work piles up behind somebody on holiday until HR is asked to
// intervene, and the intervention is somebody with admin rights approving things they
// were never meant to see.
//
// Gated on its own dates, not on whether the delegator has leave booked. A manager who
// sets a range means it, and requiring a matching leave request would make the feature
// depend on their having remembered to book — which is exactly the person who forgets.
public class ApprovalDelegation : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Whose approvals are being redirected.
    public Guid DelegatorEmployeeId { get; set; }

    public Guid DelegateEmployeeId { get; set; }

    // Inclusive, both ends. A delegation "from Monday to Friday" covers Friday — the
    // half-open reading would silently leave the last day uncovered, which is the day
    // somebody is travelling back and least likely to be watching a queue.
    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    // Shown wherever the delegate's decision appears. Not decoration: an approval
    // granted by somebody who is not the usual approver needs to explain itself, and
    // "Annual leave" is the explanation.
    public string? Reason { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool Covers(DateOnly date) => date >= StartDate && date <= EndDate;
}

// What a delegation does to a step, recorded on the step itself.
//
// Kept because the delegate did not become anybody's manager — they were temporarily
// authorised, and a year later the difference matters. An approval that reads "approved
// by Cara" with no further explanation, on a step whose rule says "line manager", looks
// like either a bug or a permissions hole.
public sealed record StepDelegation
{
    // Who the step was originally resolved to, before the redirect.
    public required Guid FromEmployeeId { get; init; }

    public required string FromName { get; init; }

    public string? Reason { get; init; }

    public required DateOnly PeriodStart { get; init; }

    public required DateOnly PeriodEnd { get; init; }
}

public static class DelegationRules
{
    // A delegation longer than this is not cover for being away, it is a reassignment of
    // duties — which is an org-chart change, not something to arrange from a side panel.
    public const int MaximumDays = 120;

    public enum Rejection
    {
        None,
        EndBeforeStart,
        TooLong,

        // Delegating to yourself achieves nothing and reads, in the audit trail, as an
        // approval that explained itself with a circular reference.
        Self,

        // Two active delegations covering the same day give the resolver no way to choose,
        // and choosing arbitrarily would be worse than refusing.
        Overlapping,

        UnknownDelegate,

        // The delegate has left. Routing work to them is how a queue ages for a month.
        DelegateInactive,
    }

    public static Rejection Check(DateOnly start, DateOnly end, Guid delegator, Guid delegate_)
    {
        if (end < start)
        {
            return Rejection.EndBeforeStart;
        }

        if (end.DayNumber - start.DayNumber + 1 > MaximumDays)
        {
            return Rejection.TooLong;
        }

        return delegator == delegate_ ? Rejection.Self : Rejection.None;
    }
}
