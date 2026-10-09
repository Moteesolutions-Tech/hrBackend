using Motee.Domain.Common;

namespace Motee.Domain.Leave;

// The days a country is closed, worked out rather than listed.
//
// A hardcoded list — which is what the frontend currently has, for the UK in 2026 — is
// wrong the moment somebody books leave in the following year. Most of these dates are
// computable: Easter follows an algorithm, the May and August bank holidays are the nth
// Monday of their month, and a fixed date landing on a weekend moves by a stated rule.
//
// What is not computable is left out on purpose. Nigeria's Eid al-Fitr, Eid al-Adha and
// Maulud an-Nabi follow the Islamic calendar and are confirmed by moon sighting and
// government announcement, typically days beforehand. Guessing them would put wrong days
// in the calendar with the same authority as the right ones, so tenants add those — and
// the seeder says so rather than leaving somebody to discover the gap.
public static class PublicHolidayCalendar
{
    // A country we have no calendar for returns nothing rather than guessing at one.
    // Empty means "add your own days", which is recoverable; a wrong calendar is not.
    public static IReadOnlyList<(DateOnly Date, string Name)> For(CountryCode country, int year)
    {
        if (country == CountryCode.UnitedKingdom)
        {
            return UnitedKingdom(year);
        }

        return country == CountryCode.Nigeria ? Nigeria(year) : [];
    }

    // England and Wales. Scotland and Northern Ireland differ — 2 January, 12 July —
    // and would be separate entries if the product ever needs them.
    private static List<(DateOnly, string)> UnitedKingdom(int year)
    {
        DateOnly easter = EasterSunday(year);

        List<(DateOnly Date, string Name)> fixedDays =
        [
            (new DateOnly(year, 1, 1), "New Year's Day"),
            (new DateOnly(year, 12, 25), "Christmas Day"),
            (new DateOnly(year, 12, 26), "Boxing Day"),
        ];

        List<(DateOnly, string)> holidays = [.. Substituted(fixedDays)];

        holidays.Add((easter.AddDays(-2), "Good Friday"));
        holidays.Add((easter.AddDays(1), "Easter Monday"));

        // Never substituted: they are defined as a Monday, so they cannot fall on a
        // weekend in the first place.
        holidays.Add((NthWeekday(year, 5, DayOfWeek.Monday, 1), "Early May Bank Holiday"));
        holidays.Add((LastWeekday(year, 5, DayOfWeek.Monday), "Spring Bank Holiday"));
        holidays.Add((LastWeekday(year, 8, DayOfWeek.Monday), "Summer Bank Holiday"));

        return [.. holidays.OrderBy(holiday => holiday.Item1)];
    }

    // The days set by the Public Holidays Act. The Christian moveable feasts are
    // computed; the Islamic ones are not, for the reason given above.
    private static List<(DateOnly, string)> Nigeria(int year)
    {
        DateOnly easter = EasterSunday(year);

        List<(DateOnly Date, string Name)> fixedDays =
        [
            (new DateOnly(year, 1, 1), "New Year's Day"),
            (new DateOnly(year, 5, 1), "Workers' Day"),
            (new DateOnly(year, 6, 12), "Democracy Day"),
            (new DateOnly(year, 10, 1), "Independence Day"),
            (new DateOnly(year, 12, 25), "Christmas Day"),
            (new DateOnly(year, 12, 26), "Boxing Day"),
        ];

        List<(DateOnly, string)> holidays = [.. Substituted(fixedDays)];

        holidays.Add((easter.AddDays(-2), "Good Friday"));
        holidays.Add((easter.AddDays(1), "Easter Monday"));

        return [.. holidays.OrderBy(holiday => holiday.Item1)];
    }

    // A fixed date landing on a weekend is observed on the next weekday that is not
    // already taken. Christmas on a Saturday pushes to Monday; Boxing Day then pushes
    // past it to Tuesday, rather than the two collapsing onto one day.
    private static List<(DateOnly, string)> Substituted(
        IReadOnlyList<(DateOnly Date, string Name)> days)
    {
        List<(DateOnly, string)> observed = [];
        HashSet<DateOnly> taken = [];

        foreach ((DateOnly date, string name) in days.OrderBy(day => day.Date))
        {
            DateOnly when = date;

            while (when.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || taken.Contains(when))
            {
                when = when.AddDays(1);
            }

            taken.Add(when);

            observed.Add((when, when == date ? name : $"{name} (substitute)"));
        }

        return observed;
    }

    // The anonymous Gregorian algorithm. Easter governs Good Friday and Easter Monday in
    // both countries, and there is no simpler way to get it — it is the first Sunday
    // after the first ecclesiastical full moon on or after 21 March.
    public static DateOnly EasterSunday(int year)
    {
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = ((19 * a) + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        int m = (a + (11 * h) + (22 * l)) / 451;
        int month = (h + l - (7 * m) + 114) / 31;
        int day = ((h + l - (7 * m) + 114) % 31) + 1;

        return new DateOnly(year, month, day);
    }

    private static DateOnly NthWeekday(int year, int month, DayOfWeek weekday, int nth)
    {
        DateOnly first = new(year, month, 1);
        int offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;

        return first.AddDays(offset + ((nth - 1) * 7));
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek weekday)
    {
        DateOnly last = new(year, month, DateTime.DaysInMonth(year, month));
        int offset = ((int)last.DayOfWeek - (int)weekday + 7) % 7;

        return last.AddDays(-offset);
    }
}
