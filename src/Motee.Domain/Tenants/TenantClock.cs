using Motee.Domain.Common;

namespace Motee.Domain.Tenants;

// Which calendar day an instant falls on, for the company it happened at.
//
// Attendance is the first thing here that needs this. "Today's attendance", "the week
// starting Monday" and "did they clock in before nine" are all local-calendar
// questions, and answering them in UTC is wrong in a way that leaves no trace: a punch
// at 00:30 in Lagos files under yesterday, the row reads perfectly, and the day it
// belonged to is short one person.
public static class TenantClock
{
    public const string Nigeria = "Africa/Lagos";

    public const string UnitedKingdom = "Europe/London";

    public static string DefaultFor(CountryCode country) =>
        country == CountryCode.UnitedKingdom ? UnitedKingdom : Nigeria;

    // Falls back rather than throwing. A zone id that the host does not recognise —
    // an older container without tzdata, a hand-edited settings row — would otherwise
    // take out every attendance request for that company, which is a worse outcome
    // than an hour's offset on a boundary case.
    public static TimeZoneInfo Zone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out TimeZoneInfo? zone)
            ? zone
            : TimeZoneInfo.Utc;
    }

    public static DateOnly DayOf(DateTimeOffset instant, string? timeZoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone(timeZoneId)).DateTime);

    public static TimeOnly TimeOf(DateTimeOffset instant, string? timeZoneId) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone(timeZoneId)).DateTime);

    // Monday-anchored, matching how the timesheet week is drawn. ISO-8601's week
    // start, and the one every payroll week in both countries runs on.
    public static DateOnly WeekStart(DateOnly day) =>
        day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}
