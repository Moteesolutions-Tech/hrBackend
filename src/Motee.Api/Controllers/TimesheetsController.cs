using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Attendance;
using Motee.Application.Common;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Weeks, handed in and signed off.
//
// Split like the clock: handing in your own week is self-service and takes no
// employee id, while reviewing somebody else's is administrative. The owner reviews
// and cannot submit, which is right — they have no week.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/attendance/timesheets")]
public class TimesheetsController(ITimesheetService timesheets) : ApiControllerBase
{
    private const string Module = "time-payroll.attendance";

    // Defaults to the current week. Always answers: a week nobody has handed in comes
    // back Pending rather than 404, because the screen draws it either way.
    [HttpGet("mine")]
    [RequiresEmployee]
    [ProducesResponseType<TimesheetDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(
        [FromQuery] DateOnly? weekStart,
        CancellationToken cancellationToken)
    {
        TimesheetDto? sheet = await timesheets.MineAsync(weekStart, cancellationToken);

        return sheet is null
            ? Failure<TimesheetDto>(MoteeStatusCodes.NotFound, "No timesheet found.")
            : Ok(sheet);
    }

    [HttpPost("mine/submit")]
    [RequiresEmployee]
    [ProducesResponseType<TimesheetDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Submit(
        SubmitTimesheetRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await timesheets.SubmitAsync(request.WeekStart, cancellationToken),
            "Timesheet submitted.");

    // ── reviewing ───────────────────────────────────────────────────────────

    [HttpGet]
    [ProducesResponseType<PagedResult<TimesheetDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] TimesheetQuery query,
        CancellationToken cancellationToken) =>
        Ok(await timesheets.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<TimesheetDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        TimesheetDto? sheet = await timesheets.GetAsync(id, cancellationToken);

        return sheet is null
            ? Failure<TimesheetDto>(MoteeStatusCodes.NotFound, "Timesheet not found.")
            : Ok(sheet);
    }

    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<TimesheetDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Approve(Guid id, CancellationToken cancellationToken) =>
        Respond(await timesheets.ApproveAsync(id, cancellationToken), "Timesheet approved.");

    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<TimesheetDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Reject(
        Guid id,
        RejectTimesheetRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await timesheets.RejectAsync(id, request.Reason, cancellationToken),
            "Timesheet sent back.");

    private IActionResult Respond(TimesheetResult result, string message) =>
        result.Succeeded
            ? Ok(result.Timesheet!, message)
            : Failure<TimesheetDto>(
                StatusFor(result.Outcome),
                result.Reason ?? MessageFor(result.Outcome));

    private static string StatusFor(TimesheetOutcome outcome) => outcome switch
    {
        TimesheetOutcome.NotFound or TimesheetOutcome.EmployeeNotFound =>
            MoteeStatusCodes.NotFound,
        TimesheetOutcome.NotAllowed => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(TimesheetOutcome outcome) => outcome switch
    {
        TimesheetOutcome.NotFound => "Timesheet not found.",
        TimesheetOutcome.EmployeeNotFound => "Employee not found.",
        TimesheetOutcome.WeekNotOver =>
            "That week has not finished yet. It can be handed in once it has.",
        TimesheetOutcome.ReasonRequired =>
            "Say why it is going back, or the person has to guess and will send the "
            + "same week again.",
        _ => "Could not update the timesheet.",
    };
}

public sealed record SubmitTimesheetRequest
{
    // Any day in the week is accepted and snapped to its Monday, so a client does not
    // have to agree with the server about where a week begins.
    public required DateOnly WeekStart { get; init; }
}

public sealed record RejectTimesheetRequest
{
    public required string Reason { get; init; }
}
