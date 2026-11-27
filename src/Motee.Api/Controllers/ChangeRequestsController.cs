using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// HR's queue of profile change requests, and the decisions on them.
//
// Behind the employee module's Edit rather than View, because approving one writes to
// the employee record. Reading the queue is not the privilege here; deciding is.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/change-requests")]
public class ChangeRequestsController(IChangeRequestService requests) : ApiControllerBase
{
    private const string Module = "organization.employees";

    // Pending by default, which the query does: that is the only status anybody has to
    // act on, and a queue that opens on every request ever made is a list nobody reads.
    [HttpGet]
    [ProducesResponseType<PagedResult<ChangeRequestDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> List(
        [FromQuery] ChangeRequestQuery query,
        CancellationToken cancellationToken) =>
        Ok(await requests.ListAsync(query, cancellationToken));

    // Applies the change to the record, then marks the request decided — one
    // transaction, because a request recorded as approved whose field never changed is
    // worse than a failure: nothing afterwards will notice.
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<ChangeRequestDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Approve(
        Guid id,
        [FromBody] ApproveChangeRequest? request,
        CancellationToken cancellationToken) =>
        Respond(
            await requests.ApproveAsync(id, request?.Note, cancellationToken),
            "Change applied.");

    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<ChangeRequestDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Reject(
        Guid id,
        RejectChangeRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await requests.RejectAsync(id, request.Reason, cancellationToken),
            "Change request rejected.");

    private IActionResult Respond(ChangeRequestResult result, string message) =>
        result.Succeeded
            ? Ok(result.Request!, message)
            : Failure<ChangeRequestDto>(
                ChangeRequestResponses.StatusFor(result.Outcome),
                result.Reason ?? ChangeRequestResponses.MessageFor(result.Outcome));
}

public sealed record ApproveChangeRequest
{
    // Optional. An approval needs no explanation — the request already carries one.
    public string? Note { get; init; }
}

public sealed record RejectChangeRequest
{
    public required string Reason { get; init; }
}

// Shared with the self-service controller, so the two cannot drift into describing the
// same outcome differently.
internal static class ChangeRequestResponses
{
    public static string StatusFor(ChangeRequestOutcome outcome) => outcome switch
    {
        ChangeRequestOutcome.NotFound => MoteeStatusCodes.NotFound,
        ChangeRequestOutcome.NoEmployeeRecord or ChangeRequestOutcome.NotTheRequester =>
            MoteeStatusCodes.Forbidden,

        // Conflict rather than a bad request: nothing about the request was wrong, the
        // record is simply not in a state that can take it.
        ChangeRequestOutcome.AlreadyDecided or ChangeRequestOutcome.AlreadyPending
            or ChangeRequestOutcome.NoChange => MoteeStatusCodes.Conflict,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    public static string MessageFor(ChangeRequestOutcome outcome) => outcome switch
    {
        ChangeRequestOutcome.NotFound => "Change request not found.",
        ChangeRequestOutcome.NoEmployeeRecord =>
            "This account has no employee record, so it has no details to change.",
        ChangeRequestOutcome.NotTheRequester => "That request was raised by somebody else.",
        ChangeRequestOutcome.AlreadyDecided => "That request has already been decided.",
        ChangeRequestOutcome.AlreadyPending =>
            "There is already a request waiting on that field.",
        ChangeRequestOutcome.NoChange => "That is the value already on the record.",
        ChangeRequestOutcome.FieldNotRequestable =>
            "That is not a field which can be changed by request.",
        ChangeRequestOutcome.ReasonRequired => "Say why the change is needed.",
        ChangeRequestOutcome.ReasonTooLong => "That explanation is too long.",
        ChangeRequestOutcome.NoteRequired =>
            "Say why it was refused — being told no without a reason sends somebody to "
            + "HR to ask.",
        ChangeRequestOutcome.UnknownFile => "That attachment is not on file.",
        ChangeRequestOutcome.ValueRefused => "That value cannot go on the record.",
        _ => "That change request was not accepted.",
    };
}
