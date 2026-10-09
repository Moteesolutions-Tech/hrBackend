namespace Motee.Domain.Leave;

// How many days a leave request actually costs somebody.
//
// The naive answer — end minus start — charges people for the weekend, and the frontend's
// current answer counts Monday to Friday but ignores public holidays entirely. Both are
// wrong in the direction that takes days off staff who did not take them, which is the
// kind of error people notice at the end of the year and cannot reconstruct.
public static class WorkingDays
{
    // Public holidays are passed in rather than looked up here, because which days are
    // holidays depends on the country and the tenant — a Nigerian company and a UK one
    // share almost none of them, and either may add days of their own.
    public static decimal Between(
        DateOnly start,
        DateOnly end,
        IReadOnlySet<DateOnly> publicHolidays,
        bool excludePublicHolidays = true)
    {
        if (end < start)
        {
            return 0m;
        }

        int days = 0;

        for (DateOnly day = start; day <= end; day = day.AddDays(1))
        {
            if (IsWorkingDay(day, publicHolidays, excludePublicHolidays))
            {
                days++;
            }
        }

        return days;
    }

    // A half day is still one calendar day; it just costs half.
    //
    // Only meaningful for a single-day request — "half of a fortnight" is not something
    // anybody means, and allowing it would let somebody book ten days and be charged
    // five.
    public static decimal ForRequest(
        DateOnly start,
        DateOnly end,
        bool isHalfDay,
        IReadOnlySet<DateOnly> publicHolidays,
        bool excludePublicHolidays = true)
    {
        decimal full = Between(start, end, publicHolidays, excludePublicHolidays);

        return isHalfDay && start == end && full > 0 ? 0.5m : full;
    }

    // Weekends are Saturday and Sunday here. That is right for both countries this
    // product serves; a company working a Sunday-to-Thursday week would need the working
    // pattern to become tenant data, which is a change to make when there is one rather
    // than a guess now.
    public static bool IsWorkingDay(
        DateOnly day,
        IReadOnlySet<DateOnly> publicHolidays,
        bool excludePublicHolidays = true)
    {
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        return !excludePublicHolidays || !publicHolidays.Contains(day);
    }
}
