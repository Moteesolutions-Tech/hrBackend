using Motee.Domain.Headcount;

namespace Motee.Tests.Unit;

// Who might leave, and why the number says so.
//
// The score exists because "monitor" against every name told nobody anything.
// Each factor is named and weighted, so a High and a Medium can be told apart
// and acted on differently — which is the whole of the client's §6.11.
//
// It is a prompt for a conversation, never a decision. Nothing in this
// codebase may act on it without a person, and the factors are returned with
// the score so the person can disagree with it.
public class AttritionScoringTests
{
    private static readonly DateOnly Today = new(2026, 11, 2);

    private static AttritionAssessment Assess(
        DateOnly? startDate = null,
        EmployeeSignals? signals = null) =>
        AttritionScoring.Assess(
            startDate ?? new DateOnly(2025, 1, 1), Today, signals ?? new EmployeeSignals());

    // ── tenure ──────────────────────────────────────────────────────────────

    [Fact]
    public void SomebodyWellOverSixYearsInCarriesTheHeaviestTenureWeight()
    {
        AttritionAssessment assessed = Assess(new DateOnly(2019, 1, 1));

        Assert.Equal(45, assessed.Score);
        Assert.Equal(
            "Long tenure without role change", Assert.Single(assessed.Factors).Label);
    }

    [Fact]
    public void SomebodyOverFourYearsInCarriesLess() =>
        Assert.Equal(30, Assess(new DateOnly(2021, 6, 1)).Score);

    [Fact]
    public void SomebodyOverTwoYearsInIsApproachingTypicalTenure()
    {
        AttritionAssessment assessed = Assess(new DateOnly(2023, 6, 1));

        Assert.Equal(12, assessed.Score);
        Assert.Equal("Approaching typical tenure", Assert.Single(assessed.Factors).Label);
    }

    [Fact]
    public void ARecentJoinerCarriesNoTenureWeight()
    {
        AttritionAssessment assessed = Assess(new DateOnly(2026, 6, 1));

        Assert.Equal(0, assessed.Score);
        Assert.Equal(AttritionRiskLevel.Low, assessed.Level);

        // Not an empty list: a row saying nothing reads as a row that failed.
        Assert.Equal("Standard monitoring", Assert.Single(assessed.Factors).Label);
    }

    // The bands are not cumulative. Somebody seven years in scores 45, not
    // 45 + 30 + 12.
    [Fact]
    public void OnlyOneTenureBandApplies() =>
        Assert.Single(Assess(new DateOnly(2015, 1, 1)).Factors);

    // ── circumstances ───────────────────────────────────────────────────────

    [Fact]
    public void BeingOnLeaveAddsWeight() =>
        Assert.Equal(
            15, Assess(new DateOnly(2026, 6, 1), new EmployeeSignals { OnLeave = true }).Score);

    [Fact]
    public void BeingOnProbationAddsWeight() =>
        Assert.Equal(
            20,
            Assess(new DateOnly(2026, 6, 1), new EmployeeSignals { OnProbation = true }).Score);

    [Fact]
    public void FixedTermAndContractStaffCarryEndDateRisk() =>
        Assert.Equal(
            20,
            Assess(new DateOnly(2026, 6, 1), new EmployeeSignals { FixedTerm = true }).Score);

    // Several years in on the same grade is the classic flight risk.
    [Fact]
    public void YearsOnTheSameGradeAddsWeight()
    {
        AttritionAssessment assessed = Assess(
            new DateOnly(2021, 6, 1), new EmployeeSignals { NoGradeProgression = true });

        Assert.Equal(48, assessed.Score);
        Assert.Contains(assessed.Factors, factor => factor.Label == "No grade progression");
    }

    [Fact]
    public void GradeStagnationNeedsSomeTenureBehindIt() =>
        Assert.DoesNotContain(
            Assess(new DateOnly(2026, 6, 1), new EmployeeSignals { NoGradeProgression = true })
                .Factors,
            factor => factor.Label == "No grade progression");

    // ── factors the client asked for that now have data behind them ─────────

    // Performance and absence were both on the client's §6.11 list and absent
    // from the frontend's model, which notes they "plug in here once
    // captured". Performance reviews and sickness records exist now, so they
    // do.
    [Fact]
    public void ALowLastPerformanceRatingAddsWeight()
    {
        AttritionAssessment assessed = Assess(
            new DateOnly(2026, 6, 1), new EmployeeSignals { LastPerformanceRating = 2 });

        Assert.Equal(20, assessed.Score);
        Assert.Contains(assessed.Factors, factor => factor.Label == "Low last review rating");
    }

