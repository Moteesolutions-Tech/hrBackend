using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Expenses;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Claims, filed and reimbursed.
//
// There is no approve or reject here on purpose. A submitted claim is decided
// through /approvals/{id}/decide like everything else the engine runs, so a company
// that configures its own chain — two approvers over a threshold, a delegate while
// somebody is away — gets that applied to expenses rather than only where it was
// reimplemented. Returning and reimbursing are the claim's own, because neither is
// an approval: one sends it back to be rewritten, the other records that the money
// has gone.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/expenses/claims")]
public class ExpenseClaimsController(IExpenseClaimService claims) : ApiControllerBase
{
    private const string Module = "time-payroll.expenses";

    [HttpGet("mine")]
    [RequiresEmployee]
    [ProducesResponseType<PagedResult<ExpenseClaimDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(
        [FromQuery] ExpenseQuery query,
        CancellationToken cancellationToken) =>
        Ok(await claims.MineAsync(query, cancellationToken));

    [HttpPost("mine")]
    [RequiresEmployee]
    [ProducesResponseType<ExpenseClaimDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        ExpenseClaimRequest request,
        CancellationToken cancellationToken)
    {
        ExpenseResult result = await claims.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Claim!, "Claim saved.")
            : Failure<ExpenseClaimDto>(
                StatusFor(result.Outcome), result.Reason ?? MessageFor(result.Outcome));
    }

    [HttpPut("mine/{id:guid}")]
    [RequiresEmployee]
    [ProducesResponseType<ExpenseClaimDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        Guid id,
        ExpenseClaimRequest request,
        CancellationToken cancellationToken) =>
        Respond(await claims.UpdateAsync(id, request, cancellationToken), "Claim updated.");

    [HttpPost("mine/{id:guid}/submit")]
    [RequiresEmployee]
    [ProducesResponseType<ExpenseClaimDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Submit(Guid id, CancellationToken cancellationToken) =>
        Respond(await claims.SubmitAsync(id, cancellationToken), "Claim submitted.");

    [HttpPost("mine/{id:guid}/withdraw")]
    [RequiresEmployee]
    [ProducesResponseType<ExpenseClaimDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken cancellationToken) =>
        Respond(await claims.WithdrawAsync(id, cancellationToken), "Claim withdrawn.");

    [HttpDelete("mine/{id:guid}")]
    [RequiresEmployee]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        ExpenseOutcome outcome = await claims.DeleteAsync(id, cancellationToken);

        return outcome == ExpenseOutcome.Succeeded
            ? Ok<object?>(null, "Claim deleted.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    // ── reviewing and paying ────────────────────────────────────────────────

    [HttpGet]
    [ProducesResponseType<PagedResult<ExpenseClaimDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] ExpenseQuery query,
        CancellationToken cancellationToken) =>
        Ok(await claims.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ExpenseClaimDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        ExpenseClaimDto? claim = await claims.GetAsync(id, cancellationToken);

        return claim is null
            ? Failure<ExpenseClaimDto>(MoteeStatusCodes.NotFound, "Claim not found.")
            : Ok(claim);
    }

    // Sent back to be rewritten rather than turned down. A rejection ends the
    // claim; a return asks for a better one, and the difference is what the person
    // needs to know.
    [HttpPost("{id:guid}/return")]
    [ProducesResponseType<ExpenseClaimDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Return(
        Guid id,
        ReturnClaimRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await claims.ReturnAsync(id, request.Reason, cancellationToken),
            "Claim sent back.");

    // The money has gone. Behind Edit on the expenses module rather than the
    // approval permission, because paying is Finance's act and not another
    // approval step.
    [HttpPost("{id:guid}/reimburse")]
    [ProducesResponseType<ExpenseClaimDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Reimburse(Guid id, CancellationToken cancellationToken) =>
        Respond(await claims.ReimburseAsync(id, cancellationToken), "Claim marked as paid.");

    private IActionResult Respond(ExpenseResult result, string message) =>
        result.Succeeded
            ? Ok(result.Claim!, message)
            : Failure<ExpenseClaimDto>(
                StatusFor(result.Outcome), result.Reason ?? MessageFor(result.Outcome));

    private static string StatusFor(ExpenseOutcome outcome) => outcome switch
    {
        ExpenseOutcome.NotFound or ExpenseOutcome.EmployeeNotFound => MoteeStatusCodes.NotFound,
        ExpenseOutcome.NotAllowed => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(ExpenseOutcome outcome) => outcome switch
    {
        ExpenseOutcome.NotFound => "Claim not found.",
        ExpenseOutcome.EmployeeNotFound => "Employee not found.",
        ExpenseOutcome.TitleRequired => "Give the claim a title.",
        ExpenseOutcome.MerchantRequired => "Say who the money was paid to.",
        ExpenseOutcome.AmountNotPositive => "The amount has to be more than nothing.",
        ExpenseOutcome.CurrencyInvalid =>
            "Use a three-letter currency code, such as NGN or GBP.",
        ExpenseOutcome.IncurredInTheFuture =>
            "That date has not happened yet. Check the year.",
        ExpenseOutcome.NoApprovalChain =>
            "No approval chain could be started, so nobody can review this claim. "
            + "It has been left as a draft — check the expense workflow in Settings.",
        ExpenseOutcome.ReasonRequired =>
            "Say what needs fixing, or the person has to guess and will send the "
            + "same claim back.",
        _ => "Could not save the claim.",
    };
}

public sealed record ReturnClaimRequest
{
    public required string Reason { get; init; }
}
