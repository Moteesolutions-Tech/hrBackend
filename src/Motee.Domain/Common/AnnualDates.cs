namespace Motee.Domain.Common;

// A date that comes round every year: a birthday, a work anniversary.
//
// Two mistakes are easy enough here that both have been made in this codebase.
// Subtracting a birth date from today gives an age, not a countdown, so a
// December birthday read from January is hundreds of days in the past. And
// rebuilding the date with new DateOnly(year, 2, 29) throws in three years out
// of four, which takes out the whole list rather than one person.
public static class AnnualDates
{
    // 29 February is observed on 1 March in a common year. The alternative — 28
    // February — is the other common convention, and either is defensible; what
    // is not is the date vanishing. Picked to match the leap-day rule already
    // used for anniversaries elsewhere.
    public static DateOnly OnYear(DateOnly source, int year) =>
        source is { Month: 2, Day: 29 } && !DateTime.IsLeapYear(year)
            ? new DateOnly(year, 3, 1)
            : new DateOnly(year, source.Month, source.Day);

    // The first occurrence on or after a day. Today counts: it is somebody's
    // birthday today, not in 365 days.
    public static DateOnly Next(DateOnly source, DateOnly onOrAfter)
    {
        DateOnly thisYear = OnYear(source, onOrAfter.Year);

        return thisYear >= onOrAfter ? thisYear : OnYear(source, onOrAfter.Year + 1);
    }

    // Every occurrence inside a window. A year view runs from one year into the
    // next, so the same birthday can legitimately appear twice; returning only
    // the first would leave a gap in the grid that nothing explains.
    public static IReadOnlyList<DateOnly> Occurrences(DateOnly source, DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            return [];
        }

        List<DateOnly> dates = [];

        for (int year = from.Year; year <= to.Year; year++)
        {
            DateOnly occurrence = OnYear(source, year);

            if (occurrence >= from && occurrence <= to)
            {
                dates.Add(occurrence);
            }
        }

        return dates;
    }
}
