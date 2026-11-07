using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Loans;
using Motee.Domain.Authorization;
using Motee.Domain.Identity;
using Motee.Domain.Loans;

namespace Motee.Api.Controllers;

// The staff loan and salary advance register.
//
// There is no /approve for a loan that went down the chain: the chain is the
// decision, and the register reads it. The approve and reject routes here are
// for a loan with no chain behind it — an imported one, or a company that does
// not route loans for approval at all.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/loans")]
public class LoansController(
    ILoanService loans,
    IUserPermissions userPermissions) : ApiControllerBase
{
    private const string Module = "organization.loans";

    [HttpGet]
    [ProducesResponseType<PagedResult<LoanDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] LoanQuery query,
        CancellationToken cancellationToken) =>
        Ok(await loans.ListAsync(query, cancellationToken));

    [HttpGet("summary")]
    [ProducesResponseType<LoanBookDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await loans.SummaryAsync(cancellationToken));

    // The signed-in employee's own. Resolved from the session rather than from
    // a route id, so there is nothing to change to read somebody else's — and
    // no module permission, because applying for a loan is not an HR function.
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<IReadOnlyList<LoanDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await loans.MineAsync(cancellationToken));

    // One person's loans, for the employee record's Loans section. Gated on
    // the register rather than on the employee directory: what somebody has
    // borrowed is financial, and a line manager who can open a record has no
    // business reading it.
    [HttpGet("employees/{employeeId:guid}")]
    [ProducesResponseType<IReadOnlyList<LoanDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> ForEmployee(
        Guid employeeId,
        CancellationToken cancellationToken) =>
        Ok(await loans.ForEmployeeAsync(employeeId, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<LoanDetailDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        LoanDetailDto? found = await loans.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<LoanDetailDto>(MoteeStatusCodes.NotFound, "Loan not found.")
            : Ok(found);
    }

    // Applying for yourself needs no module permission; recording one for
    // somebody else does. Both land here, and the service treats an omitted
    // employee as the signed-in one.
    [HttpPost]
    [Authorize]
    [ProducesResponseType<LoanDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Apply(
        LoanRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EmployeeId is not null
            && !await CanRecordForOthersAsync(cancellationToken))
        {
            return Failure<LoanDto>(
                MoteeStatusCodes.Forbidden,
                "You can apply for your own loan. Recording one for somebody else "
                + "needs access to the loan register.");
        }

        LoanResult result = await loans.RequestAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Loan!, "Loan request submitted for approval.")
            : Failure<LoanDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<LoanDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Approve)]
    public async Task<IActionResult> Approve(
        Guid id,
        ApproveLoanRequest? request,
        CancellationToken cancellationToken) =>
        Respond(
            await loans.ApproveAsync(id, request?.IssuedOn, cancellationToken),
            "Loan approved and issued.");

    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<LoanDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Approve)]
    public async Task<IActionResult> Reject(Guid id, CancellationToken cancellationToken) =>
        Respond(await loans.RejectAsync(id, cancellationToken), "Loan request rejected.");

    [HttpPost("{id:guid}/repayments")]
    [ProducesResponseType<LoanDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> RecordRepayment(
        Guid id,
        LoanRepaymentRequest request,
        CancellationToken cancellationToken)
    {
        LoanResult result = await loans.RecordRepaymentAsync(id, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(
                result.Loan!,
                result.Loan!.Status == LoanStatus.Closed
                    ? "Repayment recorded. The loan is now fully repaid."
                    : "Repayment recorded.")
            : Failure<LoanDto>(StatusFor(result), MessageFor(result));
    }

    // Reverses one ledger row. A mis-keyed repayment is undoable, which is the
    // whole reason this is a ledger rather than a running total.
    [HttpDelete("{id:guid}/repayments/{repaymentId:guid}")]
    [ProducesResponseType<LoanDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> ReverseRepayment(
        Guid id,
        Guid repaymentId,
        CancellationToken cancellationToken) =>
        Respond(
            await loans.ReverseRepaymentAsync(id, repaymentId, cancellationToken),
            "Repayment reversed.");

    [HttpPost("{id:guid}/default")]
    [ProducesResponseType<LoanDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> MarkDefaulted(
        Guid id,
        CancellationToken cancellationToken) =>
        Respond(
            await loans.MarkDefaultedAsync(id, cancellationToken),
            "Loan marked as defaulted. Repayments can still be recorded against it.");

    [HttpPost("{id:guid}/reinstate")]
    [ProducesResponseType<LoanDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Reinstate(Guid id, CancellationToken cancellationToken) =>
        Respond(await loans.ReinstateAsync(id, cancellationToken), "Loan reinstated.");

    // Resolved here rather than as an attribute because one endpoint serves
    // both cases: an employee applying for themselves and HR recording a loan
    // for somebody else arrive at the same route, and only the body says which.
    private async Task<bool> CanRecordForOthersAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(MoteeClaimTypes.Subject)?.Value, out Guid userId))
        {
            return false;
        }

        ResolvedPermissions held = await userPermissions.ForAsync(userId, cancellationToken);

        return held.IsOwner
            || AccessDecision.Resolve(held.Permissions, Module, PermissionAction.Create)
                .Kind != DataScopeKind.None;
    }

    private IActionResult Respond(LoanResult result, string message) =>
        result.Succeeded
            ? Ok(result.Loan!, message)
            : Failure<LoanDto>(StatusFor(result), MessageFor(result));

    private static string StatusFor(LoanResult result) => result.Outcome switch
    {
        LoanOutcome.NotFound or LoanOutcome.EmployeeNotFound => MoteeStatusCodes.NotFound,
        LoanOutcome.Forbidden => MoteeStatusCodes.Forbidden,

        LoanOutcome.Refused when result.Refusal
            is LoanRefusal.AlreadyClosed
            or LoanRefusal.NotActive
            or LoanRefusal.NotIssued => MoteeStatusCodes.Conflict,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(LoanResult result) => result.Outcome switch
    {
        LoanOutcome.NotFound => "Loan not found.",
        LoanOutcome.EmployeeNotFound => "That person does not work here.",
        LoanOutcome.Forbidden => "You cannot act on this loan.",
        LoanOutcome.CouldNotStartApproval =>
            "No loan approval chain is configured, so the request could not be sent.",
        _ => ReasonFor(result.Refusal),
    };

    private static string ReasonFor(LoanRefusal refusal) => refusal switch
    {
        LoanRefusal.AmountNotPositive => "Enter the amount being requested.",
        LoanRefusal.MonthsOutOfRange =>
            $"The repayment period has to be between 1 and {EmployeeLoans.LongestTermMonths} months.",
        LoanRefusal.PurposeRequired => "Say what the loan is for.",
        LoanRefusal.InterestOutOfRange => "Interest has to be a percentage between 0 and 100.",
        LoanRefusal.TypeNotOfferedInThisCountry =>
            "That loan product is not one your company offers.",
        LoanRefusal.NotIssued =>
            "This loan has not been issued, so there is nothing to repay against it yet.",
        LoanRefusal.RepaymentNotPositive => "Enter the amount received.",
        LoanRefusal.RepaymentExceedsBalance =>
            "That is more than the outstanding balance. Record what was actually "
            + "received against the loan, and handle the difference separately.",
        LoanRefusal.AlreadyClosed => "This loan is fully repaid.",
        LoanRefusal.NotActive =>
            "Only a loan that has been issued and is still owing can be marked as defaulted.",
        _ => "Could not save the loan.",
    };
}

public sealed record ApproveLoanRequest
{
    // Omitted means today. Set when recording a loan that was issued earlier
    // — an import, or one agreed before the register existed.
    public DateOnly? IssuedOn { get; init; }
}
