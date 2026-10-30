using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Domain.Expenses;

namespace Motee.Application.Expenses;

// Claims, filed and reimbursed.
//
// The approval is not implemented here. A claim submits into the expense_claim chain
// and the engine decides it — the same engine, resolvers and delegation as everything
// else, so a company that configures "two approvers over £500" gets it everywhere
// rather than only where somebody remembered to reimplement it.
//
// What is here is the claim's own lifecycle around that: writing it, handing it in,
// taking it back, sending it back, and paying it.
public interface IExpenseClaimService
{
    Task<PagedResult<ExpenseClaimDto>> MineAsync(
        ExpenseQuery query,
        CancellationToken cancellationToken = default);

    Task<ExpenseClaimDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ExpenseResult> CreateAsync(
        ExpenseClaimRequest request,
        CancellationToken cancellationToken = default);

    Task<ExpenseResult> UpdateAsync(
        Guid id,
        ExpenseClaimRequest request,
        CancellationToken cancellationToken = default);

    // Hands it to the chain. Issues the reference, because that is the first moment
    // anybody else sees the claim and has something to quote.
    Task<ExpenseResult> SubmitAsync(Guid id, CancellationToken cancellationToken = default);

    // The filer taking it back before anybody has decided. Returns it to a draft,
    // keeping the reference — the claim is the same claim.
    Task<ExpenseResult> WithdrawAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ExpenseOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    // ── reviewing and paying ────────────────────────────────────────────────

    Task<PagedResult<ExpenseClaimDto>> ListAsync(
        ExpenseQuery query,
        CancellationToken cancellationToken = default);

    // Sent back to be rewritten rather than turned down. The difference matters: a
    // rejection ends the claim, a return asks for a better one.
    Task<ExpenseResult> ReturnAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default);

    // The money has gone. Separate from approval because agreeing to pay and paying
    // are different acts by different people, often days apart.
    Task<ExpenseResult> ReimburseAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record ExpenseClaimDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? DepartmentName { get; init; }

    // EXP-2026-00124. Null while it is a draft nobody has seen.
    public string? Reference { get; init; }

    public required string Title { get; init; }

    public required ExpenseCategory Category { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required string Merchant { get; init; }

    public required DateOnly IncurredOn { get; init; }

    public string? Notes { get; init; }

    public required ExpenseStatus Status { get; init; }

    public required bool Returned { get; init; }

    public string? ReturnedReason { get; init; }

    public required IReadOnlyList<Guid> ReceiptFileIds { get; init; }

    // The chain, straight from the engine — who it is with, which step, what has
    // been decided. Null for a draft.
    //
    // Served rather than summarised into a stage number, because the client already
    // renders this shape for leave and onboarding, and a second summary of the same
    // thing is a second thing to keep in step.
    public ApprovalDto? Review { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    public DateTimeOffset? ReimbursedAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record ExpenseClaimRequest
{
    public required string Title { get; init; }

    public required ExpenseCategory Category { get; init; }

    public required decimal Amount { get; init; }

    // ISO 4217. Defaults to the company's own currency when the client omits it,
    // which is the overwhelmingly common case.
    public string? Currency { get; init; }

    public required string Merchant { get; init; }

    public required DateOnly IncurredOn { get; init; }

    public string? Notes { get; init; }

    public IReadOnlyList<Guid>? ReceiptFileIds { get; init; }
}

public sealed record ExpenseQuery : PagedQuery
{
    public Guid? EmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }

    public ExpenseStatus? Status { get; init; }

    public ExpenseCategory? Category { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }
}

public enum ExpenseOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,

    // The lifecycle will not make that move. The message says which.
    NotAllowed,

    TitleRequired,
    MerchantRequired,

    // Zero or negative. A claim for nothing is a mistake, and a negative one is a
    // refund being filed as an expense.
    AmountNotPositive,

    // Not three letters. Not validated against the ISO list — that list changes and
    // a company trading somewhere unusual should not be told their currency is
    // invalid — but a shape check catches the typo.
    CurrencyInvalid,

    // Spent in the future. Almost always a mistyped year.
    IncurredInTheFuture,

    // No chain could be started, so nobody can review it. The claim stays a draft
    // rather than sitting submitted with no reviewer, because unlike an onboarding
    // pack this is somebody's money waiting.
    NoApprovalChain,

    ReasonRequired,
}

public sealed record ExpenseResult
{
    public required ExpenseOutcome Outcome { get; init; }

    public ExpenseClaimDto? Claim { get; init; }

    public string? Reason { get; init; }

    public bool Succeeded => Outcome == ExpenseOutcome.Succeeded;

    public static ExpenseResult Failed(ExpenseOutcome outcome, string? reason = null) =>
        new() { Outcome = outcome, Reason = reason };

    public static ExpenseResult Ok(ExpenseClaimDto claim) =>
        new() { Outcome = ExpenseOutcome.Succeeded, Claim = claim };
}
