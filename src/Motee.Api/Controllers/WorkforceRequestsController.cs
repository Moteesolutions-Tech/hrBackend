using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Workforce;
using Motee.Domain.Authorization;
using Motee.Domain.Workforce;

namespace Motee.Api.Controllers;

// Workforce requests: asking for headcount.
//
// Approving is not here. A request is submitted to the approval engine and
// decided through the approvals module with every other chain, so a tenant's
// configured route applies to headcount the same way it applies to expenses.
// Putting an /approve route on this controller would be a second approval
// mechanism, and the two would disagree the first time somebody edited a
// template.
//
// /convert is the one that closes the chain: an approved request becomes a
// vacancy, carrying its position, department, contract shape, openings and
// start date across.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/workforce-requests")]
public class WorkforceRequestsController(IWorkforceRequestService requests) : ApiControllerBase
{
    private const string Module = "talent.workforce-requests";

    [HttpGet]
    [ProducesResponseType<PagedResult<WorkforceRequestDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] WorkforceRequestQuery query,
        CancellationToken cancellationToken) =>
        Ok(await requests.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<WorkforceRequestDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        WorkforceRequestDto? found = await requests.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<WorkforceRequestDto>(MoteeStatusCodes.NotFound, "Request not found.")
            : Ok(found);
    }

    [HttpPost]
    [ProducesResponseType<WorkforceRequestDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        WorkforceRequestRequest request,
        CancellationToken cancellationToken)
    {
        WorkforceRequestResult result = await requests.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Request!, "Request saved as a draft.")
            : Failure<WorkforceRequestDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<WorkforceRequestDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        WorkforceRequestRequest request,
        CancellationToken cancellationToken) =>
        Respond(await requests.UpdateAsync(id, request, cancellationToken), "Request updated.");

    // Hands it to the approval engine and issues its reference. Also the
    // resubmit route: a request that came back for changes goes the same way.
    [HttpPost("{id:guid}/submit")]
    [ProducesResponseType<WorkforceRequestDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Submit(Guid id, CancellationToken cancellationToken) =>
        Respond(await requests.SubmitAsync(id, cancellationToken), "Sent for approval.");

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<WorkforceRequestDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Cancel(
        Guid id,
        WorkforceCancelRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await requests.CancelAsync(id, request.Reason, cancellationToken),
            "Request withdrawn.");

    // Closes the chain: authorised headcount becomes a vacancy.
    [HttpPost("{id:guid}/convert")]
    [ProducesResponseType<WorkforceRequestDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Convert(Guid id, CancellationToken cancellationToken) =>
        Respond(
            await requests.ConvertToRequisitionAsync(id, cancellationToken),
            "Vacancy created from the request.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await requests.DeleteAsync(id, cancellationToken) switch
        {
            WorkforceRequestOutcome.Succeeded => Ok<object?>(null, "Draft deleted."),
            WorkforceRequestOutcome.NotFound =>
                Failure<object?>(MoteeStatusCodes.NotFound, "Request not found."),

            // Once it has been in front of an approver it is part of the
            // authorisation record. Withdrawing is what cancelling is for.
            _ => Failure<object?>(
                MoteeStatusCodes.Conflict,
                "This request has been submitted. Withdraw it instead of deleting it."),
        };

    private IActionResult Respond(WorkforceRequestResult result, string message) =>
        result.Succeeded
            ? Ok(result.Request!, message)
            : Failure<WorkforceRequestDto>(StatusFor(result), MessageFor(result));

    private static string StatusFor(WorkforceRequestResult result) => result.Outcome switch
    {
        WorkforceRequestOutcome.NotFound or WorkforceRequestOutcome.DepartmentNotFound =>
            MoteeStatusCodes.NotFound,

        WorkforceRequestOutcome.CouldNotStartApproval
            or WorkforceRequestOutcome.CouldNotCreateRequisition => MoteeStatusCodes.Conflict,

        WorkforceRequestOutcome.Refused => ConflictOrRequest(result.Refusal),

        _ => MoteeStatusCodes.InvalidRequest,
    };

    // A refusal about the request's state is a conflict; one about what is
    // missing from it is a bad request the caller can fix.
    private static string ConflictOrRequest(WorkforceRefusal refusal) => refusal switch
    {
        WorkforceRefusal.PositionRequired or WorkforceRefusal.JustificationRequired
            or WorkforceRefusal.HiresNotPositive or WorkforceRefusal.CostCentreRequired
            or WorkforceRefusal.DepartmentRequired => MoteeStatusCodes.InvalidRequest,

        _ => MoteeStatusCodes.Conflict,
    };

    private static string MessageFor(WorkforceRequestResult result) => result.Outcome switch
    {
        WorkforceRequestOutcome.NotFound => "Request not found.",
        WorkforceRequestOutcome.DepartmentNotFound => "That department does not exist.",
        WorkforceRequestOutcome.ReasonRequired => "Say why it is being withdrawn.",

        WorkforceRequestOutcome.CouldNotStartApproval =>
            "No approval route is set up for headcount requests. "
            + "Add one under approval workflows first.",

        WorkforceRequestOutcome.CouldNotCreateRequisition =>
            "Could not raise the vacancy from this request.",

        _ => ReasonFor(result.Refusal),
    };

    // Sentences, because they are shown as written.
    private static string ReasonFor(WorkforceRefusal refusal) => refusal switch
    {
        WorkforceRefusal.PositionRequired => "Say which role is being asked for.",
        WorkforceRefusal.JustificationRequired =>
            "Make the case for the headcount — it is what an approver reads.",
        WorkforceRefusal.HiresNotPositive => "Ask for at least one person.",
        WorkforceRefusal.CostCentreRequired =>
            "Give a cost centre. Finance books the spend against it, and without "
            + "one there is nothing to charge.",
        WorkforceRefusal.AlreadySubmitted =>
            "This request is already with approvers.",
        WorkforceRefusal.NotSubmittable =>
            "This request has already been approved.",
        WorkforceRefusal.NotApproved =>
            "Only an approved request becomes a vacancy.",
        WorkforceRefusal.AlreadyConverted =>
            "A vacancy has already been raised from this request.",
        WorkforceRefusal.AlreadyClosed =>
            "This request has been withdrawn or already became a vacancy.",
        WorkforceRefusal.DepartmentRequired =>
            "Set the department before raising the vacancy — a vacancy needs one.",
        _ => "Could not save the request.",
    };
}

public sealed record WorkforceCancelRequest
{
    public required string Reason { get; init; }
}
