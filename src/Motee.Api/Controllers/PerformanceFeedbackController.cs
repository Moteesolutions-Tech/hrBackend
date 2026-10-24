using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Performance;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Feedback, asked for and given.
//
// Nearly all self-service: asking a colleague, answering one, and reading what was
// written about you are all acts by the person themselves. Only the company-wide
// read is administrative, because that one is somebody else's file.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/performance/feedback")]
public class PerformanceFeedbackController(
    IPerformanceFeedbackService feedback) : ApiControllerBase
{
    private const string Module = "talent.performance";

    [HttpGet("mine")]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<PerformanceFeedbackDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AboutMe(CancellationToken cancellationToken) =>
        Ok(await feedback.AboutMeAsync(cancellationToken));

    // Asks the caller has sent out, and asks waiting on them. Two reads rather than
    // one filtered list, because a screen showing one and labelling it the other is
    // the obvious mistake and the hardest to notice.
    [HttpGet("requests/mine")]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<FeedbackRequestDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MyRequests(CancellationToken cancellationToken) =>
        Ok(await feedback.MyRequestsAsync(cancellationToken));

    [HttpGet("requests/awaiting-me")]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<FeedbackRequestDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AwaitingMe(CancellationToken cancellationToken) =>
        Ok(await feedback.AwaitingMeAsync(cancellationToken));

    [HttpPost("requests")]
    [RequiresEmployee]
    [ProducesResponseType<FeedbackRequestDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Ask(
        AskForFeedbackRequest request,
        CancellationToken cancellationToken)
    {
        FeedbackRequestResult result = await feedback.AskAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Request!, "Feedback request sent.")
            : Failure<FeedbackRequestDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPost("requests/{id:guid}/decline")]
    [RequiresEmployee]
    [ProducesResponseType<FeedbackRequestDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Decline(Guid id, CancellationToken cancellationToken)
    {
        FeedbackRequestResult result = await feedback.DeclineAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok(result.Request!, "Request declined.")
            : Failure<FeedbackRequestDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPost]
    [RequiresEmployee]
    [ProducesResponseType<PerformanceFeedbackDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Give(
        GiveFeedbackRequest request,
        CancellationToken cancellationToken)
    {
        FeedbackResult result = await feedback.GiveAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Feedback!, "Feedback sent.")
            : Failure<PerformanceFeedbackDto>(
                StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // Everything written about one person, for whoever is running their review.
    [HttpGet]
    [ProducesResponseType<PagedResult<PerformanceFeedbackDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] FeedbackQuery query,
        CancellationToken cancellationToken) =>
        Ok(await feedback.ListAsync(query, cancellationToken));

    private static string StatusFor(FeedbackOutcome outcome) => outcome switch
    {
        FeedbackOutcome.NotFound or FeedbackOutcome.EmployeeNotFound =>
            MoteeStatusCodes.NotFound,
        FeedbackOutcome.AlreadyAsked or FeedbackOutcome.AlreadyResolved =>
            MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(FeedbackOutcome outcome) => outcome switch
    {
        FeedbackOutcome.NotFound => "That feedback request was not found.",
        FeedbackOutcome.EmployeeNotFound => "That person does not work here.",
        FeedbackOutcome.CannotBeAboutYourself =>
            "Feedback is about somebody else. Your own view goes in your "
            + "self-assessment.",
        FeedbackOutcome.MessageRequired => "Write something before sending it.",
        FeedbackOutcome.AlreadyAsked =>
            "You have already asked them, and they have not answered yet.",
        FeedbackOutcome.AlreadyResolved => "That request has already been answered.",
        _ => "Could not send the feedback.",
    };
}
