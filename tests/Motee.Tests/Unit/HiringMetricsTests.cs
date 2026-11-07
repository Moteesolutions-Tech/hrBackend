using Motee.Domain.Workforce;

namespace Motee.Tests.Unit;

// How hiring is going: how long a role takes to fill, and how many offers
// come back yes.
//
// Both numbers get quoted at recruiters, so what they exclude matters as much
// as what they count. A role still open has no time-to-fill — including it as
// "days so far" drags the average down and makes a stalled pipeline look fast.
public class HiringMetricsTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);

    // ── time to fill ────────────────────────────────────────────────────────

    [Fact]
    public void TimeToFillIsFromOpeningToAcceptance() =>
        Assert.Equal(
            45m,
            Hiring.AverageDaysToFill(
                [new Filled(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 16))]));

    [Fact]
    public void SeveralHiresAverageOut() =>
        Assert.Equal(
            30m,
            Hiring.AverageDaysToFill(
            [
                new Filled(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 21)),
                new Filled(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 11)),
            ]));

    // A role nobody has filled has no time-to-fill. Counting days-so-far
    // would make the longest-running vacancies improve the average.
    [Fact]
    public void ARoleStillOpenIsNotInTheAverage() =>
        Assert.Null(Hiring.AverageDaysToFill([]));

    [Fact]
    public void TheAverageIsRoundedToOneDecimal() =>
        Assert.Equal(
            33.3m,
            Hiring.AverageDaysToFill(
            [
                new Filled(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)),
                new Filled(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)),
                new Filled(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 11)),
            ]));

    // A vacancy published after the offer was accepted is somebody backfilling
    // the record, not a negative hire.
    [Fact]
    public void AnAcceptanceBeforeTheOpeningIsNotNegativeDays() =>
        Assert.Equal(
            0m,
            Hiring.AverageDaysToFill(
                [new Filled(new DateOnly(2026, 10, 1), new DateOnly(2026, 9, 1))]));

    // ── offers ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheAcceptanceRateIsAcceptedOverExtended() =>
        Assert.Equal(75m, Hiring.AcceptanceRate(extended: 8, accepted: 6));

    [Fact]
    public void NoOffersOutIsNoRateRatherThanZero() =>
        Assert.Null(Hiring.AcceptanceRate(extended: 0, accepted: 0));

    [Fact]
    public void TheAcceptanceRateIsRoundedToOneDecimal() =>
        Assert.Equal(66.7m, Hiring.AcceptanceRate(extended: 3, accepted: 2));

    // ── the age of a live vacancy ───────────────────────────────────────────

    // Not a time-to-fill, but the number that says a role has gone quiet.
    [Fact]
    public void AnOpenVacancyReportsHowLongItHasBeenOpen() =>
        Assert.Equal(63, Hiring.DaysOpen(new DateOnly(2026, 9, 1), Today));

    [Fact]
    public void AVacancyPublishedTodayIsNoDaysOld() =>
        Assert.Equal(0, Hiring.DaysOpen(Today, Today));

    [Theory]
    [InlineData(10, false)]
    [InlineData(60, false)]
    [InlineData(61, true)]
    [InlineData(120, true)]
    public void AVacancyOpenPastTheThresholdIsStale(int days, bool expected) =>
        Assert.Equal(expected, Hiring.IsStale(days, afterDays: 61));
}
