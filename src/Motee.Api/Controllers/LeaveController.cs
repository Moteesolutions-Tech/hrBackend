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
[Route("api/v{version:apiVersion}/leave/requests")]
public class LeaveController(
    ILeaveRequestService requests,
    ICurrentEmployee currentEmployee) : ApiControllerBase
{
    private const string Module = "time-payroll.leave";

    [HttpGet]
    [ProducesResponseType<PagedResult<LeaveRequestDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] LeaveRequestQuery query,
        CancellationToken cancellationToken) =>
        Ok(await requests.ListAsync(query, cancellationToken));

    // The employee's own requests. No permission attribute: these are theirs, and
    // requiring the leave module would mean granting every member of staff the screen
    // that lists everybody else's absence.
    [HttpGet("mine")]
    [ProducesResponseType<PagedResult<LeaveRequestDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(
        [FromQuery] LeaveRequestQuery query,
        CancellationToken cancellationToken)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return Failure<object?>(MoteeStatusCodes.NotFound, "No employee record found.");
        }

        return Ok(await requests.ListAsync(
            query with { EmployeeId = employeeId }, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<LeaveRequestDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        LeaveRequestDto? request = await requests.GetAsync(id, cancellationToken);

        return request is null
            ? Failure<LeaveRequestDto>(MoteeStatusCodes.NotFound, "Leave request not found.")
            : Ok(request);
    }

    // What a request would cost, before anybody commits to it. Behind View rather than
    // Create so the form can price dates as they are picked without the caller needing
    // permission to book.
    [HttpPost("quote")]
    [ProducesResponseType<LeaveQuoteDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Quote(
        LeaveQuoteRequest request,
        CancellationToken cancellationToken) =>
        Ok<object>(await requests.QuoteAsync(request, cancellationToken));

    [HttpPost]
    [ProducesResponseType<LeaveRequestDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Submit(
        LeaveRequestSubmission request,
        CancellationToken cancellationToken) =>
        Respond(await requests.SubmitAsync(request, cancellationToken), created: true);

    // Behind Edit rather than Delete: withdrawing leave is not deleting the record of it,
    // and the request stays visible as cancelled.
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<LeaveRequestDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Cancel(
        Guid id,
        [FromBody] CancelLeaveRequest? request,
        CancellationToken cancellationToken) =>
        Respond(await requests.CancelAsync(id, request?.Reason, cancellationToken));

    private IActionResult Respond(LeaveRequestResult result, bool created = false)
    {
        if (!result.Succeeded)
        {
            return Failure<LeaveRequestDto>(
                StatusFor(result.Outcome), result.Reason ?? MessageFor(result.Outcome));
        }

        return created
            ? CreatedEnvelope(result.Request!, "Leave request submitted.")
            : Ok(result.Request!, "Leave request updated.");
    }

    private static string StatusFor(LeaveRequestOutcome outcome) => outcome switch
    {
        LeaveRequestOutcome.NotFound => MoteeStatusCodes.NotFound,

        // Everything else is the caller asking for something the rules do not allow, not
        // a server problem — so they are all 4xx and the message carries the detail.
        LeaveRequestOutcome.Overlaps or LeaveRequestOutcome.NotCancellable =>
            MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(LeaveRequestOutcome outcome) => outcome switch
    {
        LeaveRequestOutcome.NotFound => "Leave request not found.",
        LeaveRequestOutcome.UnknownLeaveType => "That kind of leave is not available.",
        LeaveRequestOutcome.NoPolicy => "No policy is configured for that kind of leave.",
        LeaveRequestOutcome.InvalidDates => "Those dates are not a valid range.",
        LeaveRequestOutcome.NoWorkingDays =>
            "Every day in that range is a weekend or a company holiday.",
        LeaveRequestOutcome.InsufficientNotice => "That is shorter notice than the policy allows.",
        LeaveRequestOutcome.TooLong => "That is longer than the policy allows in one go.",
        LeaveRequestOutcome.InsufficientBalance => "There are not enough days left.",
        LeaveRequestOutcome.Overlaps => "There is already leave booked over those dates.",
        LeaveRequestOutcome.NotCancellable => "That request can no longer be cancelled.",
        _ => "Could not save the leave request.",
    };
}

public sealed record CancelLeaveRequest
{
    public string? Reason { get; init; }
}
