using Motee.Domain.Common;

namespace Motee.Domain.Performance;

public enum ReviewType
{
    Annual,
    MidYear,
    Probation,

    // A performance improvement plan. The same shape as any other review and
    // emphatically not the same thing to be on, which is why it is its own type
    // rather than an annual review with a note on it.
    Pip,

    // Gathered from colleagues as well as the manager.
    ThreeSixty,
}

// Where a review has got to.
//
// Overdue is absent for the same reason it is absent from goals: it is a fact about
// the date rather than a decision, so it is derived on read and cannot be left
// stale by a review that was late and then finished.
public enum ReviewStatus
{
    NotStarted,

    // Somebody has begun — the employee handed in a self-assessment, or the manager
    // started writing. One state for both, because the screen asks "is this moving"
    // rather than "whose turn is it", and whose turn it is can be read from whether
    // the self-assessment is in.
    InProgress,

    Completed,
}

// What the employee says about their own year, before anybody rates them.
//
// Written first on purpose: a manager who reads it before forming a view is the
// whole point of asking, and a self-assessment collected afterwards is a formality.
public sealed record SelfAssessment
{
    public string? Achievements { get; init; }

    public string? Challenges { get; init; }

    public string? DevelopmentAreas { get; init; }

    // What they want from their manager. Phrased as a prompt to them rather than
    // about them, which is the difference between a review and an appraisal.
    public string? ManagerFeedback { get; init; }
}

public class PerformanceReview : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    // Whose judgement this is. An employee rather than a user: a reviewer is
    // somebody's manager, which is a position in the company, not an account.
    public Guid? ReviewerId { get; set; }

    public ReviewType ReviewType { get; set; }

    // The stretch being reviewed, in the company's own words — "H1 2026",
    // "Year to March 2026". Free text because a review period is whatever the
    // company runs, and parsing it would mean refusing the ones that do not fit.
    public required string Period { get; set; }

    public ReviewStatus Status { get; set; } = ReviewStatus.NotStarted;

    public DateOnly DueDate { get; set; }

    // ── the employee's half ─────────────────────────────────────────────────

    public SelfAssessment? SelfAssessment { get; set; }

    public int? SelfRating { get; set; }

    // Set when they hand it in. A saved draft leaves it null, which is what makes
    // "still writing" distinguishable from "waiting on the manager".
    public DateTimeOffset? SelfSubmittedAt { get; set; }

    // ── the manager's half ──────────────────────────────────────────────────

    public int? ManagerRating { get; set; }

    public string? Strengths { get; set; }

    public string? Improvements { get; set; }

    public string? Comments { get; set; }

    // ── calibration ─────────────────────────────────────────────────────────

    // HR's adjustment, after comparing managers against each other.
    //
    // Kept beside the manager's rather than overwriting it. The point of
    // calibration is that one manager marks generously and another harshly; erasing
    // what they each said would destroy the evidence the adjustment was made from,
    // and leave nobody able to answer why a four became a three.
    public int? CalibratedRating { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public static class PerformanceReviews
{
    public const int MinimumRating = 1;

    public const int MaximumRating = 5;

    public static bool IsValidRating(int rating) =>
        rating is >= MinimumRating and <= MaximumRating;

    // The rating that counts: HR's calibration when there is one, the manager's
    // otherwise. Derived rather than stored, so the two cannot drift apart and a
    // calibration never silently becomes the only number anybody can see.
    public static int? FinalRating(PerformanceReview review) =>
        review.CalibratedRating ?? review.ManagerRating;

    public static bool IsOverdue(PerformanceReview review, DateOnly today) =>
        review.DueDate < today && review.Status != ReviewStatus.Completed;
}
