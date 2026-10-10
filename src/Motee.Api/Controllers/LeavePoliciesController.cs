using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Leave;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/leave")]
public class LeavePoliciesController(ILeavePolicyService policies) : ApiControllerBase
{
    private const string Module = "time-payroll.leave";

    // Read by the request form as well as the policies screen — somebody booking leave
    // Read by the request form as well as the policies screen — somebody booking leave
    // has to pick a type — so this sits behind View rather than the Edit that changing
    // one needs.
    [HttpGet("types")]
    [ProducesResponseType<IReadOnlyList<LeaveTypeDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> ListTypes(CancellationToken cancellationToken) =>
        Ok(await policies.ListTypesAsync(cancellationToken));

    [HttpPost("types")]
    [ProducesResponseType<LeaveTypeDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> CreateType(
        LeaveTypeRequest request,
        CancellationToken cancellationToken) =>
        Respond(await policies.CreateTypeAsync(request, cancellationToken), created: true);

    [HttpPut("types/{id:guid}")]
    [ProducesResponseType<LeaveTypeDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> UpdateType(
        Guid id,
        LeaveTypeRequest request,
        CancellationToken cancellationToken) =>
        Respond(await policies.UpdateTypeAsync(id, request, cancellationToken));

    // Deactivation rather than deletion, which is why it is a DELETE that does not
    // delete: requests point at the type and history has to keep reading correctly.
    [HttpDelete("types/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> DeactivateType(Guid id, CancellationToken cancellationToken)
    {
        LeavePolicyOutcome outcome = await policies.DeactivateTypeAsync(id, cancellationToken);

        return outcome == LeavePolicyOutcome.Succeeded
            ? Ok<object?>(null, "Leave type deactivated.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    [HttpGet("holidays")]
    [ProducesResponseType<IReadOnlyList<PublicHolidayDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> ListHolidays(
        [FromQuery] int? year,
        CancellationToken cancellationToken) =>
        Ok(await policies.ListHolidaysAsync(year, cancellationToken));

    [HttpPost("holidays")]
    [ProducesResponseType<PublicHolidayDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> AddHoliday(
        PublicHolidayRequest request,
        CancellationToken cancellationToken)
    {
        PublicHolidayResult result = await policies.AddHolidayAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Holiday!, "Public holiday added.")
            : Failure<PublicHolidayDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // Fills a year in from the built-in calendar. Existing days are left alone — a
    // company that renamed one, or moved it to match a local announcement, has said
    // something this must not undo.
    //
    // The computed days only: Eid and the other moon-sighting holidays are announced
    // rather than calculable, so they stay a company's own entry.
    [HttpPost("holidays/generate/{year:int}")]
    [ProducesResponseType<IReadOnlyList<PublicHolidayDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> GenerateHolidays(
        int year,
        CancellationToken cancellationToken)
    {
        if (year is < 2000 or > 2100)
        {
            return Failure<object?>(
                MoteeStatusCodes.InvalidRequest, "That is not a year this can generate.");
        }

        return Ok(
            await policies.GenerateHolidaysAsync(year, cancellationToken),
            "Public holidays generated.");
    }

    [HttpDelete("holidays/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> RemoveHoliday(Guid id, CancellationToken cancellationToken)
    {
        LeavePolicyOutcome outcome = await policies.RemoveHolidayAsync(id, cancellationToken);

        return outcome == LeavePolicyOutcome.Succeeded
            ? Ok<object?>(null, "Public holiday removed.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    // Periods the company will not approve planned leave over. The opposite of a public
    // holiday: that is a day nobody works, this is a day everybody does.
    [HttpGet("blackouts")]
    [ProducesResponseType<IReadOnlyList<LeaveBlackoutDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Blackouts(CancellationToken cancellationToken) =>
        Ok(await policies.ListBlackoutsAsync(cancellationToken));

    [HttpPost("blackouts")]
    [ProducesResponseType<LeaveBlackoutDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> AddBlackout(
        LeaveBlackoutRequest request,
        CancellationToken cancellationToken) =>
        RespondBlackout(
            await policies.SaveBlackoutAsync(null, request, cancellationToken), created: true);

    [HttpPut("blackouts/{id:guid}")]
    [ProducesResponseType<LeaveBlackoutDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> UpdateBlackout(
        Guid id,
        LeaveBlackoutRequest request,
        CancellationToken cancellationToken) =>
        RespondBlackout(await policies.SaveBlackoutAsync(id, request, cancellationToken));

    [HttpDelete("blackouts/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> RemoveBlackout(Guid id, CancellationToken cancellationToken)
    {
        LeavePolicyOutcome outcome = await policies.RemoveBlackoutAsync(id, cancellationToken);

        return outcome == LeavePolicyOutcome.Succeeded
            ? Ok<object?>(null, "Blackout period removed.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    private IActionResult RespondBlackout(LeaveBlackoutResult result, bool created = false)
    {
        if (!result.Succeeded)
        {
            return Failure<LeaveBlackoutDto>(
                StatusFor(result.Outcome), MessageFor(result.Outcome));
        }

        return created
            ? CreatedEnvelope(result.Blackout!, "Blackout period created.")
            : Ok(result.Blackout!, "Blackout period updated.");
    }

    private IActionResult Respond(LeaveTypeResult result, bool created = false)
    {
        if (!result.Succeeded)
        {
            return Failure<LeaveTypeDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
        }

        return created
            ? CreatedEnvelope(result.Type!, "Leave type created.")
            : Ok(result.Type!, "Leave type updated.");
    }

    private static string StatusFor(LeavePolicyOutcome outcome) => outcome switch
    {
        LeavePolicyOutcome.NotFound => MoteeStatusCodes.NotFound,
        LeavePolicyOutcome.DuplicateName or LeavePolicyOutcome.DuplicateDate =>
            MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(LeavePolicyOutcome outcome) => outcome switch
    {
        LeavePolicyOutcome.NotFound => "Not found.",
        LeavePolicyOutcome.DuplicateName => "A leave type with that name already exists.",
        LeavePolicyOutcome.DuplicateDate => "That date is already a public holiday.",

        // Says which rule, because "invalid" leaves somebody re-reading a form of a
        // dozen fields to find the one at fault.
        LeavePolicyOutcome.InvalidRule =>
            "Days cannot be negative, and the carry-over cap cannot exceed the entitlement.",
        _ => "Could not save.",
    };
}
