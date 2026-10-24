using Motee.Application.Common;
using Motee.Domain.Performance;

namespace Motee.Application.Performance;

// Reviews: opened by HR, written by two people, signed off by one.
//
// The halves are deliberately separate calls. An employee saving their self-
// assessment and a manager recording a rating are different acts by different people
// on the same record, and one method taking everything would let either overwrite
// the other's half.
public interface IPerformanceReviewService
{
    // ── the employee's half ─────────────────────────────────────────────────

    Task<IReadOnlyList<PerformanceReviewDto>> MineAsync(
        CancellationToken cancellationToken = default);

    // Save and continue later. Leaves the review waiting on them rather than on
    // their manager, which is what the unset submission date means.
    Task<ReviewResult> SaveSelfAssessmentAsync(
        Guid id,
        SelfAssessmentRequest request,
        CancellationToken cancellationToken = default);

    // Hand it in. Needs a rating and something written, because a blank assessment
    // submitted is worse than one still open — the manager has nothing to read and
    // no prompt to chase.
    Task<ReviewResult> SubmitSelfAssessmentAsync(
        Guid id,
        SelfAssessmentRequest request,
        CancellationToken cancellationToken = default);

    // ── the manager's and HR's half ─────────────────────────────────────────

    Task<PagedResult<PerformanceReviewDto>> ListAsync(
        ReviewQuery query,
        CancellationToken cancellationToken = default);

    Task<PerformanceReviewDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ReviewResult> OpenAsync(
        OpenReviewRequest request,
        CancellationToken cancellationToken = default);

    Task<ReviewResult> RecordManagerReviewAsync(
        Guid id,
        ManagerReviewRequest request,
        CancellationToken cancellationToken = default);

    // Signs it off. Needs a manager rating — completing a review nobody rated
    // leaves a finished record that says nothing.
    Task<ReviewResult> CompleteAsync(Guid id, CancellationToken cancellationToken = default);

    // HR's adjustment after comparing managers against each other. Only on a
    // finished review: calibrating one still being written is calibrating nothing.
    Task<ReviewResult> CalibrateAsync(
        Guid id,
        int rating,
        CancellationToken cancellationToken = default);

    Task<ReviewOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record SelfAssessmentDto
{
    public string? Achievements { get; init; }

    public string? Challenges { get; init; }

    public string? DevelopmentAreas { get; init; }

    public string? ManagerFeedback { get; init; }
}

public sealed record PerformanceReviewDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? JobTitle { get; init; }

    public string? DepartmentName { get; init; }

    public Guid? ReviewerId { get; init; }

    public string? ReviewerName { get; init; }

    public required ReviewType ReviewType { get; init; }

    public required string Period { get; init; }

    public required ReviewStatus Status { get; init; }

    // Past its date and unfinished. Derived, so it becomes true at midnight and
    // stops the moment the review is signed off.
    public required bool IsOverdue { get; init; }

    public required DateOnly DueDate { get; init; }

    public SelfAssessmentDto? SelfAssessment { get; init; }

    public int? SelfRating { get; init; }

    public DateTimeOffset? SelfSubmittedAt { get; init; }

    public int? ManagerRating { get; init; }

    public string? Strengths { get; init; }

    public string? Improvements { get; init; }

    public string? Comments { get; init; }

    public int? CalibratedRating { get; init; }

    // The one that counts: the calibration when there is one, the manager's
    // otherwise. Served rather than left to the client, which would otherwise write
    // that rule four times and get it wrong somewhere.
    public int? Rating { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record OpenReviewRequest
{
    public required Guid EmployeeId { get; init; }

    public Guid? ReviewerId { get; init; }

    public required ReviewType ReviewType { get; init; }

    public required string Period { get; init; }

    public required DateOnly DueDate { get; init; }
}

public sealed record SelfAssessmentRequest
{
    public string? Achievements { get; init; }

    public string? Challenges { get; init; }

    public string? DevelopmentAreas { get; init; }

    public string? ManagerFeedback { get; init; }

    public int? SelfRating { get; init; }
}

public sealed record ManagerReviewRequest
{
    public int? ManagerRating { get; init; }

    public string? Strengths { get; init; }

    public string? Improvements { get; init; }

    public string? Comments { get; init; }
}

public sealed record ReviewQuery : PagedQuery
{
    public Guid? EmployeeId { get; init; }

    public Guid? ReviewerId { get; init; }

    public Guid? DepartmentId { get; init; }

    public ReviewStatus? Status { get; init; }

    public ReviewType? ReviewType { get; init; }

    public bool? OverdueOnly { get; init; }
}

public enum ReviewOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,
    ReviewerNotFound,

    // Outside one to five.
    RatingOutOfRange,

    // Already handed in. Letting it happen twice would move the submission date and
    // make a late assessment look punctual.
    AlreadySubmitted,

    // Nothing written, or no rating. A blank assessment handed in is worse than one
    // still open: the manager has nothing to read and no prompt to chase.
    AssessmentIncomplete,

    // Signing off a review nobody rated leaves a finished record saying nothing.
    RatingRequired,

    // Finished. Reopening would leave CompletedAt describing a completion that no
    // longer happened — the same rule goals follow.
    AlreadyCompleted,

    // Calibrating a review still being written is calibrating nothing.
    NotCompleted,

    PeriodRequired,
}

public sealed record ReviewResult
{
    public required ReviewOutcome Outcome { get; init; }

    public PerformanceReviewDto? Review { get; init; }

    public bool Succeeded => Outcome == ReviewOutcome.Succeeded;

    public static ReviewResult Failed(ReviewOutcome outcome) => new() { Outcome = outcome };

    public static ReviewResult Ok(PerformanceReviewDto review) =>
        new() { Outcome = ReviewOutcome.Succeeded, Review = review };
}
