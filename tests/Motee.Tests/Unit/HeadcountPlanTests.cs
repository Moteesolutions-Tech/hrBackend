using Motee.Domain.Headcount;

namespace Motee.Tests.Unit;

// A department's target against what it actually has, and what the gap means.
public class HeadcountPlanTests
{
    [Theory]
    [InlineData(8, 10, GapStatus.Under)]
    [InlineData(10, 10, GapStatus.OnTarget)]
    [InlineData(12, 10, GapStatus.Over)]
    [InlineData(0, 0, GapStatus.OnTarget)]
    public void TheGapIsWhereActualSitsAgainstTarget(
        int actual, int target, GapStatus expected) =>
        Assert.Equal(expected, Headcounts.Gap(actual, target));

    // ── movement ────────────────────────────────────────────────────────────

    [Fact]
    public void GrowthIsReportedToOneDecimal() =>
        Assert.Equal(16.7m, Headcounts.PercentChange(14, 12));

    [Fact]
    public void ShrinkingIsNegative() =>
        Assert.Equal(-25m, Headcounts.PercentChange(9, 12));

    // Going from nobody to five people is not a 500% increase and it is not
    // infinity. It has no meaningful percentage, so the caller shows the
    // absolute movement instead of a number that reads as a fault.
    [Fact]
    public void GrowthFromNothingHasNoPercentage() =>
        Assert.Null(Headcounts.PercentChange(5, 0));

    [Fact]
    public void NoMovementIsZeroNotNull() =>
        Assert.Equal(0m, Headcounts.PercentChange(12, 12));

    // ── what the gap report asks for ────────────────────────────────────────

    [Fact]
    public void BeingOverTargetIsAStructureQuestion() =>
        Assert.Equal(
            "Review Organisation Structure",
            Headcounts.RecommendedAction(actual: 12, target: 10, activeRecruitments: 0, openVacancies: 0));

    [Fact]
    public void BeingOnTargetNeedsNothing() =>
        Assert.Equal(
            "No Action Required",
            Headcounts.RecommendedAction(10, 10, 0, 0));

    // Under target with the order of precedence that matters: hiring already
    // under way beats an unfilled vacancy, which beats having neither.
    [Fact]
    public void HiringAlreadyUnderWayIsReportedAsSuch() =>
        Assert.Equal(
            "Recruitment in Progress",
            Headcounts.RecommendedAction(8, 10, activeRecruitments: 2, openVacancies: 1));

    [Fact]
    public void AnUnfilledVacancyIsTheNextThingToDo() =>
        Assert.Equal(
            "Fill Existing Vacancy",
            Headcounts.RecommendedAction(8, 10, activeRecruitments: 0, openVacancies: 1));

    [Fact]
    public void AGapWithNoVacancyBehindItNeedsOneRaising() =>
        Assert.Equal(
            "Create Workforce Request",
            Headcounts.RecommendedAction(8, 10, activeRecruitments: 0, openVacancies: 0));

    // ── bands ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2002-01-01", "Under 25")]
    [InlineData("1994-01-01", "25–34")]
    [InlineData("1990-06-15", "35–44")]
    [InlineData("1981-01-01", "45–54")]
    [InlineData("1971-01-01", "55–64")]
    [InlineData("1955-01-01", "65+")]
    public void AgeIsReportedInBandsAndNeverAsAnAge(string born, string expected) =>
        Assert.Equal(
            expected,
            Headcounts.AgeBand(DateOnly.Parse(born), new DateOnly(2026, 11, 2)));

    // A birthday later this month means they have not had it yet, so they are
    // still in the younger band. Dividing days by 365.25 gets this wrong one
    // year in four.
    [Fact]
    public void ABirthdayNotYetReachedThisYearCountsAsTheYoungerAge() =>
        Assert.Equal(
            "25–34",
            Headcounts.AgeBand(new DateOnly(1991, 12, 25), new DateOnly(2026, 11, 2)));

    [Fact]
    public void ABirthdayAlreadyPassedCountsAsTheOlderAge() =>
        Assert.Equal(
            "35–44",
            Headcounts.AgeBand(new DateOnly(1991, 10, 25), new DateOnly(2026, 11, 2)));

    [Theory]
    [InlineData("2026-06-01", "<1y")]
    [InlineData("2025-01-01", "1-3y")]
    [InlineData("2022-06-01", "3-5y")]
    [InlineData("2015-01-01", "5+y")]
    public void TenureIsReportedInBands(string started, string expected) =>
        Assert.Equal(
            expected,
            Headcounts.TenureBand(DateOnly.Parse(started), new DateOnly(2026, 11, 2)));

    [Fact]
    public void TenureInYearsIsReportedToOneDecimal() =>
        Assert.Equal(
            1.5m, Headcounts.TenureYears(new DateOnly(2025, 5, 2), new DateOnly(2026, 11, 2)));

    // ── counting a period's headcount from history ──────────────────────────

    // The figure the frontend cannot produce: it holds only today's roster, so
    // it fakes past quarters with a 2% decay. Counted from start and leaving
    // dates, a past period reports what was actually true.
    [Fact]
    public void HeadcountOnADayCountsWhoHadStartedAndNotLeft()
    {
        Spell[] people =
        [
            new(new DateOnly(2024, 1, 1), null),
            new(new DateOnly(2024, 1, 1), new DateOnly(2026, 3, 31)),
            new(new DateOnly(2026, 8, 1), null),
            new(null, null),
        ];

        Assert.Equal(2, Headcounts.OnDay(people, new DateOnly(2026, 1, 15)));
        Assert.Equal(2, Headcounts.OnDay(people, new DateOnly(2026, 9, 30)));
        Assert.Equal(1, Headcounts.OnDay(people, new DateOnly(2026, 4, 1)));
        Assert.Equal(0, Headcounts.OnDay(people, new DateOnly(2023, 1, 1)));
    }

    // Their leaving day is a day they were still here.
    [Fact]
    public void SomebodysLastDayStillCounts() =>
        Assert.Equal(
            1,
            Headcounts.OnDay(
                [new Spell(new DateOnly(2024, 1, 1), new DateOnly(2026, 3, 31))],
                new DateOnly(2026, 3, 31)));

    // Somebody with no start date has not started. Counting them would
    // inflate every historical figure by the size of the pending queue.
    [Fact]
    public void SomebodyWithNoStartDateIsNotHeadcount() =>
        Assert.Equal(0, Headcounts.OnDay([new Spell(null, null)], new DateOnly(2026, 1, 1)));
}
