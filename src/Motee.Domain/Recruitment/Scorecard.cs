using Motee.Domain.Common;

namespace Motee.Domain.Recruitment;

public enum ScorecardRecommendation
{
    StrongNo,
    No,
    Yes,
    StrongYes,
}

// One line on a scorecard: what was assessed and how it went.
public sealed record ScorecardCriterion
{
    public required string Label { get; init; }

    // One to five.
    public required int Score { get; init; }
}

// What an interviewer thought, recorded as the interview's evidence.
//
// The reason this exists as a row rather than a number on the candidate: a
// panel of three produces three opinions, and averaging them into a single
// mutable field loses who said what. It also makes the interview gate
// meaningful — "has somebody actually met this person" is answered by the
// presence of a row, not by whether a field is non-null.
public class Scorecard : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CandidateId { get; set; }

    // Which interview this rates, when it rates one. Null for a rating given
    // outside a scheduled meeting — a portfolio review, a take-home.
    public Guid? InterviewId { get; set; }

    public Guid? ByEmployeeId { get; set; }

    public string? ByName { get; set; }

    public List<ScorecardCriterion> Criteria { get; set; } = [];

    public ScorecardRecommendation? Recommendation { get; set; }

    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public static class Scorecards
{
    // The average of the lines, to one decimal place.
    //
    // Derived rather than stored, so it cannot disagree with the criteria
    // beneath it. An interviewer editing one line and not the total is exactly
    // how a candidate ends up with a headline score nobody can reproduce.
    public static decimal? Overall(IReadOnlyCollection<ScorecardCriterion> criteria) =>
        criteria.Count == 0
            ? null
            : Math.Round(criteria.Average(criterion => (decimal)criterion.Score), 1);

    // What the candidate's score is across every rating they have had. Null
    // when nobody has rated them, which is not the same as zero.
    public static decimal? AverageFor(IReadOnlyCollection<Scorecard> scorecards)
    {
        List<decimal> overalls =
        [
            .. scorecards.Select(scorecard => Overall(scorecard.Criteria)).OfType<decimal>(),
        ];

        return overalls.Count == 0 ? null : Math.Round(overalls.Average(), 1);
    }
}
