using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Api.Http;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Health;
using Motee.Domain.Authorization;
using Motee.Domain.Health;

namespace Motee.Api.Controllers;

// Occupational health: asking a clinician whether somebody is fit for work, and
// what would help.
//
// Every read on this controller comes in two forms, and which one a caller gets
// is decided by their data scope — not by an action on the module. A line
// manager holds edit on their own reports generally, so action-based gating
// would hand them the confidential view; the scope is the signal this codebase
// already uses to mean "a manager looking at their own team" as against HR. The
// sickness module settles the same question the same way.
//
// The narrow form is a different shape rather than the same shape with fields
// removed: OhManagerView has nowhere to put the case notes, so this cannot leak
// them by somebody forgetting a line.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/occupational-health")]
public class OccupationalHealthController(
    IOhService cases,
    IUserPermissions userPermissions) : ApiControllerBase
{
    private const string Module = "time-payroll.occupational-health";

    [HttpGet]
    [ProducesResponseType<PagedResult<OhCaseDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Board(
        [FromQuery] OhCaseQuery query,
        CancellationToken cancellationToken) =>
        await CanViewConfidentialAsync(cancellationToken)
            ? Ok(await cases.BoardAsync(query, cancellationToken))
            : Ok(await cases.ManagerBoardAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<OhCaseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Case(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanViewConfidentialAsync(cancellationToken))
        {
            OhManagerView? narrow = await cases.ManagerCaseAsync(id, cancellationToken);

            return narrow is null
                ? Failure<OhManagerView>(MoteeStatusCodes.NotFound, "Case not found.")
                : Ok(narrow);
        }

        OhCaseDto? full = await cases.CaseAsync(id, cancellationToken);

        return full is null
            ? Failure<OhCaseDto>(MoteeStatusCodes.NotFound, "Case not found.")
            : Ok(full);
    }

    [HttpGet("summary")]
    [ProducesResponseType<OhTally>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await cases.SummaryAsync(cancellationToken));

    // Absences past the referral threshold that nobody has raised a referral
    // for. The frontend cannot produce this — its board is a list of referrals,
    // so an absence with no referral is invisible on it, which is exactly the
    // case this module exists to catch.
    [HttpGet("candidates")]
    [ProducesResponseType<IReadOnlyList<OhCandidateDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Candidates(CancellationToken cancellationToken) =>
        Ok(await cases.CandidatesAsync(cancellationToken));

    // Edit rather than View: the history carries what somebody wrote on the
    // case, so it belongs with the confidential view and not with a manager's.
    [HttpGet("{id:guid}/history")]
    [ProducesResponseType<IReadOnlyList<OhHistoryDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> History(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanViewConfidentialAsync(cancellationToken))
        {
            return Failure<IReadOnlyList<OhHistoryDto>>(
                MoteeStatusCodes.Forbidden,
                "A case's history is readable by HR and occupational health staff only.");
        }

        return Ok(await cases.HistoryAsync(id, cancellationToken));
    }

    [HttpPost]
    [ProducesResponseType<OhCaseDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Raise(
        OhReferralRequest request,
        CancellationToken cancellationToken)
    {
        OhResult result = await cases.RaiseAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Case!, "Case opened.")
            : Failure<OhCaseDto>(StatusFor(result.Outcome), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<OhCaseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        OhReferralRequest request,
        CancellationToken cancellationToken) =>
        Respond(await cases.UpdateAsync(id, request, cancellationToken), "Case updated.");

    // Its own route rather than a field on the update, because each stage has
    // its own precondition — referring needs a referral date, recommendations
    // need a fitness opinion — and a stage buried in a form is one that gets
    // moved by accident.
    [HttpPost("{id:guid}/stage")]
    [ProducesResponseType<OhCaseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Move(
        Guid id,
        OhStageRequest request,
        CancellationToken cancellationToken) =>
        Respond(await cases.MoveAsync(id, request, cancellationToken), "Case moved on.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        OhOutcome outcome = await cases.RemoveAsync(id, cancellationToken);

        return outcome == OhOutcome.Succeeded
            ? Ok<object?>(null, "Case removed.")
            : Failure<object?>(MoteeStatusCodes.NotFound, "Case not found.");
    }

    // ── adjustments ────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/adjustments")]
    [ProducesResponseType<OhCaseDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> AddAdjustment(
        Guid id,
        AdjustmentRequest request,
        CancellationToken cancellationToken)
    {
        OhResult result = await cases.AddAdjustmentAsync(id, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Case!, "Adjustment proposed.")
            : Failure<OhCaseDto>(StatusFor(result.Outcome), MessageFor(result));
    }

    [HttpPut("{id:guid}/adjustments/{adjustmentId:guid}")]
    [ProducesResponseType<OhCaseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> UpdateAdjustment(
        Guid id,
        Guid adjustmentId,
        AdjustmentRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await cases.UpdateAdjustmentAsync(id, adjustmentId, request, cancellationToken),
            "Adjustment updated.");

    // Agreed, implemented, reviewed — its own route, because an adjustment is a
    // commitment and each step has a precondition: agreeing needs an owner,
    // implementing needs a date it started.
    [HttpPost("{id:guid}/adjustments/{adjustmentId:guid}/stage")]
    [ProducesResponseType<OhCaseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> MoveAdjustment(
        Guid id,
        Guid adjustmentId,
        AdjustmentStageRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await cases.MoveAdjustmentAsync(id, adjustmentId, request, cancellationToken),
            "Adjustment updated.");

    [HttpDelete("{id:guid}/adjustments/{adjustmentId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> RemoveAdjustment(
        Guid id,
        Guid adjustmentId,
        CancellationToken cancellationToken)
    {
        OhOutcome outcome = await cases.RemoveAdjustmentAsync(
            id, adjustmentId, cancellationToken);

        return outcome == OhOutcome.Succeeded
            ? Ok<object?>(null, "Adjustment removed.")
            : Failure<object?>(MoteeStatusCodes.NotFound, "Adjustment not found.");
    }

    // Keyed off the data scope, not the action. Mirrors SicknessController,
    // which settles the same question for the same reason — and fails closed,
    // unlike the client's version, which falls open to the full view while
    // access is still resolving.
    private async Task<bool> CanViewConfidentialAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(MoteeClaimTypes.Subject)?.Value, out Guid userId))
        {
            return false;
        }

        ResolvedPermissions held = await userPermissions.ForAsync(userId, cancellationToken);

        if (held.IsOwner)
        {
            return true;
        }

        DataScope scope = AccessDecision.Resolve(
            held.Permissions, Module, PermissionAction.Edit);

        return scope.Kind is not (DataScopeKind.None
            or DataScopeKind.Self
            or DataScopeKind.DirectReports);
    }

    private IActionResult Respond(OhResult result, string message) =>
        result.Succeeded
            ? Ok(result.Case!, message)
            : Failure<OhCaseDto>(StatusFor(result.Outcome), MessageFor(result));

    private static string StatusFor(OhOutcome outcome) => outcome switch
    {
        OhOutcome.NotFound => MoteeStatusCodes.NotFound,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(OhResult result) => result.Outcome switch
    {
        OhOutcome.NotFound => "Case not found.",
        _ => result.Refusal switch
        {
            OhRefusal.EmployeeNotFound => "That person does not work here.",
            OhRefusal.AlreadyOpen =>
                "That person already has an open case. Close it before opening another.",
            OhRefusal.StageNotReachable => "That is not a move this case can make.",
            OhRefusal.ReferralDateRequired =>
                "Record the date before moving the case on — a step with no date is one "
                + "nobody can evidence.",
            OhRefusal.AssessmentDateRequired => "Record the assessment date.",
            OhRefusal.FitnessRequired =>
                "Record the clinician's fitness opinion — recommendations without one mean "
                + "the advice has not arrived.",
            OhRefusal.AssessmentBeforeReferral =>
                "An assessment cannot have happened before the referral.",
            OhRefusal.ReturnBeforeAbsenceStart =>
                "An expected return cannot be before the absence started.",
            OhRefusal.DescriptionRequired =>
                "Record the adjustment itself before marking it in place.",
            OhRefusal.AdjustmentStageNotReachable =>
                "That is not a move this adjustment can make.",
            OhRefusal.OwnerNotFound =>
                "Name who owns it — an agreed adjustment with no owner is the one that "
                + "quietly never happens.",
            OhRefusal.NotesTooLong =>
                $"Case notes can be at most {OhRules.MaximumNotesLength} characters.",
            _ => "That change was not accepted.",
        },
    };
}
