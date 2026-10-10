using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Approvals;
using Motee.Domain.Approvals;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/approval-delegations")]
public class ApprovalDelegationsController(IApprovalDelegationService delegations)
    : ApiControllerBase
{
    private const string Module = "submissions.workflows";

    // No permission attribute on the three below: arranging cover for your own approvals
    // is self-service, and requiring the workflows module would mean granting every
    // manager the screen that configures the company's chains.
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await delegations.MineAsync(cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(
        ApprovalDelegationRequest request,
        CancellationToken cancellationToken)
    {
        ApprovalDelegationResult result = await delegations.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Delegation!, "Cover arranged.")
            : Failure<ApprovalDelegationDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        ApprovalDelegationOutcome outcome = await delegations.CancelAsync(id, cancellationToken);

        return outcome == ApprovalDelegationOutcome.Succeeded
            ? Ok<object?>(null, "Cover cancelled.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    // Who is covering for whom across the company. A different question from the panel
    // above, and an administrator's rather than an individual's.
    [HttpGet]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Active(CancellationToken cancellationToken) =>
        Ok(await delegations.ActiveAsync(cancellationToken));

    private static string StatusFor(ApprovalDelegationOutcome outcome) => outcome switch
    {
        ApprovalDelegationOutcome.NotFound => MoteeStatusCodes.NotFound,
        ApprovalDelegationOutcome.NotTheirs => MoteeStatusCodes.Forbidden,
        ApprovalDelegationOutcome.Overlapping => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(ApprovalDelegationOutcome outcome) => outcome switch
    {
        ApprovalDelegationOutcome.NotFound => "That arrangement no longer exists.",
        ApprovalDelegationOutcome.NotTheirs => "Only the person who arranged cover can cancel it.",
        ApprovalDelegationOutcome.EndBeforeStart => "The end date is before the start date.",
        ApprovalDelegationOutcome.TooLong =>
            $"Cover can be arranged for up to {DelegationRules.MaximumDays} days. "
            + "For longer than that, change the reporting line instead.",
        ApprovalDelegationOutcome.DelegatingToSelf => "You cannot delegate to yourself.",
        ApprovalDelegationOutcome.Overlapping =>
            "You already have cover arranged over some of those dates.",
        ApprovalDelegationOutcome.UnknownDelegate => "That person is not an employee here.",
        ApprovalDelegationOutcome.DelegateUnavailable =>
            "That person cannot approve on your behalf — they have no active account.",
        ApprovalDelegationOutcome.NoEmployeeRecord =>
            "Your account has no employee record, so there is nothing to delegate.",
        _ => "Could not arrange cover.",
    };
}
