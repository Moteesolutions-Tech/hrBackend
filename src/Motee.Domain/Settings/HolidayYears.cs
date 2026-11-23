using Motee.Domain.Leave;

namespace Motee.Domain.Settings;

// The holiday year cycles the absence settings screen lists.
//
// Generated from the one thing that decides them — the day the company's year
// turns over, already stored as TenantSettings.LeaveYearStartMonth/Day and
// already read by every leave balance through LeaveYearResolver. The screen
// instead offers free-text labels, arbitrary start and end dates, and a stored
// "active" flag, none of which leave reads. So an admin could add a 2025/26
// cycle, mark it active, and every balance in the product would carry on
// computing against January — with the settings screen insisting otherwise.
//
// One turnover, cycles derived from it, and the active one is simply the cycle
// containing today. There is nothing to drift.
public sealed record HolidayYearCycle
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required DateOnly StartDate { get; init; }

    public required DateOnly EndDate { get; init; }

    public required bool IsActive { get; init; }
}

public static class HolidayYears
{
    // One behind and two ahead. Behind because carry-over and year-end both
    // look back, ahead because leave is booked into next year long before it
    // starts — and far enough ahead is a list nobody reads.
    public const int CyclesBefore = 1;

    public const int CyclesAfter = 2;

    public static IReadOnlyList<HolidayYearCycle> Cycles(
        int startMonth,
        int startDay,
        DateOnly today)
    {
        (int month, int day) = Turnover(startMonth, startDay);

        LeaveYear current = LeaveYear.Containing(today, month, day);

        List<HolidayYearCycle> cycles = [];

        for (int offset = -CyclesBefore; offset <= CyclesAfter; offset++)
        {
            LeaveYear year = LeaveYear.Containing(current.Start.AddYears(offset), month, day);

            cycles.Add(new HolidayYearCycle
            {
                // Derived from the start date, so the same cycle has the same id
                // every time it is asked for. A generated one would change on
                // each request and the screen's "set active" would act on a
                // cycle that no longer exists by the time it posts.
                Id = year.Start.ToString("yyyy-MM-dd"),
                Label = Describe(year),
                StartDate = year.Start,
                EndDate = year.End,
                IsActive = year.Contains(today),
            });
        }

        return cycles;
    }

    // "Calendar Year 2026" against January, "Fiscal Year 2025/26" against
    // anything else. Matches what somebody would have typed into the label box,
    // which is why the box is gone.
    public static string Describe(LeaveYear year) =>
        year.Start.Month == 1 && year.Start.Day == 1
            ? $"Calendar Year {year.Start.Year}"
            : $"Fiscal Year {year.Label}";

    // Same fallback as LeaveYearResolver, and for the same reason: a stored 0 or
    // a 31st of February would throw inside DateOnly and present as a crash on
    // the settings screen rather than as the bad setting it is.
    public static (int Month, int Day) Turnover(int month, int day) =>
        month is < 1 or > 12 || day is < 1 or > 31 ? (1, 1) : (month, day);

    public static bool IsValidTurnover(int month, int day)
    {
        if (month is < 1 or > 12 || day < 1)
        {
            return false;
        }

        // Against a leap year, so 29 February is accepted. LeaveYear clamps it
        // to the 28th in the three years it does not exist.
        return day <= DateTime.DaysInMonth(2024, month);
    }
}
