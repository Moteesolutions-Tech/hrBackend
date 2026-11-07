using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Checklists;
using Motee.Application.Common;
using Motee.Domain.Authorization;
using Motee.Domain.Checklists;

namespace Motee.Api.Controllers;

// The onboarding checklist: the task template, and how far each starter has
// got through it.
//
// Nothing here writes a starter's checklist. It is the active template
// resolved against their start date, so there is no per-hire copy to create,
// migrate or let drift.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/employee-checklist")]
public class EmployeeChecklistController(IChecklistService checklist) : ApiControllerBase
{
    private const string Module = "organization.employee-checklist";

    [HttpGet("items")]
    [ProducesResponseType<IReadOnlyList<ChecklistItemDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Items(
        [FromQuery] bool includeRetired,
        CancellationToken cancellationToken) =>
        Ok(await checklist.ListItemsAsync(includeRetired, cancellationToken));

    [HttpPost("items")]
    [ProducesResponseType<ChecklistItemDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> AddItem(
        ChecklistItemRequest request,
        CancellationToken cancellationToken)
    {
        ChecklistResult result = await checklist.CreateItemAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Item!, "Task added to the template.")
            : Failure<ChecklistItemDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("items/{id:guid}")]
    [ProducesResponseType<ChecklistItemDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> UpdateItem(
        Guid id,
        ChecklistItemRequest request,
        CancellationToken cancellationToken) =>
        Respond(await checklist.UpdateItemAsync(id, request, cancellationToken), "Task saved.");

    // Up or down one place, which is what the table's arrows do. A no-op at
    // either end rather than a failure.
    [HttpPost("items/{id:guid}/move")]
    [ProducesResponseType<ChecklistItemDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> MoveItem(
        Guid id,
        MoveChecklistItemRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await checklist.MoveItemAsync(id, request.Up, cancellationToken),
            "Template reordered.");

    [HttpPost("items/{id:guid}/retire")]
    [ProducesResponseType<ChecklistItemDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> RetireItem(Guid id, CancellationToken cancellationToken) =>
        Respond(
            await checklist.RetireItemAsync(id, cancellationToken),
            "Task retired. It stays on the checklists of anybody who already completed it.");

    [HttpPost("items/{id:guid}/restore")]
    [ProducesResponseType<ChecklistItemDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> RestoreItem(Guid id, CancellationToken cancellationToken) =>
        Respond(await checklist.RestoreItemAsync(id, cancellationToken), "Task restored.");

    [HttpDelete("items/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> DeleteItem(Guid id, CancellationToken cancellationToken)
    {
        ChecklistResult result = await checklist.DeleteItemAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Task removed from the template.")
            : Failure<object?>(StatusFor(result), MessageFor(result));
    }

    // The New Hire Status tab.
    [HttpGet("hires")]
    [ProducesResponseType<PagedResult<ChecklistHireDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Hires(
        [FromQuery] ChecklistHireQuery query,
        CancellationToken cancellationToken) =>
        Ok(await checklist.HiresAsync(query, cancellationToken));

    // The signed-in employee's own tasks. Two of the eight seeded tasks are
    // theirs to do — compliance training and the probation form — and nothing
    // else tells them so. No module permission, because doing your own
    // onboarding is not an HR function.
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<EmployeeChecklistDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken)
    {
        EmployeeChecklistDto? found = await checklist.MineAsync(cancellationToken);

        return found is null
            ? Failure<EmployeeChecklistDto>(
                MoteeStatusCodes.NotFound,
                "Your account has no employee record, so there is no checklist to show.")
            : Ok(found);
    }

    [HttpGet("employees/{employeeId:guid}")]
    [ProducesResponseType<EmployeeChecklistDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> ForEmployee(
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        EmployeeChecklistDto? found =
            await checklist.ForEmployeeAsync(employeeId, cancellationToken);

        return found is null
            ? Failure<EmployeeChecklistDto>(MoteeStatusCodes.NotFound, "Employee not found.")
            : Ok(found);
    }

    [HttpPost("employees/{employeeId:guid}/tasks/{itemId:guid}")]
    [ProducesResponseType<EmployeeChecklistDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Complete(
        Guid employeeId,
        Guid itemId,
        ChecklistCompletionRequest request,
        CancellationToken cancellationToken) =>
        Progress(
            await checklist.CompleteAsync(employeeId, itemId, request, cancellationToken),
            "Task marked as done.");

    [HttpDelete("employees/{employeeId:guid}/tasks/{itemId:guid}")]
    [ProducesResponseType<EmployeeChecklistDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Clear(
        Guid employeeId,
        Guid itemId,
        CancellationToken cancellationToken) =>
        Progress(
            await checklist.ClearAsync(employeeId, itemId, cancellationToken),
            "Task marked as not done.");

    private IActionResult Progress(ChecklistResult result, string message) =>
        result.Succeeded
            ? Ok(result.Checklist!, message)
            : Failure<EmployeeChecklistDto>(StatusFor(result), MessageFor(result));

    private IActionResult Respond(ChecklistResult result, string message) =>
        result.Succeeded
            ? Ok(result.Item!, message)
            : Failure<ChecklistItemDto>(StatusFor(result), MessageFor(result));

    private static string StatusFor(ChecklistResult result) => result.Outcome switch
    {
        ChecklistOutcome.NotFound or ChecklistOutcome.EmployeeNotFound =>
            MoteeStatusCodes.NotFound,

        ChecklistOutcome.Refused when result.Refusal == ChecklistRefusal.InUse =>
            MoteeStatusCodes.Conflict,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(ChecklistResult result) => result.Outcome switch
    {
        ChecklistOutcome.NotFound => "Checklist task not found.",
        ChecklistOutcome.EmployeeNotFound => "That person does not work here.",
        _ => ReasonFor(result.Refusal),
    };

    private static string ReasonFor(ChecklistRefusal refusal) => refusal switch
    {
        ChecklistRefusal.TitleRequired =>
            $"Give the task a name of at least {ChecklistRules.ShortestTitle} characters.",
        ChecklistRefusal.TitleTooLong =>
            $"Keep the task name to {ChecklistRules.LongestTitle} characters or fewer.",
        ChecklistRefusal.DescriptionTooLong =>
            $"Keep the description to {ChecklistRules.LongestDescription} characters or fewer.",
        ChecklistRefusal.OffsetOutOfRange =>
            $"The due day has to be between {ChecklistRules.EarliestOffset} and "
            + $"{ChecklistRules.LatestOffset} days from the start date.",
        ChecklistRefusal.InUse =>
            "Somebody has already completed this task, so deleting it would lose that "
            + "record. Retire it instead — it stays on their checklist and comes off "
            + "everybody else's.",
        _ => "Could not save the task.",
    };
}

public sealed record MoveChecklistItemRequest
{
    public required bool Up { get; init; }
}
