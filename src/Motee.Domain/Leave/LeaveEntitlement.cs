namespace Motee.Domain.Leave;

// What somebody is owed, and what is left.
//
// Kept as arithmetic over inputs rather than a stored number, because every part of it
// moves: entitlement accrues, carried days lapse, pending requests reserve days that may
// yet be refused. A stored balance is right on the day it is written and drifts from
// then on, and the drift is invisible until somebody is told they have days they do not.
public static class LeaveEntitlement
{
    // How much of the year's allowance has been earned by a given date.
    //
    // Monthly rather than daily: staff think in months, payroll runs in months, and
    // accruing 0.0603 days per day produces balances nobody can check by hand. The month
    // somebody is in counts as earned — most companies credit the month you start it.
    public static decimal AccruedBy(
        decimal daysPerYear,
        LeaveYear year,
        DateOnly asAt,
        DateOnly? joinedOn = null)
    {
        // Somebody who joined mid-year earns from their start date, not from the
        // company's year start — otherwise a March joiner would appear to have earned
        // the whole first quarter.
        DateOnly from = joinedOn is DateOnly joined && joined > year.Start ? joined : year.Start;

        if (from > year.End || asAt < from)
        {
            return 0m;
        }

        int monthsInYear = MonthsBetween(year.Start, year.End) + 1;

        // A mid-year joiner is entitled to the part of the year they are here for, not
        // to the whole of it.
        int eligibleMonths = MonthsBetween(from, year.End) + 1;

        decimal fullEntitlement = daysPerYear * eligibleMonths / monthsInYear;

        // Past the year end the whole pro-rata entitlement is earned — but it is the
        // pro-rata one, not the full year's. Short-circuiting to daysPerYear here would
        // hand a July joiner twelve months of leave, and the error would only show up
        // when they tried to take it.
        if (asAt >= year.End)
        {
            return Round(fullEntitlement);
        }

        int earnedMonths = MonthsBetween(from, asAt) + 1;

        return Round(Math.Min(fullEntitlement, fullEntitlement * earnedMonths / eligibleMonths));
    }

    // Days brought forward that have not yet lapsed.
    //
    // Carried days expire partway through the year under most policies. Returning them
    // as still available after that date is how somebody books leave in October against
    // days that ran out in March.
    public static decimal CarriedOverAvailable(
        decimal carriedOver,
        LeaveYear year,
        int expiryMonths,
        DateOnly asAt)
    {
        if (carriedOver <= 0m)
        {
            return 0m;
        }

        if (expiryMonths <= 0)
        {
            return carriedOver;
        }

        return asAt <= year.Start.AddMonths(expiryMonths) ? carriedOver : 0m;
    }

    // What can be booked right now.
    //
    // Pending requests are subtracted as though they were approved. Somebody with ten
    // days left and eight awaiting a decision has two to spend, not ten — the alternative
    // lets them book the same days twice and puts the contradiction in front of an
    // approver rather than the person creating it.
    public static decimal Available(
        decimal entitled,
        decimal carriedOverAvailable,
        decimal used,
        decimal pending,
        decimal adjustments) =>
        Round(entitled + carriedOverAvailable + adjustments - used - pending);

    // What is left to carry at the year end, capped by policy.
    public static decimal CarryForward(
        decimal available,
        bool carryOverAllowed,
        decimal maxCarryOverDays) =>
        !carryOverAllowed || available <= 0m
            ? 0m
            : Round(Math.Min(available, maxCarryOverDays));

    // Half days are the smallest unit anybody books, so balances are held to a half.
    // Rounding to two decimals instead would produce 12.33 days, which is not a thing a
    // person can take.
    private static decimal Round(decimal days) => Math.Round(days * 2, MidpointRounding.ToZero) / 2;

    private static int MonthsBetween(DateOnly from, DateOnly to) =>
        ((to.Year - from.Year) * 12) + to.Month - from.Month;
}
