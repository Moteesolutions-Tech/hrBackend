using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Domain.Approvals;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Approvals in flight. Deliberately no "start" endpoint: a chain is started by the module
// that needs it — onboarding, offboarding, leave — never by a client, or a caller could
// raise an approval for something that never happened.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/approvals")]
public class ApprovalsController(IApprovalService approvals) : ApiControllerBase
{
    private const string Module = "submissions.queue";

    // What is waiting on the person asking. No permission beyond being signed in: this
    // is their own queue, and gating it behind a module would hide from a line manager
    // the very thing they have been asked to decide.
    [HttpGet("my-queue")]
    [ProducesResponseType<PagedResult<ApprovalDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MyQueue(
        [FromQuery] PagedQuery query,
        CancellationToken cancellationToken) =>
        Ok(await approvals.MyQueueAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ApprovalDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        ApprovalDto? approval = await approvals.GetAsync(id, cancellationToken);

        return approval is null
            ? Failure<ApprovalDto>(MoteeStatusCodes.NotFound, "Approval not found.")
            : Ok(approval);
    }

    // Everything running against one record, so a module can show "waiting on the
    // department head" without knowing the engine's tables.
    [HttpGet("for/{subjectType}/{subjectId:guid}")]
    [ProducesResponseType<IReadOnlyList<ApprovalDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> ForSubject(
        string subjectType,
        Guid subjectId,
        CancellationToken cancellationToken) =>
        Ok(await approvals.ForSubjectAsync(subjectType, subjectId, cancellationToken));

    // No permission attribute on purpose. The right to decide comes from being the person
    // the step resolved to, which the service checks — a module permission would let
    // anyone holding it approve somebody else's step, and the chain's order would then
    // describe nothing.
    [HttpPost("{id:guid}/decide")]
    [ProducesResponseType<ApprovalDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Decide(
        Guid id,
        DecideApprovalRequest request,
        CancellationToken cancellationToken) =>
        Respond(await approvals.DecideAsync(id, request.Decision, request.Note, cancellationToken));

    [HttpPost("{id:guid}/resubmit")]
    [ProducesResponseType<ApprovalDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resubmit(Guid id, CancellationToken cancellationToken) =>
        Respond(await approvals.ResubmitAsync(id, cancellationToken));

    // Ask a blocked approval to look again for its approver, once the reason it was stuck
    // has been dealt with elsewhere — a head appointed, somebody assigned to the level.
    //
    // Behind Edit rather than View: it is the only way to unstick a chain short of
    // cancelling it, and it changes who work is sitting with.
    [HttpPost("{id:guid}/reresolve")]
    [ProducesResponseType<ApprovalDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Reresolve(Guid id, CancellationToken cancellationToken) =>
        Respond(await approvals.ReresolveAsync(id, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<ApprovalDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Cancel(
        Guid id,
        [FromBody] CancelApprovalRequest? request,
        CancellationToken cancellationToken) =>
        Respond(await approvals.CancelAsync(id, request?.Reason, cancellationToken));

    private IActionResult Respond(ApprovalResult result) =>
        result.Succeeded
            ? Ok(result.Approval!, "Approval updated.")
            : Failure<ApprovalDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));

    private static string StatusFor(ApprovalOutcome outcome) => outcome switch
    {
        ApprovalOutcome.NotFound or ApprovalOutcome.TemplateNotFound => MoteeStatusCodes.NotFound,
        ApprovalOutcome.NotTheApprover => MoteeStatusCodes.Forbidden,
        ApprovalOutcome.NotAllowed or ApprovalOutcome.Unstartable => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InternalServerError,
    };

    private static string MessageFor(ApprovalOutcome outcome) => outcome switch
    {
        ApprovalOutcome.NotFound => "Approval not found.",
        ApprovalOutcome.TemplateNotFound =>
            "No approval chain is configured for this. Set a default template first.",

        // Says which of the two it is. "Not allowed" covers both deciding something
        // already decided and deciding out of turn, and the caller can act on neither
        // without being told which.
        ApprovalOutcome.NotTheApprover => "This step is waiting on somebody else.",
        ApprovalOutcome.NotAllowed => "That is not possible for this approval in its current state.",
        ApprovalOutcome.Unstartable => "That approval chain has no steps to run.",
        _ => "Could not update the approval.",
    };
}

public sealed record DecideApprovalRequest
{
    // Approved, Rejected or Returned. Skipped is not a decision a person makes — the
    // engine writes it when nobody could be resolved.
    public required ApprovalStepStatus Decision { get; init; }

    public string? Note { get; init; }
}

public sealed record CancelApprovalRequest
{
    public string? Reason { get; init; }
}
