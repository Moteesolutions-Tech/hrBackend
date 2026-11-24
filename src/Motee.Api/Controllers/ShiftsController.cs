using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Api.Http;
using Motee.Application.Attendance;
using Motee.Domain.Attendance;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// The roster: who is on which work schedule on which day.
//
// There are no shift-template endpoints here, deliberately. The screen models a
// ShiftTemplate with a name, start and end time, break minutes and working
// days — which is a work schedule, served by WorkSchedulesController, and that
// table carries grace minutes and an active flag besides. A second list of
// shift patterns would be a second answer to what hours a shift runs, and
// attendance judges lateness against the work schedule.
//
// Empty days come back empty. The client fills them with a default "so the
// schedule always reads as populated instead of empty", which means a manager
// checking cover reads coverage that does not exist.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/shifts")]
public class ShiftsController(IShiftService shifts) : ApiControllerBase
{
    private const string Module = "time-payroll.shifts";

    [HttpGet("roster")]
    [ProducesResponseType<RosterResult>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Roster(
        [FromQuery] RosterQuery query,
        CancellationToken cancellationToken)
    {
        RosterResult roster = await shifts.RosterAsync(query, cancellationToken);

        return roster.Succeeded
            ? Ok(roster)
            : Failure<RosterResult>(MoteeStatusCodes.InvalidRequest, MessageFor(roster.Refusal));
    }

    // Somebody's own shifts. No module grant: a person's own rota is theirs to
    // see, and needing the rostering permission to find out when you are
    // working would mean only managers could.
    [HttpGet("mine")]
    [ProducesResponseType<RosterResult>(StatusCodes.Status200OK)]
    [RequiresEmployee]
    public async Task<IActionResult> Mine(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        CancellationToken cancellationToken)
    {
        RosterResult roster = await shifts.MineAsync(from, to, cancellationToken);

        return roster.Succeeded
            ? Ok(roster)
            : Failure<RosterResult>(MoteeStatusCodes.InvalidRequest, MessageFor(roster.Refusal));
    }

    // An upsert: putting somebody on a shift for a day they already have one
    // changes the pattern rather than refusing, because that is what dragging a
    // different shift onto a filled cell means.
    [HttpPost("assignments")]
    [ProducesResponseType<ShiftAssignmentDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Assign(
        ShiftAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        ShiftResult result = await shifts.AssignAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Assignment!, "Shift rostered.")
            : Failure<ShiftAssignmentDto>(StatusFor(result.Outcome), MessageFor(result.Refusal));
    }

    // Called off, which is a decision rather than a deletion: the rota still
    // shows somebody was meant to be there, and that is a different thing from
    // a day nobody rostered.
    [HttpPost("assignments/{id:guid}/cancel")]
    [ProducesResponseType<ShiftAssignmentDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        ShiftResult result = await shifts.CancelAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok(result.Assignment!, "Shift cancelled.")
            : Failure<ShiftAssignmentDto>(StatusFor(result.Outcome), MessageFor(result.Refusal));
    }

    [HttpDelete("assignments/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Clear(Guid id, CancellationToken cancellationToken)
    {
        ShiftOutcome outcome = await shifts.ClearAsync(id, cancellationToken);

        return outcome == ShiftOutcome.Succeeded
            ? Ok<object?>(null, "Shift removed from the rota.")
            : Failure<object?>(MoteeStatusCodes.NotFound, "Shift not found.");
    }

    private static string StatusFor(ShiftOutcome outcome) => outcome switch
    {
        ShiftOutcome.NotFound => MoteeStatusCodes.NotFound,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(ShiftRefusal refusal) => refusal switch
    {
        ShiftRefusal.EmployeeNotFound => "That person does not work here.",
        ShiftRefusal.ScheduleNotFound => "That shift pattern does not exist.",
        ShiftRefusal.ScheduleInactive => "That shift pattern is no longer in use.",
        ShiftRefusal.NotAWorkingDay =>
            "That pattern does not run on that day, so nobody could work the shift.",
        ShiftRefusal.DateInThePast =>
            "A rota cannot be changed for a day already past — attendance has judged it.",
        ShiftRefusal.AlreadyAssigned => "That person already has a shift that day.",
        ShiftRefusal.RangeBackwards => "The end of the range is before its start.",
        ShiftRefusal.RangeTooWide =>
            $"A roster can cover at most {Shifts.MaximumRosterDays} days at a time.",
        _ => "That change was not accepted.",
    };
}
