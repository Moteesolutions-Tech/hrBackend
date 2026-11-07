namespace Motee.Domain.Headcount;

public enum AttritionRiskLevel
{
    Low,
    Medium,
    High,
}

public sealed record RiskFactor
{
    public required string Label { get; init; }

    // Points towards the 0–100 score. Returned with the label so somebody
    // shown a score can see what built it and disagree with it.
    public required int Weight { get; init; }
}

public sealed record AttritionAssessment
{
    public required int Score { get; init; }

    public required AttritionRiskLevel Level { get; init; }

    public required IReadOnlyList<RiskFactor> Factors { get; init; }

    public required string RecommendedAction { get; init; }
}

// What is known about somebody that bears on whether they might leave.
//
// A record of signals rather than the employee itself, so the scoring stays
// pure domain logic and the service decides where each signal comes from.
public sealed record EmployeeSignals
{
    public bool OnLeave { get; init; }

    public bool OnProbation { get; init; }

    public bool FixedTerm { get; init; }

    public bool NoGradeProgression { get; init; }

    // 1–5 from their last completed review, where there is one. Null means
    // nobody has reviewed them, which is not the same as a poor review.
    public int? LastPerformanceRating { get; init; }

    // Their Bradford Factor. Frequent short absences correlate with
    // disengagement, which is why the client asked for it.
    public decimal? BradfordScore { get; init; }

    // HR has excluded this person's absence from scoring — disability,
    // pregnancy. The absence signal is then dropped entirely rather than
    // reweighted: scoring somebody's disability once as attendance and again
    // as flight risk is the same unlawful outcome in a different column.
    public bool AbsenceProtected { get; init; }
}

// Who might leave, and why the number says so.
//
// This exists because "monitor" against every name told nobody anything. Each
// factor is named and weighted so a High and a Medium can be told apart and
// acted on differently.
//
// It is a prompt for a conversation, never a decision. Nothing may act on it
// without a person, and the factors travel with the score so the person can
// disagree with it.
public static class AttritionScoring
{
    // Weights as the client specified them. Conservative and additive, capped
    // at 100, with the band taken from the total.
    private const int LongTenure = 45;
    private const int MidTenure = 30;
    private const int ApproachingTenure = 12;
    private const int OnLeaveWeight = 15;
    private const int ProbationWeight = 20;
    private const int FixedTermWeight = 20;
    private const int NoProgressionWeight = 18;

    // Performance and absence were both on the client's list and had no data
    // behind them when the frontend was written — its own note says they
    // "plug in here once captured". Reviews and sickness records exist now.
    private const int LowRatingWeight = 20;
    private const int AbsenceWeight = 15;

    // A rating at or below this is a signal. Three of five is holding steady.
    private const int LowRatingAtOrBelow = 2;

    // The band at which the Bradford Factor starts suggesting a conversation,
    // matching the manager-review threshold the sickness module uses.
    private const int AbsenceScoreAtOrAbove = 50;

    public static AttritionRiskLevel LevelFor(int score) => score switch
    {
        >= 70 => AttritionRiskLevel.High,
        >= 40 => AttritionRiskLevel.Medium,
        _ => AttritionRiskLevel.Low,
    };

    public static AttritionAssessment Assess(
        DateOnly startDate,
        DateOnly asOf,
        EmployeeSignals signals)
    {
        decimal years = (asOf.DayNumber - startDate.DayNumber) / 365.25m;

        List<RiskFactor> factors = [];

        // One band only. Somebody seven years in scores 45, not 45 + 30 + 12.
        if (years > 6m)
        {
            factors.Add(Factor("Long tenure without role change", LongTenure));
        }
        else if (years > 4m)
        {
            factors.Add(Factor("Long tenure without role change", MidTenure));
        }
        else if (years > 2m)
        {
            factors.Add(Factor("Approaching typical tenure", ApproachingTenure));
        }

        if (signals.OnLeave)
        {
            factors.Add(Factor("Currently on leave", OnLeaveWeight));
        }

        if (signals.OnProbation)
        {
            factors.Add(Factor("Still on probation", ProbationWeight));
        }

        if (signals.FixedTerm)
        {
            factors.Add(Factor("Fixed-term or contract", FixedTermWeight));
        }

        // Stagnation needs some tenure behind it: a new joiner on the bottom
        // grade is a new joiner, not a flight risk.
        if (signals.NoGradeProgression && years > 3m)
        {
            factors.Add(Factor("No grade progression", NoProgressionWeight));
        }

        if (signals.LastPerformanceRating is int rating && rating <= LowRatingAtOrBelow)
        {
            factors.Add(Factor("Low last review rating", LowRatingWeight));
        }

        if (!signals.AbsenceProtected
            && signals.BradfordScore is decimal bradford
            && bradford >= AbsenceScoreAtOrAbove)
        {
            factors.Add(Factor("Frequent short absences", AbsenceWeight));
        }

        int score = Math.Min(100, factors.Sum(factor => factor.Weight));
        AttritionRiskLevel level = LevelFor(score);

        return new AttritionAssessment
        {
            Score = score,
            Level = level,

            // Never an empty list. A row saying nothing about somebody reads
            // as a row that failed to load.
            Factors = factors.Count > 0 ? factors : [Factor("Standard monitoring", 0)],
            RecommendedAction = RecommendedAction(level, factors),
        };
    }

    // Routed off the heaviest factor rather than off the band. The band says
    // how urgent it is; the factor says what to actually do about it, and
    // "monitor" against every name is what this replaced.
    public static string RecommendedAction(
        AttritionRiskLevel level,
        IReadOnlyList<RiskFactor> factors)
    {
        if (level == AttritionRiskLevel.Low)
        {
            return "No action needed";
        }

        string top = factors
            .OrderByDescending(factor => factor.Weight)
            .Select(factor => factor.Label)
            .FirstOrDefault() ?? string.Empty;

        if (Mentions(top, "salary", "market"))
        {
            return "Salary benchmarking review";
        }

        if (Mentions(top, "promotion", "tenure", "role change", "progression"))
        {
            return level == AttritionRiskLevel.High
                ? "Career development discussion"
                : "Review progression opportunities";
        }

        if (Mentions(top, "performance", "rating"))
        {
            return "Performance support plan";
        }

        if (Mentions(top, "engagement", "survey"))
        {
            return "1:1 engagement check-in";
        }

        if (Mentions(top, "learning", "training"))
        {
            return "Learning & Development plan";
        }

        if (Mentions(top, "absence", "absenteeism"))
        {
            return "Wellbeing conversation";
        }

        if (Mentions(top, "manager"))
        {
            return "Manager transition support";
        }

        return level == AttritionRiskLevel.High
            ? "Career development discussion"
            : "Review progression opportunities";
    }

    private static RiskFactor Factor(string label, int weight) =>
        new() { Label = label, Weight = weight };

    private static bool Mentions(string label, params string[] words) =>
        Array.Exists(words, word => label.Contains(word, StringComparison.OrdinalIgnoreCase));
}
