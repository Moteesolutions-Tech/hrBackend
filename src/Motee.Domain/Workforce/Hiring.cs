namespace Motee.Domain.Workforce;

// A role that got filled: when it opened, and when somebody said yes.
public readonly record struct Filled(DateOnly OpenedOn, DateOnly AcceptedOn);

// How hiring is going.
//
// Both figures here get quoted at recruiters, so what they leave out matters
// as much as what they count.
public static class Hiring
{
    // Opening to acceptance, averaged over the roles that actually got
    // filled.
    //
    // Null rather than zero when none have. A role still open has no
    // time-to-fill, and counting days-so-far would mean the longest-running
    // vacancies improved the average — the opposite of what the number is
    // read as.
    public static decimal? AverageDaysToFill(IReadOnlyCollection<Filled> filled)
    {
        if (filled.Count == 0)
        {
            return null;
        }

        // Clamped at zero: a vacancy published after its offer was accepted is
        // somebody backfilling the record, not a hire that took negative time.
        decimal total = filled.Sum(role =>
            Math.Max(0, role.AcceptedOn.DayNumber - role.OpenedOn.DayNumber));

        return Math.Round(total / filled.Count, 1, MidpointRounding.AwayFromZero);
    }

    // Null when nothing has gone out. Zero would read as "everybody said no".
    public static decimal? AcceptanceRate(int extended, int accepted) => extended <= 0
        ? null
        : Math.Round(accepted / (decimal)extended * 100m, 1, MidpointRounding.AwayFromZero);

    public static int DaysOpen(DateOnly openedOn, DateOnly asOf) =>
        Math.Max(0, asOf.DayNumber - openedOn.DayNumber);

    // Two months is the point at which a vacancy has stopped being a search
    // and become a problem — usually the brief, the salary or the panel.
    public static bool IsStale(int daysOpen, int afterDays) => daysOpen >= afterDays;
}