    [Fact]
    public void AStrongLastPerformanceRatingAddsNothing() =>
        Assert.Equal(
            0,
            Assess(new DateOnly(2026, 6, 1), new EmployeeSignals { LastPerformanceRating = 4 })
                .Score);

    [Fact]
    public void AHighBradfordScoreAddsWeight()
    {
        AttritionAssessment assessed = Assess(
            new DateOnly(2026, 6, 1), new EmployeeSignals { BradfordScore = 250 });

        Assert.Equal(15, assessed.Score);
        Assert.Contains(assessed.Factors, factor => factor.Label == "Frequent short absences");
    }

    [Fact]
    public void AnOrdinaryAbsenceRecordAddsNothing() =>
        Assert.Equal(
            0, Assess(new DateOnly(2026, 6, 1), new EmployeeSignals { BradfordScore = 20 }).Score);

    // Absence that HR has excluded from the Bradford score must not come back
    // in here as a retention risk. Scoring somebody's disability twice, once
    // as attendance and once as flight risk, is the same unlawful outcome in
    // a different column.
    [Fact]
    public void ExcludedAbsenceDoesNotReappearAsRetentionRisk() =>
        Assert.Equal(
            0,
            Assess(
                new DateOnly(2026, 6, 1),
                new EmployeeSignals { BradfordScore = 250, AbsenceProtected = true }).Score);

    // ── the total ───────────────────────────────────────────────────────────

    [Fact]
    public void FactorsAddUp()
    {
        AttritionAssessment assessed = Assess(
            new DateOnly(2021, 6, 1),
            new EmployeeSignals { FixedTerm = true, NoGradeProgression = true });

        Assert.Equal(68, assessed.Score);
        Assert.Equal(3, assessed.Factors.Count);
    }

    // A score over a hundred is not more urgent than a hundred, and a
    // percentage above one hundred reads as a bug.
    [Fact]
    public void TheScoreIsCappedAtAHundred() =>
        Assert.Equal(
            100,
            Assess(
                new DateOnly(2015, 1, 1),
                new EmployeeSignals
                {
                    OnLeave = true,
                    FixedTerm = true,
                    NoGradeProgression = true,
                    LastPerformanceRating = 1,
                    BradfordScore = 500,
                }).Score);

    [Theory]
    [InlineData(0, AttritionRiskLevel.Low)]
    [InlineData(39, AttritionRiskLevel.Low)]
    [InlineData(40, AttritionRiskLevel.Medium)]
    [InlineData(69, AttritionRiskLevel.Medium)]
    [InlineData(70, AttritionRiskLevel.High)]
    [InlineData(100, AttritionRiskLevel.High)]
    public void TheBandComesFromTheScore(int score, AttritionRiskLevel expected) =>
        Assert.Equal(expected, AttritionScoring.LevelFor(score));

    // ── what to do about it ─────────────────────────────────────────────────

    [Fact]
    public void NobodyLowRiskNeedsAnAction() =>
        Assert.Equal(
            "No action needed",
            AttritionScoring.RecommendedAction(AttritionRiskLevel.Low, []));

    // Routed off the heaviest factor, not off the band. The band says how
    // urgent; the factor says what to actually do.
    [Theory]
    [InlineData("Long tenure without role change", AttritionRiskLevel.High, "Career development discussion")]
    [InlineData("Long tenure without role change", AttritionRiskLevel.Medium, "Review progression opportunities")]
    [InlineData("No grade progression", AttritionRiskLevel.High, "Career development discussion")]
    [InlineData("Low last review rating", AttritionRiskLevel.High, "Performance support plan")]
    [InlineData("Frequent short absences", AttritionRiskLevel.High, "Wellbeing conversation")]
    public void TheActionFollowsTheHeaviestFactor(
        string label, AttritionRiskLevel level, string expected) =>
        Assert.Equal(
            expected,
            AttritionScoring.RecommendedAction(
                level, [new RiskFactor { Label = label, Weight = 40 }]));

    [Fact]
    public void TheHeaviestFactorWinsNotTheFirst() =>
        Assert.Equal(
            "Wellbeing conversation",
            AttritionScoring.RecommendedAction(
                AttritionRiskLevel.High,
                [
                    new RiskFactor { Label = "Approaching typical tenure", Weight = 12 },
                    new RiskFactor { Label = "Frequent short absences", Weight = 40 },
                ]));
}
