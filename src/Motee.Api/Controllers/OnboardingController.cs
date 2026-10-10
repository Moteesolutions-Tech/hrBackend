using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Onboarding;
using Motee.Domain.Authorization;
using Motee.Domain.Onboarding;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/onboarding")]
public class OnboardingController(IOnboardingService onboarding) : ApiControllerBase
{
    private const string Module = "talent.onboarding";

    [HttpGet]
    [ProducesResponseType<PagedResult<OnboardingDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] OnboardingQuery query,
        CancellationToken cancellationToken) =>
        Ok(await onboarding.ListAsync(query, cancellationToken));

    // The stages a record can be moved to. Served rather than hard-coded in the client for
    // the same reason as the approvals catalogue: the list is the backend's to decide.
    [HttpGet("catalogue")]
    [ProducesResponseType<OnboardingCatalogue>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public IActionResult Catalogue() =>
        Ok(new OnboardingCatalogue
        {
            Stages = Enum.GetNames<OnboardingStage>(),
            Submissions = Enum.GetNames<OnboardingSubmission>(),
        });

    // The joiner's own record. No permission attribute: this is theirs, and requiring the
    // onboarding module would mean granting every new hire the screen that lists everyone
    // else's onboarding too.
    [HttpGet("mine")]
    [ProducesResponseType<OnboardingDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken)
    {
        OnboardingDto? record = await onboarding.MineAsync(cancellationToken);

        return record is null
            ? Failure<OnboardingDto>(MoteeStatusCodes.NotFound, "No onboarding record found.")
            : Ok(record);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<OnboardingDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        OnboardingDto? record = await onboarding.GetAsync(id, cancellationToken);

        return record is null
            ? Failure<OnboardingDto>(MoteeStatusCodes.NotFound, "Onboarding record not found.")
            : Ok(record);
    }

    [HttpPost("{id:guid}/stage")]
    [ProducesResponseType<OnboardingDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Move(
        Guid id,
        MoveStageRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await onboarding.MoveAsync(id, request.Stage, cancellationToken),
            "Onboarding stage updated.");

    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType<OnboardingDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Complete(Guid id, CancellationToken cancellationToken) =>
        Respond(await onboarding.CompleteAsync(id, cancellationToken), "Onboarding completed.");

    private IActionResult Respond(OnboardingResult result, string message) =>
        result.Succeeded
            ? Ok(result.Record!, message)
            : Failure<OnboardingDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));

    private static string StatusFor(OnboardingOutcome outcome) => outcome switch
    {
        OnboardingOutcome.NotFound => MoteeStatusCodes.NotFound,
        OnboardingOutcome.NotAllowed or OnboardingOutcome.ReviewIncomplete =>
            MoteeStatusCodes.Conflict,
        OnboardingOutcome.ReviewUnstartable => MoteeStatusCodes.InvalidRequest,
        _ => MoteeStatusCodes.InternalServerError,
    };

    private static string MessageFor(OnboardingOutcome outcome) => outcome switch
    {
        OnboardingOutcome.NotFound => "Onboarding record not found.",
        OnboardingOutcome.NotAllowed => "That is not a move this onboarding can make.",

        // Says where it is stuck, because "not allowed" would send HR looking for a
        // permission problem when what they need is to chase an approver.
        OnboardingOutcome.ReviewIncomplete =>
            "This joiner's submission has not been approved yet.",
        OnboardingOutcome.ReviewUnstartable =>
            "No approval chain could be started for onboarding. Check the workflow "
            + "configuration.",
        _ => "Could not update the onboarding record.",
    };
}

public sealed record MoveStageRequest
{
    public required OnboardingStage Stage { get; init; }
}

// A named type rather than an anonymous object, so the published document can describe
// it. An anonymous payload reaches the frontend as "data": null — and these are the
// dropdown values a screen is built from, which is the worst thing to leave undocumented.
public sealed record OnboardingCatalogue
{
    public required IReadOnlyList<string> Stages { get; init; }

    public required IReadOnlyList<string> Submissions { get; init; }
}
