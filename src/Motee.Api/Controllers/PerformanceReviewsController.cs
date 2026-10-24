using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Performance;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Reviews: opened by HR, written by two people, signed off by one.
//
// The self-assessment routes are self-service and resolve the review against the
// caller — an id alone would let anybody write their half into somebody else's
// review, which is the one part of this record that must be theirs.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/performance/reviews")]
public class PerformanceReviewsController(IPerformanceReviewService reviews) : ApiControllerBase
{
    private const string Module = "talent.performance";

    [HttpGet("mine")]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<PerformanceReviewDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await reviews.MineAsync(cancellationToken));

    // Save and come back to it. Leaves the review waiting on them rather than on
    // their manager, which is what the unset submission date means.
    [HttpPut("mine/{id:guid}/self-assessment")]
    [RequiresEmployee]
    [ProducesResponseType<PerformanceReviewDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SaveSelfAssessment(
        Guid id,
        SelfAssessmentRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await reviews.SaveSelfAssessmentAsync(id, request, cancellationToken),
            "Draft saved.");

    [HttpPost("mine/{id:guid}/self-assessment")]
    [RequiresEmployee]
    [ProducesResponseType<PerformanceReviewDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SubmitSelfAssessment(
        Guid id,
        SelfAssessmentRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await reviews.SubmitSelfAssessmentAsync(id, request, cancellationToken),
            "Self-assessment submitted.");

    // ── managing ────────────────────────────────────────────────────────────

    [HttpGet]
    [ProducesResponseType<PagedResult<PerformanceReviewDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] ReviewQuery query,
        CancellationToken cancellationToken) =>
        Ok(await reviews.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<PerformanceReviewDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        PerformanceReviewDto? review = await reviews.GetAsync(id, cancellationToken);

        return review is null
            ? Failure<PerformanceReviewDto>(MoteeStatusCodes.NotFound, "Review not found.")
            : Ok(review);
    }

    [HttpPost]
    [ProducesResponseType<PerformanceReviewDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Open(
        OpenReviewRequest request,
        CancellationToken cancellationToken)
    {
        ReviewResult result = await reviews.OpenAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Review!, "Review opened.")
            : Failure<PerformanceReviewDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPut("{id:guid}/manager-review")]
    [ProducesResponseType<PerformanceReviewDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> RecordManagerReview(
        Guid id,
        ManagerReviewRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await reviews.RecordManagerReviewAsync(id, request, cancellationToken),
            "Review saved.");

    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType<PerformanceReviewDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Complete(Guid id, CancellationToken cancellationToken) =>
        Respond(await reviews.CompleteAsync(id, cancellationToken), "Review completed.");

    // HR's adjustment after comparing managers against each other. Kept beside the
    // manager's rating rather than replacing it, so why a four became a three stays
    // answerable.
    [HttpPost("{id:guid}/calibrate")]
    [ProducesResponseType<PerformanceReviewDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Calibrate(
        Guid id,
        CalibrateReviewRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await reviews.CalibrateAsync(id, request.Rating, cancellationToken),
            "Rating calibrated.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        ReviewOutcome outcome = await reviews.DeleteAsync(id, cancellationToken);

        return outcome == ReviewOutcome.Succeeded
            ? Ok<object?>(null, "Review deleted.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    private IActionResult Respond(ReviewResult result, string message) =>
        result.Succeeded
            ? Ok(result.Review!, message)
            : Failure<PerformanceReviewDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));

    private static string StatusFor(ReviewOutcome outcome) => outcome switch
    {
        ReviewOutcome.NotFound or ReviewOutcome.EmployeeNotFound
            or ReviewOutcome.ReviewerNotFound => MoteeStatusCodes.NotFound,
        ReviewOutcome.AlreadySubmitted or ReviewOutcome.AlreadyCompleted
            or ReviewOutcome.NotCompleted => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(ReviewOutcome outcome) => outcome switch
    {
        ReviewOutcome.NotFound => "Review not found.",
        ReviewOutcome.EmployeeNotFound => "Employee not found.",
        ReviewOutcome.ReviewerNotFound => "That reviewer is not an employee here.",
        ReviewOutcome.RatingOutOfRange => "Ratings run from 1 to 5.",
        ReviewOutcome.AlreadySubmitted =>
            "Your self-assessment has already been handed in.",
        ReviewOutcome.AssessmentIncomplete =>
            "Give a rating and say what you achieved before handing it in.",
        ReviewOutcome.RatingRequired =>
            "Record a rating before completing the review.",
        ReviewOutcome.AlreadyCompleted =>
            "This review has been completed. Calibrate the rating rather than reopening it.",
        ReviewOutcome.NotCompleted =>
            "Only a completed review can be calibrated.",
        ReviewOutcome.PeriodRequired => "Say which period this review covers.",
        _ => "Could not save the review.",
    };
}

public sealed record CalibrateReviewRequest
{
    public required int Rating { get; init; }
}
