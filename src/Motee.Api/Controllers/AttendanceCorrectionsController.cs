using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Attendance;
using Motee.Application.Common;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Claims that a recorded day is wrong.
//
// The employee asks and somebody else decides, which is the whole design: a forgotten
// clock-out is an assertion about what happened rather than a fact the person gets to
// type. Approving writes the agreed times onto the day and keeps what the clock
// originally said.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/attendance/corrections")]
public class AttendanceCorrectionsController(
    IAttendanceCorrectionService corrections) : ApiControllerBase
{
    private const string Module = "time-payroll.attendance";

    [HttpGet("mine")]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<AttendanceCorrectionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await corrections.MineAsync(cancellationToken));

    [HttpPost("mine")]
    [RequiresEmployee]
    [ProducesResponseType<AttendanceCorrectionDto>(StatusCodes.Status201Created)]
    // Not named Request: that is ControllerBase's own HttpRequest, and hiding it
    // would shadow the thing every other action reads the caller from.
    public async Task<IActionResult> Raise(
        RequestCorrectionRequest request,
        CancellationToken cancellationToken)
    {
        CorrectionResult result = await corrections.RequestAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Correction!, "Correction requested.")
            : Failure<AttendanceCorrectionDto>(
                StatusFor(result.Outcome), result.Reason ?? MessageFor(result.Outcome));
    }

    [HttpGet]
    [ProducesResponseType<PagedResult<AttendanceCorrectionDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] CorrectionQuery query,
        CancellationToken cancellationToken) =>
        Ok(await corrections.ListAsync(query, cancellationToken));

    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<AttendanceCorrectionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Approve(
        Guid id,
        ResolveCorrectionRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await corrections.ApproveAsync(id, request.Note, cancellationToken),
            "Correction applied.");

    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<AttendanceCorrectionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Reject(
        Guid id,
        ResolveCorrectionRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await corrections.RejectAsync(id, request.Note ?? string.Empty, cancellationToken),
            "Correction turned down.");

    private IActionResult Respond(CorrectionResult result, string message) =>
        result.Succeeded
            ? Ok(result.Correction!, message)
            : Failure<AttendanceCorrectionDto>(
                StatusFor(result.Outcome), result.Reason ?? MessageFor(result.Outcome));

    private static string StatusFor(CorrectionOutcome outcome) => outcome switch
    {
        CorrectionOutcome.NotFound or CorrectionOutcome.EmployeeNotFound =>
            MoteeStatusCodes.NotFound,
        CorrectionOutcome.NotAllowed or CorrectionOutcome.AlreadyPending =>
            MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(CorrectionOutcome outcome) => outcome switch
    {
        CorrectionOutcome.NotFound => "Correction not found.",
        CorrectionOutcome.EmployeeNotFound => "Employee not found.",
        CorrectionOutcome.ReasonRequired =>
            "Say what was wrong with the day. Whoever reviews it has nothing to decide "
            + "on otherwise.",
        CorrectionOutcome.NothingToChange =>
            "That asks for the times already recorded.",
        CorrectionOutcome.InTheFuture => "That day has not happened yet.",
        CorrectionOutcome.OutOfOrder => "The clock-out is before the clock-in.",
        CorrectionOutcome.AlreadyPending =>
            "You already have a correction waiting on that day.",
        _ => "Could not record the correction.",
    };
}

public sealed record ResolveCorrectionRequest
{
    // Optional when approving — agreeing needs no explanation. Required when turning
    // one down, where the person is owed one.
    public string? Note { get; init; }
}
