using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Offboarding;
using Motee.Domain.Authorization;
using Motee.Domain.Offboarding;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/offboarding")]
public class OffboardingController(IOffboardingService offboarding) : ApiControllerBase
{
    private const string Module = "talent.offboarding";

    [HttpGet]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] OffboardingQuery query,
        CancellationToken cancellationToken) =>
        Ok(await offboarding.ListAsync(query, cancellationToken));

    [HttpGet("stats")]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Stats(CancellationToken cancellationToken) =>
        Ok(await offboarding.StatsAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        OffboardingDto? record = await offboarding.GetAsync(id, cancellationToken);

        return record is null
            ? Failure<OffboardingDto>(MoteeStatusCodes.NotFound, "Offboarding record not found.")
            : Ok(record);
    }

    [HttpPost]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Initiate(
        InitiateOffboardingRequest request,
        CancellationToken cancellationToken) =>
        Respond(await offboarding.InitiateAsync(request, cancellationToken), created: true);

    [HttpPut("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        UpdateOffboardingRequest request,
        CancellationToken cancellationToken) =>
        Respond(await offboarding.UpdateAsync(id, request, cancellationToken));


    [HttpPost("{id:guid}/actions/{action}")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Apply(
        Guid id,
        OffboardingAction action,
        [FromBody] OffboardingActionRequest? request,
        CancellationToken cancellationToken) =>
        Respond(await offboarding.ApplyAsync(id, action, request?.Reason, cancellationToken));

    [HttpPost("{id:guid}/clearance/{itemId:guid}")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> CompleteClearance(
        Guid id,
        Guid itemId,
        [FromBody] ClearanceNoteRequest? request,
        CancellationToken cancellationToken) =>
        Respond(await offboarding.CompleteClearanceAsync(id, itemId, request?.Notes, cancellationToken));

    // The one action here with an immediate effect outside the record: their sessions
    // end. Behind Administer rather than Edit, because ticking a checklist and cutting
    // somebody's access are not the same responsibility.
    [HttpPost("{id:guid}/revoke-access")]
    [RequiresPermission(Module, PermissionAction.Administer)]
    public async Task<IActionResult> RevokeAccess(Guid id, CancellationToken cancellationToken) =>
        Respond(await offboarding.RevokeAccessAsync(id, cancellationToken));

    [HttpPost("{id:guid}/exit-interview/schedule")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> ScheduleExitInterview(
        Guid id,
        ScheduleInterviewRequest request,
        CancellationToken cancellationToken) =>
        Respond(await offboarding.ScheduleExitInterviewAsync(id, request.ScheduledAt, cancellationToken));

    [HttpPost("{id:guid}/exit-interview/complete")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> CompleteExitInterview(
        Guid id,
        [FromBody] ExitInterviewNotesRequest? request,
        CancellationToken cancellationToken) =>
        Respond(await offboarding.CompleteExitInterviewAsync(id, request?.Notes, cancellationToken));

    [HttpPost("{id:guid}/exit-documents")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> GenerateExitDocuments(
        Guid id,
        CancellationToken cancellationToken) =>
        Respond(await offboarding.GenerateExitDocumentsAsync(id, cancellationToken));

    [HttpDelete("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        OffboardingOutcome outcome = await offboarding.DeleteAsync(id, cancellationToken);

        return outcome == OffboardingOutcome.Succeeded
            ? Ok<object?>(null, "Offboarding record removed.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    private IActionResult Respond(OffboardingResult result, bool created = false)
    {
        if (!result.Succeeded)
        {
            return Failure<OffboardingDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
        }

        return created
            ? CreatedEnvelope(result.Record!, "Offboarding started.")
            : Ok(result.Record!, "Offboarding updated.");
    }

    private static string StatusFor(OffboardingOutcome outcome) => outcome switch
    {
        OffboardingOutcome.NotFound
            or OffboardingOutcome.EmployeeNotFound
            or OffboardingOutcome.ClearanceItemNotFound => MoteeStatusCodes.NotFound,
        OffboardingOutcome.AlreadyOffboarding => MoteeStatusCodes.Conflict,
        OffboardingOutcome.NotAllowed => MoteeStatusCodes.Conflict,
        OffboardingOutcome.ReasonRequired => MoteeStatusCodes.InvalidRequest,
        _ => MoteeStatusCodes.InternalServerError,
    };

    private static string MessageFor(OffboardingOutcome outcome) => outcome switch
    {
        OffboardingOutcome.NotFound => "Offboarding record not found.",
        OffboardingOutcome.EmployeeNotFound => "Employee not found.",
        OffboardingOutcome.ClearanceItemNotFound => "Clearance item not found.",
        OffboardingOutcome.AlreadyOffboarding =>
            "This employee already has an offboarding in progress.",

        // Says what is wrong rather than only that it was refused: the caller asked for
        // something the record's current state does not permit, and the response carries
        // the actions that are permitted.
        OffboardingOutcome.NotAllowed =>
            "That is not possible for this record in its current state.",
        OffboardingOutcome.ReasonRequired => "A reason is required when turning down an exit.",
        _ => "Could not update the offboarding record.",
    };
}

public sealed record OffboardingActionRequest
{
    public string? Reason { get; init; }
}

public sealed record ClearanceNoteRequest
{
    public string? Notes { get; init; }
}

public sealed record ScheduleInterviewRequest
{
    public required DateTimeOffset ScheduledAt { get; init; }
}

public sealed record ExitInterviewNotesRequest
{
    public string? Notes { get; init; }
}
