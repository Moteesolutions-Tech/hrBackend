namespace Motee.Domain.Leave;

// The twelve months a company's entitlement runs over.
//
// Not always January to December. UK companies commonly run April to March to line up
// with the tax year, and a company that changed hands may run from whatever date the
// acquisition closed. Getting this wrong does not fail loudly — it silently attributes
// somebody's leave to the wrong year, so their balance is wrong and nobody can see why.
public readonly record struct LeaveYear(DateOnly Start, DateOnly End)
{
    // Which year a date falls in, given the day the company's year turns over.
    //
    // startMonth/startDay describe the turnover, not a specific year: "1 April" means
    // every April. A date before this year's turnover belongs to the year that began the
    // previous April, which is the whole subtlety here.
    public static LeaveYear Containing(DateOnly date, int startMonth, int startDay)
    {
        DateOnly turnover = SafeDate(date.Year, startMonth, startDay);

        DateOnly start = date >= turnover
            ? turnover
            : SafeDate(date.Year - 1, startMonth, startDay);

        return new LeaveYear(start, start.AddYears(1).AddDays(-1));
    }

    // 29 February is a legal turnover day and does not exist in three years out of four.
    // Clamping to the 28th keeps the year continuous rather than throwing on a date
    // somebody entered in good faith.
    private static DateOnly SafeDate(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    public bool Contains(DateOnly date) => date >= Start && date <= End;

    // What a year is called on screen. "2026" when it runs to the calendar, "2026/27"
    // when it straddles — because "2026" would be ambiguous for an April start.
    public string Label => Start.Month == 1 && Start.Day == 1
        ? Start.Year.ToString()
        : $"{Start.Year}/{End.Year % 100:D2}";
}
