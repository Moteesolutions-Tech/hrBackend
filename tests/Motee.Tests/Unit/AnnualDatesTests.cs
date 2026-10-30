using Motee.Domain.Common;

namespace Motee.Tests.Unit;

// Birthdays and work anniversaries: the same date, every year, forever.
//
// Written down once because two screens needed it and both got it wrong in the
// same two ways — a December birthday read as "in -340 days" from January, and
// 29 February disappeared in three years out of four.
public class AnnualDatesTests
{
    private static readonly DateOnly MidJune = new(2026, 6, 15);

    [Fact]
    public void ADateLaterThisYearFallsThisYear() =>
        Assert.Equal(
            new DateOnly(2026, 9, 3),
            AnnualDates.Next(new DateOnly(1990, 9, 3), MidJune));

    [Fact]
    public void ADateAlreadyPastFallsNextYear() =>
        Assert.Equal(
            new DateOnly(2027, 3, 11),
            AnnualDates.Next(new DateOnly(1988, 3, 11), MidJune));

    // The one that made December birthdays read as negative days away.
    [Fact]
    public void ADecemberDateSeenFromJanuaryIsThisYearNotLast() =>
        Assert.Equal(
            new DateOnly(2026, 12, 24),
            AnnualDates.Next(new DateOnly(1979, 12, 24), new DateOnly(2026, 1, 2)));

    [Fact]
    public void TodayCounts() =>
        Assert.Equal(MidJune, AnnualDates.Next(new DateOnly(1995, 6, 15), MidJune));

    [Fact]
    public void ALeapDayLandsOnTheFirstOfMarchInACommonYear() =>
        Assert.Equal(
            new DateOnly(2027, 3, 1),
            AnnualDates.Next(new DateOnly(2000, 2, 29), new DateOnly(2027, 1, 1)));

    [Fact]
    public void ALeapDayKeepsItsOwnDateInALeapYear() =>
        Assert.Equal(
            new DateOnly(2028, 2, 29),
            AnnualDates.Next(new DateOnly(2000, 2, 29), new DateOnly(2028, 1, 1)));

    // ── windows ─────────────────────────────────────────────────────────────

    [Fact]
    public void AWindowHoldingTheDayReportsIt() =>
        Assert.Equal(
            [new DateOnly(2026, 6, 20)],
            AnnualDates.Occurrences(
                new DateOnly(1990, 6, 20), MidJune, new DateOnly(2026, 6, 30)));

    [Fact]
    public void AWindowMissingTheDayReportsNothing() =>
        Assert.Empty(AnnualDates.Occurrences(
            new DateOnly(1990, 1, 20), MidJune, new DateOnly(2026, 6, 30)));

    // A calendar's year view starts in one year and ends in the next, so the
    // same birthday can be in it twice. Returning only the first would drop the
    // second from the grid.
    [Fact]
    public void AWindowLongerThanAYearReportsEveryOccurrence() =>
        Assert.Equal(
            [new DateOnly(2026, 7, 4), new DateOnly(2027, 7, 4)],
            AnnualDates.Occurrences(
                new DateOnly(1985, 7, 4), MidJune, new DateOnly(2027, 12, 31)));

    [Fact]
    public void AWindowThatRunsBackwardsReportsNothing() =>
        Assert.Empty(AnnualDates.Occurrences(
            new DateOnly(1990, 6, 20), new DateOnly(2026, 6, 30), MidJune));

    [Fact]
    public void AWindowOfOneDayOnTheDateReportsIt() =>
        Assert.Equal(
            [MidJune],
            AnnualDates.Occurrences(new DateOnly(1990, 6, 15), MidJune, MidJune));
}
