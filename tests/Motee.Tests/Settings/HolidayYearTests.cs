using Motee.Domain.Leave;
using Motee.Domain.Settings;

namespace Motee.Tests.Settings;

// The holiday year cycles the absence settings screen lists.
//
// The screen offers free-text labels, arbitrary dates and a stored "active"
// flag — and leave reads none of it. An admin could add a 2025/26 cycle, mark
// it active, and every balance in the product would carry on computing against
// January while the settings screen insisted otherwise.
//
// Written before the rules.
public class HolidayYearTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    // ── the cycles are generated, not typed ─────────────────────────────────

    [Fact]
    public void ACalendarYearCompanyGetsCalendarCycles()
    {
        IReadOnlyList<HolidayYearCycle> cycles = HolidayYears.Cycles(1, 1, Today);

        HolidayYearCycle active = cycles.Single(cycle => cycle.IsActive);

        Assert.Equal(new DateOnly(2026, 1, 1), active.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 31), active.EndDate);
        Assert.Equal("Calendar Year 2026", active.Label);
    }

    // April to March, which is what UK companies run to line up with the tax
    // year. In June 2026 that puts them in the year that began April 2026.
    [Fact]
    public void AnAprilCompanyGetsFiscalCycles()
    {
        IReadOnlyList<HolidayYearCycle> cycles = HolidayYears.Cycles(4, 1, Today);

        HolidayYearCycle active = cycles.Single(cycle => cycle.IsActive);

        Assert.Equal(new DateOnly(2026, 4, 1), active.StartDate);
        Assert.Equal(new DateOnly(2027, 3, 31), active.EndDate);
        Assert.Equal("Fiscal Year 2026/27", active.Label);
    }

    // January 2026 against an April turnover is still the 2025/26 year. The
    // whole subtlety of a straddling year, and the thing a hand-typed cycle
    // gets wrong.
    [Fact]
    public void ADateBeforeTheTurnoverBelongsToThePreviousCycle()
    {
        IReadOnlyList<HolidayYearCycle> cycles =
            HolidayYears.Cycles(4, 1, new DateOnly(2026, 1, 20));

        HolidayYearCycle active = cycles.Single(cycle => cycle.IsActive);

        Assert.Equal(new DateOnly(2025, 4, 1), active.StartDate);
        Assert.Equal("Fiscal Year 2025/26", active.Label);
    }

    [Fact]
    public void ExactlyOneCycleIsActive()
    {
        Assert.Single(HolidayYears.Cycles(4, 6, Today), cycle => cycle.IsActive);
    }

    [Fact]
    public void TheListReachesBackOneYearAndForwardTwo()
    {
        IReadOnlyList<HolidayYearCycle> cycles = HolidayYears.Cycles(1, 1, Today);

        Assert.Equal(HolidayYears.CyclesBefore + HolidayYears.CyclesAfter + 1, cycles.Count);
        Assert.Equal(new DateOnly(2025, 1, 1), cycles[0].StartDate);
        Assert.Equal(new DateOnly(2028, 1, 1), cycles[^1].StartDate);
    }

    [Fact]
    public void TheCyclesRunBackToBackWithNoGapOrOverlap()
    {
        IReadOnlyList<HolidayYearCycle> cycles = HolidayYears.Cycles(4, 1, Today);

        for (int index = 1; index < cycles.Count; index++)
        {
            Assert.Equal(cycles[index - 1].EndDate.AddDays(1), cycles[index].StartDate);
        }
    }

    // The screen posts an id back to set a cycle active. A generated id would
    // change on each request, so the post would act on a cycle that no longer
    // exists by the time it lands.
    [Fact]
    public void ACycleKeepsTheSameIdBetweenRequests()
    {
        IReadOnlyList<HolidayYearCycle> first = HolidayYears.Cycles(4, 1, Today);
        IReadOnlyList<HolidayYearCycle> again = HolidayYears.Cycles(4, 1, Today.AddDays(3));

        Assert.Equal(
            first.Single(cycle => cycle.IsActive).Id,
            again.Single(cycle => cycle.IsActive).Id);
    }

    // ── it agrees with the leave module ────────────────────────────────────

    // Two answers to "which year is this" would file leave under one and show
    // the balance against the other.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(10, 6)]
    public void TheActiveCycleIsTheOneLeaveComputesAgainst(int month, int day)
    {
        HolidayYearCycle active = HolidayYears
            .Cycles(month, day, Today)
            .Single(cycle => cycle.IsActive);

        LeaveYear year = LeaveYear.Containing(Today, month, day);

        Assert.Equal(year.Start, active.StartDate);
        Assert.Equal(year.End, active.EndDate);
    }

    // ── a turnover that cannot be ──────────────────────────────────────────

    [Theory]
    [InlineData(0, 1)]
    [InlineData(13, 1)]
    [InlineData(2, 31)]
    [InlineData(6, 0)]
    public void ATurnoverThatCannotExistIsRefused(int month, int day)
    {
        Assert.False(HolidayYears.IsValidTurnover(month, day));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 6)]
    [InlineData(12, 31)]
    [InlineData(2, 29)]
    public void ARealTurnoverIsAccepted(int month, int day)
    {
        Assert.True(HolidayYears.IsValidTurnover(month, day));
    }

    // A stored 0 would throw inside DateOnly and present as a crash on the
    // settings screen rather than as the bad setting it is. Same fallback as
    // LeaveYearResolver, so both read a broken setting the same way.
    [Fact]
    public void ABrokenStoredTurnoverFallsBackToJanuary()
    {
        IReadOnlyList<HolidayYearCycle> cycles = HolidayYears.Cycles(0, 0, Today);

        Assert.Equal(new DateOnly(2026, 1, 1), cycles.Single(cycle => cycle.IsActive).StartDate);
    }

    // 29 February is a legal turnover and does not exist three years in four.
    [Fact]
    public void ALeapDayTurnoverDoesNotThrow()
    {
        IReadOnlyList<HolidayYearCycle> cycles =
            HolidayYears.Cycles(2, 29, new DateOnly(2026, 6, 15));

        Assert.Equal(new DateOnly(2026, 2, 28), cycles.Single(cycle => cycle.IsActive).StartDate);
    }
}
