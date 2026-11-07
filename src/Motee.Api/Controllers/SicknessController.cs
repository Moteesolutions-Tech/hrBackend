using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Sickness;
using Motee.Domain.Authorization;
using Motee.Domain.Identity;
using Motee.Domain.Sickness;

namespace Motee.Api.Controllers;

// Absence and sickness.
//
// The interesting thing here is not the routes, it is what comes back in
// them. Holding the module lets a line manager see that their team is off;
// it does not let them read why. The clinical category and the employee's
// own words need Edit on the module with reach beyond the holder's own team
// — the §10.8 rule the frontend applies, resolved here so it holds for every
// client rather than for the one that remembers.
//
// Resolved per request rather than as an attribute because it is not a yes/no
// on the endpoint: the same read serves both audiences, with less in it for
// one of them.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/sickness")]
public class SicknessController(
    ISicknessService sickness,
    IUserPermissions userPermissions) : ApiControllerBase
{
    private const string Module = "time-payroll.sickness";

    [HttpGet]
    [ProducesResponseType<PagedResult<SicknessRecordDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] SicknessQuery query,
        CancellationToken cancellationToken) =>
        Ok(await sickness.ListAsync(
            query, await CanViewMedicalAsync(cancellationToken), cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<SicknessRecordDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        SicknessRecordDto? record = await sickness.GetAsync(
            id, await CanViewMedicalAsync(cancellationToken), cancellationToken);

        return record is null
            ? Failure<SicknessRecordDto>(MoteeStatusCodes.NotFound, "Absence not found.")
            : Ok(record);
    }

    // The figures every sickness screen shows. One read, so the records page,
    // the dashboard tile and the alerts page cannot disagree about how many
    // days the company lost.
    [HttpGet("summary")]
    [ProducesResponseType<SicknessSummaryDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(
        [FromQuery] DateOnly? from,
        CancellationToken cancellationToken) =>
        Ok(await sickness.SummaryAsync(from, cancellationToken));

    [HttpGet("alerts")]
    [ProducesResponseType<IReadOnlyList<SicknessAlertDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Alerts(CancellationToken cancellationToken) =>
        Ok(await sickness.AlertsAsync(cancellationToken));

    [HttpPost]
    [ProducesResponseType<SicknessRecordDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Report(
        SicknessRequest request,
        CancellationToken cancellationToken)
    {
        SicknessResult result = await sickness.ReportAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Record!, "Absence recorded.")
            : Failure<SicknessRecordDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<SicknessRecordDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        SicknessRequest request,
        CancellationToken cancellationToken) =>
        Respond(await sickness.UpdateAsync(id, request, cancellationToken), "Absence updated.");

    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<SicknessRecordDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Approve(Guid id, CancellationToken cancellationToken) =>
        Respond(await sickness.ApproveAsync(id, cancellationToken), "Absence approved.");

    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<SicknessRecordDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Reject(
        Guid id,
        SicknessReasonRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await sickness.RejectAsync(id, request.Reason, cancellationToken), "Absence rejected.");

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<SicknessRecordDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Cancel(
        Guid id,
        SicknessReasonRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await sickness.CancelAsync(id, request.Reason, cancellationToken), "Absence cancelled.");

    // They are back. Closes the absence and opens the return-to-work.
    [HttpPost("{id:guid}/return")]
    [ProducesResponseType<SicknessRecordDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Return(
        Guid id,
        ReturnRequest request,
        CancellationToken cancellationToken) =>
        Respond(await sickness.ReturnAsync(id, request, cancellationToken), "Return recorded.");

    [HttpPost("{id:guid}/return-to-work")]
    [ProducesResponseType<SicknessRecordDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> CompleteReturnToWork(
        Guid id,
        ReturnToWorkRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await sickness.CompleteReturnToWorkAsync(id, request, cancellationToken),
            "Return-to-work completed.");

    // A fit note is special-category data, so adding one needs the grant that
    // would let the caller read it back.
    [HttpPost("{id:guid}/documents")]
    [ProducesResponseType<SicknessRecordDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> AddDocument(
        Guid id,
        SicknessDocumentRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanViewMedicalAsync(cancellationToken))
        {
            return Failure<SicknessRecordDto>(MoteeStatusCodes.Forbidden, MedicalDenied);
        }

        return Respond(
            await sickness.AddDocumentAsync(id, request, cancellationToken), "Document added.");
    }

    // Taking an absence out of the Bradford score. Needs the medical grant:
    // the decision turns on why somebody was off.
    [HttpPost("{id:guid}/exclusion")]
    [ProducesResponseType<SicknessRecordDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Exclude(
        Guid id,
        ExclusionRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanViewMedicalAsync(cancellationToken))
        {
            return Failure<SicknessRecordDto>(MoteeStatusCodes.Forbidden, MedicalDenied);
        }

        return Respond(
            await sickness.ExcludeAsync(id, request, cancellationToken),
            "Absence excluded from scoring.");
    }

    [HttpDelete("{id:guid}/exclusion")]
    [ProducesResponseType<SicknessRecordDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> ClearExclusion(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!await CanViewMedicalAsync(cancellationToken))
        {
            return Failure<SicknessRecordDto>(MoteeStatusCodes.Forbidden, MedicalDenied);
        }

        return Respond(
            await sickness.ClearExclusionAsync(id, cancellationToken), "Exclusion lifted.");
    }

    // ── Bradford ────────────────────────────────────────────────────────────

    // The score, with the spells it came from and what was left out. The
    // working is part of the answer: a number quoted at somebody in a meeting
    // that nobody can reproduce is not evidence of anything.
    [HttpGet("bradford/{employeeId:guid}")]
    [ProducesResponseType<BradfordDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Bradford(
        Guid employeeId,
        CancellationToken cancellationToken) =>
        Ok(await sickness.BradfordAsync(employeeId, cancellationToken));

    [HttpGet("bradford")]
    [ProducesResponseType<IReadOnlyList<BradfordRowDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> BradfordBoard(
        [FromQuery] BradfordBoardQuery query,
        CancellationToken cancellationToken) =>
        Ok(await sickness.BradfordBoardAsync(query, cancellationToken));

    private const string MedicalDenied =
        "Medical detail on an absence needs the sickness permission with reach "
        + "beyond your own team.";

    private IActionResult Respond(SicknessResult result, string message) =>
        result.Succeeded
            ? Ok(result.Record!, message)
            : Failure<SicknessRecordDto>(StatusFor(result), MessageFor(result));

    // Edit on the module, with a scope that reaches past the caller's own
    // team. A manager holding the module for their reports sees the absence
    // and not the reason, which is the point of the rule.
    private async Task<bool> CanViewMedicalAsync(CancellationToken cancellationToken)
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

    private static string StatusFor(SicknessResult result) => result.Outcome switch
    {
        SicknessOutcome.NotFound or SicknessOutcome.EmployeeNotFound =>
            MoteeStatusCodes.NotFound,

        SicknessOutcome.OverlapsAnother => MoteeStatusCodes.Conflict,

        SicknessOutcome.Refused => ConflictOrRequest(result.Refusal),

        _ => MoteeStatusCodes.InvalidRequest,
    };

    // A refusal about the record's state is a conflict; one about what was
    // sent is a bad request.
    private static string ConflictOrRequest(SicknessRefusal refusal) => refusal switch
    {
        SicknessRefusal.LastDayBeforeStart or SicknessRefusal.ReturnBeforeLastDay
            or SicknessRefusal.SafetyNotConfirmed or SicknessRefusal.InterviewNotHeld =>
            MoteeStatusCodes.InvalidRequest,

        _ => MoteeStatusCodes.Conflict,
    };

    private static string MessageFor(SicknessResult result) => result.Outcome switch
    {
        SicknessOutcome.NotFound => "Absence not found.",
        SicknessOutcome.EmployeeNotFound => "That person does not work here.",
        SicknessOutcome.StartDateRequired => "An absence needs a start date.",
        SicknessOutcome.EndBeforeStart => "The absence ends before it starts.",
        SicknessOutcome.ReasonRequired => "Say why, in a sentence.",
        SicknessOutcome.DocumentNameRequired => "Give the document a name.",
        SicknessOutcome.OverlapsAnother =>
            "This person already has an absence over those days. "
            + "Edit that one rather than adding a second.",
        _ => ReasonFor(result.Refusal),
    };

    // Sentences, because they are shown as written.
    private static string ReasonFor(SicknessRefusal refusal) => refusal switch
    {
        SicknessRefusal.NotAwaitingApproval => "This absence has already been decided.",
        SicknessRefusal.AlreadyClosed => "This absence was rejected or cancelled.",
        SicknessRefusal.AlreadyReturned => "This absence is already closed.",
        SicknessRefusal.LastDayBeforeStart =>
            "The last sick day is before the absence started.",
        SicknessRefusal.ReturnBeforeLastDay =>
            "They cannot be back on or before their last day off.",
        SicknessRefusal.StillOngoing =>
            "Record their return first — there is no return-to-work until they are back.",
        SicknessRefusal.AlreadyCompleted => "This return-to-work is already signed off.",
        SicknessRefusal.InterviewNotHeld =>
            "This absence needs a return-to-work interview before it can be closed.",
        SicknessRefusal.SafetyNotConfirmed =>
            "Confirm they are fit to be back before completing the return-to-work.",
        _ => "Could not save the absence.",
    };
}

public sealed record SicknessReasonRequest
{
    public required string Reason { get; init; }
}
