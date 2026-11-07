using Motee.Domain.Offboarding;
using Motee.Domain.Workforce;

namespace Motee.Tests.Unit;

// Turnover: who left, why, and against how many people.
//
// The split is the point. Resignations are the company's problem to fix and
// redundancies are its own decision, so a single "12% turnover" figure hides
// the only thing worth knowing. Getting the classification wrong turns a
// restructure into an apparent retention crisis, or the reverse.
public class TurnoverTests
{
    [Theory]
    [InlineData(ExitReason.Resignation, true)]
    [InlineData(ExitReason.Retirement, true)]
    [InlineData(ExitReason.Termination, false)]
    [InlineData(ExitReason.Redundancy, false)]
    [InlineData(ExitReason.ContractEnd, false)]
    public void LeavingIsVoluntaryOnlyWhenTheyChoseIt(ExitReason reason, bool expected) =>
        Assert.Equal(expected, Turnover.IsVoluntary(reason));

    // Not guessed either way. An exit with no reason recorded is a gap in the
    // data, and folding it into voluntary would flatter the figure while
    // folding it into involuntary would libel the company.
    [Fact]
    public void AnExitWithNoReasonIsNeither()
    {
        Assert.False(Turnover.IsVoluntary(null));
        Assert.False(Turnover.IsInvoluntary(null));
    }

    [Fact]
    public void AnUnclassifiedExitStillCounts() =>
        Assert.Equal(
            1,
            Turnover.Of([new Exit(null)], averageHeadcount: 10).Total);

    // ── the rate ────────────────────────────────────────────────────────────

    // Leavers over the average headcount for the period, not over the closing
    // figure. A team that halved would otherwise report a rate of 100% when
    // half of them left, which overstates it by double.
    [Fact]
    public void TheRateIsAgainstAverageHeadcount() =>
        Assert.Equal(
            20m,
            Turnover.Of(
                [new Exit(ExitReason.Resignation), new Exit(ExitReason.Redundancy)],
                averageHeadcount: 10).RatePercent);

    [Fact]
    public void NobodyLeavingIsZeroNotNull() =>
        Assert.Equal(0m, Turnover.Of([], averageHeadcount: 10).RatePercent);

    // A department that did not exist yet has no rate — not a rate of zero,
    // and certainly not a division by nothing.
    [Fact]
    public void ARateAgainstNobodyIsNotReported() =>
        Assert.Null(Turnover.Of([new Exit(ExitReason.Resignation)], averageHeadcount: 0).RatePercent);

    [Fact]
    public void TheRateIsRoundedToOneDecimal() =>
        Assert.Equal(
            14.3m,
            Turnover.Of([new Exit(ExitReason.Resignation)], averageHeadcount: 7).RatePercent);

    [Fact]
    public void EachKindIsCountedSeparately()
    {
        TurnoverTally tally = Turnover.Of(
            [
                new Exit(ExitReason.Resignation),
                new Exit(ExitReason.Resignation),
                new Exit(ExitReason.Redundancy),
                new Exit(null),
            ],
            averageHeadcount: 20);

        Assert.Equal(4, tally.Total);
        Assert.Equal(2, tally.Voluntary);
        Assert.Equal(1, tally.Involuntary);
        Assert.Equal(1, tally.Unclassified);
        Assert.Equal(20m, tally.RatePercent);
    }

    // Average of the opening and closing headcount. Crude, and the standard
    // way it is done — a daily mean would be more exact and would disagree
    // with every HR report the reader has ever seen.
    [Theory]
    [InlineData(10, 20, 15)]
    [InlineData(0, 10, 5)]
    [InlineData(10, 10, 10)]
    public void AverageHeadcountIsTheMeanOfTheEnds(int opening, int closing, int expected) =>
        Assert.Equal(expected, Turnover.AverageHeadcount(opening, closing));
}
