using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Organisation;
using Motee.Domain.Authorization;
using Motee.Domain.Organisation;

namespace Motee.Api.Controllers;

// The establishment: posts, and who is in them.
//
// Assigning is its own route rather than a field on the employee form,
// because placing somebody in a post is an establishment decision and shows
// up in the establishment's figures. Unassigning is keyed by employee rather
// than by post: taking somebody out does not require knowing where they were.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/positions")]
public class PositionsController(IPositionService positions) : ApiControllerBase
{
    private const string Module = "organization.roles";

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PositionDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] PositionQuery query,
        CancellationToken cancellationToken) =>
        Ok(await positions.ListAsync(query, cancellationToken));

    // How much establishment exists, how much is filled, and how many people
    // are in no post at all — the last being the measure of whether this
    // module is actually being kept up.
    [HttpGet("summary")]
    [ProducesResponseType<PositionSummaryDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await positions.SummaryAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<PositionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        PositionDto? found = await positions.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<PositionDto>(MoteeStatusCodes.NotFound, "Position not found.")
            : Ok(found);
    }

    [HttpPost]
    [ProducesResponseType<PositionDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        PositionRequest request,
        CancellationToken cancellationToken)
    {
        PositionResult result = await positions.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Position!, "Position added.")
            : Failure<PositionDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<PositionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        PositionRequest request,
        CancellationToken cancellationToken) =>
        Respond(await positions.UpdateAsync(id, request, cancellationToken), "Position saved.");

    [HttpPost("{id:guid}/assign")]
    [ProducesResponseType<PositionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Assign(
        Guid id,
        AssignPositionRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await positions.AssignAsync(id, request.EmployeeId, cancellationToken),
            "Placed in the post.");

    // Keyed by employee: taking somebody out of a post does not require
    // knowing which one they were in.
    [HttpPost("unassign")]
    [ProducesResponseType<PositionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Unassign(
        AssignPositionRequest request,
        CancellationToken cancellationToken)
    {
        PositionResult result = await positions.UnassignAsync(
            request.EmployeeId, cancellationToken);

        // Succeeds with nothing to describe when they were in no post, which
        // is still the outcome the caller asked for.
        return result.Succeeded
            ? result.Position is null
                ? Ok<object?>(null, "Taken out of the post.")
                : Ok(result.Position, "Taken out of the post.")
            : Failure<PositionDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPost("{id:guid}/retire")]
    [ProducesResponseType<PositionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Retire(Guid id, CancellationToken cancellationToken) =>
        Respond(await positions.RetireAsync(id, cancellationToken), "Position retired.");

    [HttpPost("{id:guid}/restore")]
    [ProducesResponseType<PositionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken) =>
        Respond(await positions.RestoreAsync(id, cancellationToken), "Position restored.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await positions.DeleteAsync(id, cancellationToken) switch
        {
            PositionOutcome.Succeeded => Ok<object?>(null, "Position deleted."),
            PositionOutcome.NotFound =>
                Failure<object?>(MoteeStatusCodes.NotFound, "Position not found."),
            _ => Failure<object?>(
                MoteeStatusCodes.Conflict,
                "Somebody is in this post. Move them first, or retire it instead."),
        };

    private IActionResult Respond(PositionResult result, string message) =>
        result.Succeeded
            ? Ok(result.Position!, message)
            : Failure<PositionDto>(StatusFor(result), MessageFor(result));

    private static string StatusFor(PositionResult result) => result.Outcome switch
    {
        PositionOutcome.NotFound or PositionOutcome.DepartmentNotFound
            or PositionOutcome.EmployeeNotFound => MoteeStatusCodes.NotFound,

        PositionOutcome.Refused when result.Refusal
            is PositionRefusal.TitleTakenInDepartment
            or PositionRefusal.InUse => MoteeStatusCodes.Conflict,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(PositionResult result) => result.Outcome switch
    {
        PositionOutcome.NotFound => "Position not found.",
        PositionOutcome.DepartmentNotFound => "That department does not exist.",
        PositionOutcome.EmployeeNotFound => "That person does not work here.",
        _ => ReasonFor(result.Refusal),
    };

    private static string ReasonFor(PositionRefusal refusal) => refusal switch
    {
        PositionRefusal.TitleRequired => "Give the position a title.",
        PositionRefusal.HeadcountNotPositive =>
            "A position needs at least one seat. Retire it if it is no longer needed.",
        PositionRefusal.HeadcountTooLarge =>
            "That is a lot of seats — check the figure.",
        PositionRefusal.TitleTakenInDepartment =>
            "That department already has a position with this title.",
        PositionRefusal.InUse =>
            "Somebody is in this post. Move them first, then retire it.",
        _ => "Could not save the position.",
    };
}

public sealed record AssignPositionRequest
{
    public required Guid EmployeeId { get; init; }
}
