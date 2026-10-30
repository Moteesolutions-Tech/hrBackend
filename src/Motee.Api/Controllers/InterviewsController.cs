using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Recruitment;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Interviews.
//
// Scheduling succeeds even when a panel member is double-booked; the clash
// comes back named on the response so the screen can say "Cara Okafor is
// already booked at that time" and let somebody decide. Refusing outright
// would leave them unable to record a diary they already have.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/interviews")]
public class InterviewsController(IInterviewService interviews) : ApiControllerBase
{
    private const string Module = "talent.recruitment";

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<InterviewDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Upcoming(
        [FromQuery] Guid? requisitionId,
        CancellationToken cancellationToken) =>
        Ok(await interviews.UpcomingAsync(requisitionId, cancellationToken));

    [HttpGet("candidate/{candidateId:guid}")]
    [ProducesResponseType<IReadOnlyList<InterviewDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> ForCandidate(
        Guid candidateId,
        CancellationToken cancellationToken) =>
        Ok(await interviews.ForCandidateAsync(candidateId, cancellationToken));

    [HttpPost]
    [ProducesResponseType<InterviewDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Schedule(
        InterviewRequest request,
        CancellationToken cancellationToken)
    {
        InterviewResult result = await interviews.ScheduleAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Interview!, "Interview scheduled.")
            : Failure<InterviewDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<InterviewDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Reschedule(
        Guid id,
        InterviewRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await interviews.RescheduleAsync(id, request, cancellationToken),
            "Interview rescheduled.");

    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType<InterviewDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Complete(Guid id, CancellationToken cancellationToken) =>
        Respond(await interviews.CompleteAsync(id, cancellationToken), "Interview completed.");

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<InterviewDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken) =>
        Respond(await interviews.CancelAsync(id, cancellationToken), "Interview cancelled.");

    private IActionResult Respond(InterviewResult result, string message) =>
        result.Succeeded
            ? Ok(result.Interview!, message)
            : Failure<InterviewDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));

    private static string StatusFor(InterviewOutcome outcome) => outcome switch
    {
        InterviewOutcome.NotFound or InterviewOutcome.CandidateNotFound =>
            MoteeStatusCodes.NotFound,
        InterviewOutcome.NotScheduled or InterviewOutcome.CandidateRejected =>
            MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(InterviewOutcome outcome) => outcome switch
    {
        InterviewOutcome.NotFound => "Interview not found.",
        InterviewOutcome.CandidateNotFound => "Candidate not found.",
        InterviewOutcome.RoundRequired => "Name the round, such as \"First round\".",
        InterviewOutcome.DurationNotPositive => "An interview needs a length.",
        InterviewOutcome.CandidateRejected =>
            "This candidate has been rejected. Restore them first.",
        InterviewOutcome.NotScheduled =>
            "This interview has already been completed or cancelled.",
        _ => "Could not save the interview.",
    };
}
