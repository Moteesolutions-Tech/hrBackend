using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Leave;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/leave/balances")]
public class LeaveBalancesController(
    ILeaveBalanceService balances,
    ILeaveYearEndService yearEnd,
    ICurrentEmployee currentEmployee) : ApiControllerBase
{
    private const string Module = "time-payroll.leave";

    [HttpGet]
    [ProducesResponseType<PagedResult<LeaveBalanceDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] LeaveBalanceQuery query,
        CancellationToken cancellationToken) =>
        Ok(await balances.ListAsync(query, cancellationToken));

    // The employee's own balance screen. Unguarded for the same reason as their own
    // requests: needing the leave module to see your own remaining days would mean
    // granting everybody the screen showing the whole company's.
    [HttpGet("mine")]
    [ProducesResponseType<IReadOnlyList<LeaveBalanceDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(
        [FromQuery] DateOnly? asAt,
        CancellationToken cancellationToken)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return Failure<object?>(MoteeStatusCodes.NotFound, "No employee record found.");
        }

        return Ok(await balances.ForEmployeeAsync(employeeId, asAt, cancellationToken));
    }

    [HttpGet("{employeeId:guid}")]
    [ProducesResponseType<IReadOnlyList<LeaveBalanceDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> ForEmployee(
        Guid employeeId,
        [FromQuery] DateOnly? asAt,
        CancellationToken cancellationToken) =>
        Ok(await balances.ForEmployeeAsync(employeeId, asAt, cancellationToken));

    // Granting or docking days by hand. Recorded as its own row with a reason, so "why do
    // I have 23 days when the policy says 25" stays answerable.
    [HttpPost("adjust")]
    [ProducesResponseType<LeaveBalanceDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Adjust(
        LeaveAdjustmentRequest request,
        CancellationToken cancellationToken)
    {
        LeaveAdjustmentResult result = await balances.AdjustAsync(request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Balance!, "Leave balance adjusted.")
            : Failure<LeaveBalanceDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // What the year end would do, without doing it. The number HR actually want before a
    // year closes — "you are about to lose 340 days across the company" is what prompts
    // the reminder to book leave.
    [HttpGet("year-end/preview")]
    [ProducesResponseType<LeaveYearEndResult>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> PreviewYearEnd(
        [FromQuery] DateOnly? yearContaining,
        CancellationToken cancellationToken) =>
        Ok<object>(await yearEnd.PreviewAsync(yearContaining, cancellationToken));

    // Closing by hand. The daily job does this on its own, so this is for a company
    // onboarding partway through a year, or one whose job run failed and wants it
    // finished now rather than waiting for tomorrow.
    //
    // Safe to call twice: carry-over is unique per person, type and year, and anything
    // already written is left alone rather than recomputed.
    [HttpPost("year-end/close")]
    [ProducesResponseType<LeaveYearEndResult>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> CloseYearEnd(
        [FromQuery] DateOnly? yearContaining,
        CancellationToken cancellationToken) =>
        Ok<object>(
            await yearEnd.CloseAsync(yearContaining, cancellationToken),
            "Leave year closed.");

    private static string StatusFor(LeaveAdjustmentOutcome outcome) => outcome switch
    {
        LeaveAdjustmentOutcome.NotFound => MoteeStatusCodes.NotFound,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(LeaveAdjustmentOutcome outcome) => outcome switch
    {
        LeaveAdjustmentOutcome.NotFound => "That employee was not found.",
        LeaveAdjustmentOutcome.UnknownLeaveType => "That kind of leave is not available.",
        LeaveAdjustmentOutcome.NoChange => "An adjustment has to move the balance.",
        _ => "Could not adjust the balance.",
    };
}
