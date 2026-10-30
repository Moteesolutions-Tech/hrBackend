using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Recruitment;
using Motee.Domain.Authorization;
using Motee.Domain.Recruitment;

namespace Motee.Api.Controllers;

// Applicants and their progress.
//
// /can-move exists so the board can ask before it lets a card be dropped. The
// refusal then reaches the user as a sentence explaining which gate stopped it,
// rather than as a card snapping back for no stated reason.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/candidates")]
public class CandidatesController(ICandidateService candidates) : ApiControllerBase
{
    private const string Module = "talent.recruitment";

    [HttpGet]
    [ProducesResponseType<PagedResult<CandidateDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] CandidateQuery query,
        CancellationToken cancellationToken) =>
        Ok(await candidates.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<CandidateDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        CandidateDto? candidate = await candidates.GetAsync(id, cancellationToken);

        return candidate is null
            ? Failure<CandidateDto>(MoteeStatusCodes.NotFound, "Candidate not found.")
            : Ok(candidate);
    }

    [HttpPost]
    [ProducesResponseType<CandidateDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        CandidateRequest request,
        CancellationToken cancellationToken)
    {
        CandidateResult result = await candidates.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Candidate!, "Application recorded.")
            : Failure<CandidateDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<CandidateDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        CandidateRequest request,
        CancellationToken cancellationToken) =>
        Respond(await candidates.UpdateAsync(id, request, cancellationToken), "Candidate updated.");

    // Asked before a card is dropped, so the board can refuse with a reason
    // instead of snapping back.
    [HttpGet("{id:guid}/can-move")]
    [ProducesResponseType<MoveVerdictDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> CanMove(
        Guid id,
        [FromQuery] CandidateStage to,
        CancellationToken cancellationToken)
    {
        MoveRefusal refusal = await candidates.CanMoveAsync(id, to, cancellationToken);

        return Ok(new MoveVerdictDto
        {
            Allowed = refusal == MoveRefusal.None,
            Refusal = refusal,
            Reason = refusal == MoveRefusal.None ? null : ReasonFor(refusal),
        });
    }

    [HttpPost("{id:guid}/move")]
    [ProducesResponseType<CandidateDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Move(
        Guid id,
        MoveCandidateRequest request,
        CancellationToken cancellationToken) =>
        Respond(await candidates.MoveAsync(id, request.To, cancellationToken), "Candidate moved.");

    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<CandidateDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Reject(
        Guid id,
        RejectCandidateRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await candidates.RejectAsync(id, request.Reason, cancellationToken),
            "Candidate rejected.");

    [HttpPost("{id:guid}/restore")]
    [ProducesResponseType<CandidateDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken) =>
        Respond(await candidates.RestoreAsync(id, cancellationToken), "Candidate restored.");

    [HttpPost("{id:guid}/owner")]
    [ProducesResponseType<CandidateDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> AssignOwner(
        Guid id,
        AssignOwnerRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await candidates.AssignOwnerAsync(id, request.OwnerEmployeeId, cancellationToken),
            "Owner assigned.");

    [HttpPost("{id:guid}/scorecards")]
    [ProducesResponseType<CandidateDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Score(
        Guid id,
        ScorecardRequest request,
        CancellationToken cancellationToken) =>
        Respond(await candidates.ScoreAsync(id, request, cancellationToken), "Scorecard saved.");

    [HttpPost("{id:guid}/offers")]
    [ProducesResponseType<CandidateDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SendOffer(
        Guid id,
        OfferRequest request,
        CancellationToken cancellationToken) =>
        Respond(await candidates.SendOfferAsync(id, request, cancellationToken), "Offer recorded.");

    // What the candidate said. The conversation happens over email; this is
    // where its outcome is written down.
    [HttpPost("{id:guid}/offers/{offerId:guid}/respond")]
    [ProducesResponseType<CandidateDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> RespondToOffer(
        Guid id,
        Guid offerId,
        OfferResponseRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await candidates.RespondToOfferAsync(id, offerId, request, cancellationToken),
            "Response recorded.");

    // Creates the employee record, sends the join invitation and opens the
    // onboarding, in one call. The link the applicant → offer → hired →
    // onboarding → employee chain was missing: before it, somebody retyped the
    // hire's details into the employee form, which is how a name gets spelled
    // two ways and a start date gets lost.
    [HttpPost("{id:guid}/send-to-onboarding")]
    [ProducesResponseType<CandidateDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SendToOnboarding(
        Guid id,
        CancellationToken cancellationToken) =>
        Respond(
            await candidates.SendToOnboardingAsync(id, cancellationToken),
            "Invited to onboarding.");

    private IActionResult Respond(CandidateResult result, string message) =>
        result.Succeeded
            ? Ok(result.Candidate!, message)
            : Failure<CandidateDto>(StatusFor(result), MessageFor(result));

    private static string StatusFor(CandidateResult result) => result.Outcome switch
    {
        CandidateOutcome.NotFound or CandidateOutcome.RequisitionNotFound
            or CandidateOutcome.OfferNotFound => MoteeStatusCodes.NotFound,

        CandidateOutcome.AlreadyApplied or CandidateOutcome.NotAcceptingApplications
            or CandidateOutcome.OfferOutstanding or CandidateOutcome.OfferAlreadyAnswered
            or CandidateOutcome.NotRejected or CandidateOutcome.MoveRefused
            or CandidateOutcome.NotHired or CandidateOutcome.AlreadyOnboarding
            or CandidateOutcome.CouldNotCreateEmployee => MoteeStatusCodes.Conflict,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(CandidateResult result) => result.Outcome switch
    {
        CandidateOutcome.MoveRefused => ReasonFor(result.Refusal),
        CandidateOutcome.NotFound => "Candidate not found.",
        CandidateOutcome.RequisitionNotFound => "Vacancy not found.",
        CandidateOutcome.NameRequired => "A candidate needs a first and last name.",
        CandidateOutcome.EmailRequired => "A candidate needs an email address.",
        CandidateOutcome.EmailInvalid => "That email address does not look right.",
        CandidateOutcome.AlreadyApplied =>
            "This person has already applied to this vacancy.",
        CandidateOutcome.NotAcceptingApplications =>
            "This vacancy is not taking applications.",
        CandidateOutcome.ReasonRequired => "Say why they are being rejected.",
        CandidateOutcome.NotRejected => "This candidate is still in the running.",
        CandidateOutcome.CriteriaRequired => "A scorecard needs at least one line.",
        CandidateOutcome.ScoreOutOfRange => "Scores run from 1 to 5.",
        CandidateOutcome.OfferOutstanding =>
            "There is already an offer out. Record their answer, or withdraw it first.",
        CandidateOutcome.OfferNotFound => "Offer not found.",
        CandidateOutcome.OfferAlreadyAnswered => "That offer has already been answered.",
        CandidateOutcome.NotAnAnswer =>
            "Sent is where an offer starts. Record accepted, rejected or withdrawn.",
        CandidateOutcome.NotHired =>
            "Only a hired candidate can be sent to onboarding.",
        CandidateOutcome.AlreadyOnboarding =>
            "This hire has already been invited to onboarding.",
        CandidateOutcome.DepartmentRequired =>
            "The vacancy has no department, and an employee record needs one. "
            + "Set it on the vacancy first.",
        CandidateOutcome.CouldNotCreateEmployee =>
            "Could not create their employee record — somebody may already work here "
            + "under that email address.",
        _ => "Could not save the candidate.",
    };

    // Written as sentences because they are shown verbatim: somebody dragging
    // a card needs to know what to do next, not which enum stopped them.
    private static string ReasonFor(MoveRefusal refusal) => refusal switch
    {
        MoveRefusal.AlreadyThere => "They are already in that stage.",
        MoveRefusal.Rejected => "They have been rejected. Restore them first.",
        MoveRefusal.StageNotEnabled => "That stage is not enabled for this vacancy.",
        MoveRefusal.SkipsAStage => "Move them through the next stage first.",
        MoveRefusal.NotScored => "Score their interview before moving them on.",
        MoveRefusal.NoOffer => "They have not been sent an offer yet.",
        MoveRefusal.OfferNotAccepted => "They have not accepted their offer yet.",
        MoveRefusal.OfferDeclined => "They declined their offer, so they cannot be hired.",
        MoveRefusal.NoOpeningsLeft =>
            "Every opening on this vacancy is filled. Raise the headcount first.",
        _ => "That move is not allowed.",
    };
}

public sealed record MoveVerdictDto
{
    public required bool Allowed { get; init; }

    public required MoveRefusal Refusal { get; init; }

    // Shown verbatim, so it is a sentence rather than a label.
    public string? Reason { get; init; }
}

public sealed record MoveCandidateRequest
{
    public required CandidateStage To { get; init; }
}

public sealed record RejectCandidateRequest
{
    public required string Reason { get; init; }
}

public sealed record AssignOwnerRequest
{
    public Guid? OwnerEmployeeId { get; init; }
}
