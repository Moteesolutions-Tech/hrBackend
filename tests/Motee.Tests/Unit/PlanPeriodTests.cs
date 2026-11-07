using Motee.Domain.Headcount;

namespace Motee.Tests.Unit;

// "Q3 2026" and "FY 2026" as the frontend writes them, and the calendar
// windows behind them.
//
// The window is the part that earns a test. A plan's actual headcount is
// counted as at the end of its period, so a wrong boundary silently reports
// last quarter's number against this quarter's target — and the two look
// equally plausible on the screen.
public class PlanPeriodTests
{
    [Theory]
    [InlineData("Q1 2026", 1, 2026)]
    [InlineData("Q4 2025", 4, 2025)]
    public void AQuarterIsRead(string period, int quarter, int year)
    {
        Assert.True(PlanPeriods.IsValid(period));

        (int Quarter, int Year)? parsed = PlanPeriods.Quarter(period);

        Assert.Equal((quarter, year), parsed);
    }

    [Theory]
    [InlineData("FY 2026")]
    public void AFullYearIsNotAQuarter(string period)
    {
        Assert.True(PlanPeriods.IsValid(period));
        Assert.Null(PlanPeriods.Quarter(period));
    }

    [Theory]
    [InlineData("Q5 2026")]
    [InlineData("Q0 2026")]
    [InlineData("2026 Q1")]
    [InlineData("Q1")]
    [InlineData("FY")]
    [InlineData("")]
    [InlineData("rubbish")]
    public void AnythingElseIsNotAPeriod(string period) =>
        Assert.False(PlanPeriods.IsValid(period));

    // ── windows ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Q1 2026", "2026-01-01", "2026-03-31")]
    [InlineData("Q2 2026", "2026-04-01", "2026-06-30")]
    [InlineData("Q3 2026", "2026-07-01", "2026-09-30")]
    [InlineData("Q4 2026", "2026-10-01", "2026-12-31")]
    [InlineData("FY 2026", "2026-01-01", "2026-12-31")]
    public void APeriodCoversItsOwnDays(string period, string start, string end)
    {
        (DateOnly Start, DateOnly End)? window = PlanPeriods.Window(period);

        Assert.NotNull(window);
        Assert.Equal(DateOnly.Parse(start), window.Value.Start);
        Assert.Equal(DateOnly.Parse(end), window.Value.End);
    }

    [Fact]
    public void AnInvalidPeriodHasNoWindow() => Assert.Null(PlanPeriods.Window("Q9 2026"));

    // ── comparison periods ──────────────────────────────────────────────────

    [Fact]
    public void TheQuarterBeforeIsTheOneBefore() =>
        Assert.Equal("Q2 2026", PlanPeriods.PreviousQuarter("Q3 2026"));

    // The wraparound. Q1's predecessor is last year's Q4, and getting this
    // wrong reports a year-long jump as a quarter's growth.
    [Fact]
    public void TheQuarterBeforeTheFirstIsLastYearsFourth() =>
        Assert.Equal("Q4 2025", PlanPeriods.PreviousQuarter("Q1 2026"));

    [Fact]
    public void AFullYearHasNoPreviousQuarter() =>
        Assert.Null(PlanPeriods.PreviousQuarter("FY 2026"));

    [Fact]
    public void TheSameQuarterLastYearIsTheSameQuarterLastYear() =>
        Assert.Equal("Q3 2025", PlanPeriods.SameQuarterLastYear("Q3 2026"));

    [Fact]
    public void AFullYearComparesAgainstTheYearBefore() =>
        Assert.Equal("FY 2025", PlanPeriods.SameQuarterLastYear("FY 2026"));

    // ── which period a day falls in ─────────────────────────────────────────

    [Theory]
    [InlineData("2026-01-01", "Q1 2026")]
    [InlineData("2026-03-31", "Q1 2026")]
    [InlineData("2026-04-01", "Q2 2026")]
    [InlineData("2026-11-02", "Q4 2026")]
    public void ADayBelongsToItsQuarter(string day, string expected) =>
        Assert.Equal(expected, PlanPeriods.QuarterOf(DateOnly.Parse(day)));

    [Fact]
    public void TheQuartersOfAYearAreInOrder() =>
        Assert.Equal(
            ["Q1 2026", "Q2 2026", "Q3 2026", "Q4 2026"], PlanPeriods.QuartersOf(2026));
}
