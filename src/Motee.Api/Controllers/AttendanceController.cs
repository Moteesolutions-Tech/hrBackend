using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Attendance;
using Motee.Application.Common;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Punches, and the day they add up to.
//
// Split by who is acting. The clock is self-service and carries RequiresEmployee: the
// only day anybody can punch is their own, so none of those routes takes an employee
// id — an id in the signature is an id somebody eventually passes somebody else's.
// The overview and the manual log are administrative and name whose day they mean.
//
// The owner reaches the second set and not the first, which is correct: they run the
// company rather than working at it, and have no attendance of their own.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/attendance")]
public class AttendanceController(IAttendanceService attendance) : ApiControllerBase
{
    private const string Module = "time-payroll.attendance";

    // ── the employee's own day ──────────────────────────────────────────────

    // No permission attribute, like the rest of self-service: needing the attendance
    // module to see your own clock would mean granting every member of staff the
    // screen showing where the whole company is.
    [HttpGet("mine")]
    [RequiresEmployee]
    [ProducesResponseType<AttendanceDayDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(
        [FromQuery] DateOnly? date,
        CancellationToken cancellationToken)
    {
        AttendanceDayDto? day = await attendance.MineAsync(date, cancellationToken);

        return day is null
            ? Failure<AttendanceDayDto>(MoteeStatusCodes.NotFound, "No attendance record found.")
            : Ok(day);
    }

    // The month calendar and the week strip. Every day in the range comes back,
    // including the ones with no punches — a calendar with gaps where somebody was
    // absent is a calendar that hides absences.
    [HttpGet("mine/days")]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<AttendanceDayDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MyDays(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        CancellationToken cancellationToken) =>
        Ok(await attendance.MineBetweenAsync(from, to, cancellationToken));

    [HttpGet("mine/summary")]
    [RequiresEmployee]
    [ProducesResponseType<AttendanceSummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MySummary(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        CancellationToken cancellationToken) =>
        Ok(await attendance.MineSummaryAsync(from, to, cancellationToken));

    [HttpPost("mine/clock-in")]
    [RequiresEmployee]
    [ProducesResponseType<AttendanceDayDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClockIn(
        ClockInRequest request,
        CancellationToken cancellationToken) =>
        Respond(await attendance.ClockInAsync(request, cancellationToken), "Clocked in.");

    [HttpPost("mine/break/start")]
    [RequiresEmployee]
    [ProducesResponseType<AttendanceDayDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StartBreak(CancellationToken cancellationToken) =>
        Respond(await attendance.StartBreakAsync(cancellationToken), "Break started.");

    [HttpPost("mine/break/end")]
    [RequiresEmployee]
    [ProducesResponseType<AttendanceDayDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> EndBreak(CancellationToken cancellationToken) =>
        Respond(await attendance.EndBreakAsync(cancellationToken), "Break ended.");

    [HttpPost("mine/clock-out")]
    [RequiresEmployee]
    [ProducesResponseType<AttendanceDayDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClockOut(
        ClockOutRequest request,
        CancellationToken cancellationToken) =>
        Respond(await attendance.ClockOutAsync(request, cancellationToken), "Clocked out.");

    // Moving between office and home mid-day. Not a new punch — somebody who went
    // home after lunch did not arrive twice.
    [HttpPut("mine/location")]
    [RequiresEmployee]
    [ProducesResponseType<AttendanceDayDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetLocation(
        SetWorkLocationRequest request,
        CancellationToken cancellationToken) =>
        Respond(await attendance.SetLocationAsync(request, cancellationToken), "Location updated.");

    // ── the company's day ───────────────────────────────────────────────────

    [HttpGet]
    [ProducesResponseType<PagedResult<AttendanceDayDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] AttendanceQuery query,
        CancellationToken cancellationToken) =>
        Ok(await attendance.ListAsync(query, cancellationToken));

    // The figures above the overview: hours, overtime, and how the days fell out.
    // Computed from the same derivation as the rows, so a total and the list under it
    // cannot disagree.
    [HttpGet("summary")]
    [ProducesResponseType<AttendanceSummaryDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(
        [FromQuery] AttendanceSummaryQuery query,
        CancellationToken cancellationToken) =>
        Ok(await attendance.SummaryAsync(query, cancellationToken));

    // HR typing a day in for somebody who forgot, or whose reader was down. Always
    // recorded as Manual with the author attached — a typed day and a punched one
    // must never be indistinguishable.
    [HttpPost("log")]
    [ProducesResponseType<AttendanceDayDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Log(
        LogAttendanceRequest request,
        CancellationToken cancellationToken) =>
        Respond(await attendance.LogAsync(request, cancellationToken), "Attendance recorded.");

    private IActionResult Respond(AttendanceResult result, string message) =>
        result.Succeeded
            ? Ok(result.Day!, message)
            : Failure<AttendanceDayDto>(
                StatusFor(result.Outcome),

                // The lifecycle's own sentence when it wrote one — "you are already
                // clocked in" rather than the category it fell into.
                result.Reason ?? MessageFor(result.Outcome));

    private static string StatusFor(AttendanceOutcome outcome) => outcome switch
    {
        AttendanceOutcome.NotFound or AttendanceOutcome.EmployeeNotFound =>
            MoteeStatusCodes.NotFound,
        AttendanceOutcome.NotAllowed => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(AttendanceOutcome outcome) => outcome switch
    {
        AttendanceOutcome.NotFound => "No attendance record found.",
        AttendanceOutcome.EmployeeNotFound => "Employee not found.",
        AttendanceOutcome.NoSchedule =>
            "No working hours are configured, so this day cannot be judged.",
        AttendanceOutcome.OutOfOrder => "The clock-out is before the clock-in.",
        AttendanceOutcome.InTheFuture =>
            "That day has not happened yet. Check the date on the device.",
        _ => "Could not record attendance.",
    };
}
