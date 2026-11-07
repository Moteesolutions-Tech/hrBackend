using Motee.Application.Common;
using Motee.Domain.Approvals;
using Motee.Domain.Loans;

namespace Motee.Application.Loans;

// The staff loan and salary advance register.
//
// Record-keeping and tracking, not payroll. Nothing here deducts anything —
// repayments are posted as ledger rows, which is the shape payroll will post
// into when it arrives rather than a counter two systems would both try to
// increment.
//
// The status, the balance and the schedule are all derived. A loan's status in
// particular comes from its approval chain, so the register cannot read
// "pending" after Finance approved it or "defaulted" after the debt was
// settled.
public interface ILoanService
{
    Task<PagedResult<LoanDto>> ListAsync(
        LoanQuery query,
        CancellationToken cancellationToken = default);

    // The loan, its repayment schedule, its repayment history and where its
    // approval stands — everything the detail page draws.
    Task<LoanDetailDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // The register's headline figures, the book by product and by department,
    // and the largest balances outstanding.
    Task<LoanBookDto> SummaryAsync(CancellationToken cancellationToken = default);

    // Raised by the employee or by HR on their behalf, and sent down the
    // loan_request chain. Approving it there is what issues the money.
    Task<LoanResult> RequestAsync(
        LoanRequest request,
        CancellationToken cancellationToken = default);

    // A direct decision, for a loan with no chain behind it — an imported one,
    // or where the company does not route loans for approval at all.
    Task<LoanResult> ApproveAsync(
        Guid id,
        DateOnly? issuedOn = null,
        CancellationToken cancellationToken = default);

    Task<LoanResult> RejectAsync(Guid id, CancellationToken cancellationToken = default);

    // Written off. Repayments can still be recorded against it, and settling
    // the balance closes it.
    Task<LoanResult> MarkDefaultedAsync(Guid id, CancellationToken cancellationToken = default);

    // Undoes a default somebody marked by mistake. Not on the frontend, which
    // offers no way back from a one-click irreversible label.
    Task<LoanResult> ReinstateAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LoanResult> RecordRepaymentAsync(
        Guid id,
        LoanRepaymentRequest request,
        CancellationToken cancellationToken = default);

    // Reverses one ledger row. A duplicated repayment is visible and
    // reversible, which is the whole reason the ledger exists rather than a
    // running total.
    Task<LoanResult> ReverseRepaymentAsync(
        Guid id,
        Guid repaymentId,
        CancellationToken cancellationToken = default);

    // One person's loans. Used by their own self-service page and by the
    // employee record's Loans section.
    Task<IReadOnlyList<LoanDto>> ForEmployeeAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default);

    // The signed-in employee's own, resolved from the request context so no
    // caller can ask for somebody else's by changing an id.
    Task<IReadOnlyList<LoanDto>> MineAsync(CancellationToken cancellationToken = default);
}

public sealed record LoanDto
{
    public required Guid Id { get; init; }

    public required string Reference { get; init; }

    public required Guid EmployeeId { get; init; }

    public string? EmployeeName { get; init; }

    public string? DepartmentName { get; init; }

    public required LoanType LoanType { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required string Purpose { get; init; }

    public required int RepaymentMonths { get; init; }

    // Derived from the amount and the term, never stored — a copy would
    // survive an edit to either and disagree with the schedule below it.
    public required decimal MonthlyRepayment { get; init; }

    // Derived: the sum of the repayment ledger.
    public required decimal AmountRepaid { get; init; }

    public required decimal Outstanding { get; init; }

    public decimal? InterestRatePercent { get; init; }

    public required DateOnly RequestedOn { get; init; }

    // The recorded date, or the day the chain approved it.
    public DateOnly? IssuedOn { get; init; }

    public DateOnly? DefaultedOn { get; init; }

    // Derived from the chain, the ledger and the default, in that order.
    public required LoanStatus Status { get; init; }

    public Guid? ApprovalInstanceId { get; init; }

    public ApprovalStatus? ApprovalStatus { get; init; }

    // The next instalment still owing, so a list can say what is coming
    // without every row carrying a whole schedule.
    public Instalment? NextInstalment { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record LoanDetailDto
{
    public required LoanDto Loan { get; init; }

    public required IReadOnlyList<Instalment> Schedule { get; init; }

    public required IReadOnlyList<LoanRepaymentDto> Repayments { get; init; }
}

public sealed record LoanRepaymentDto
{
    public required Guid Id { get; init; }

    public required decimal Amount { get; init; }

    public required DateOnly PaidOn { get; init; }

    public required LoanRepaymentMethod Method { get; init; }

    public string? Reference { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }
}

public sealed record LoanBookDto
{
    public required LoanBookTally Totals { get; init; }

    public required IReadOnlyList<LoanTypeTally> ByType { get; init; }

    // Outstanding balance per department, biggest first. Only departments with
    // something owed, so a company with one loan does not get a chart of zeros.
    public required IReadOnlyList<LoanDepartmentTally> ByDepartment { get; init; }

    // The five largest balances, which is what the register leads with when
    // somebody asks where the exposure is.
    public required IReadOnlyList<LoanDto> LargestBalances { get; init; }
}

public sealed record LoanDepartmentTally
{
    public Guid? DepartmentId { get; init; }

    public required string DepartmentName { get; init; }

    public required decimal Outstanding { get; init; }

    public required int Loans { get; init; }
}

public sealed record LoanQuery : PagedQuery
{
    public LoanStatus? Status { get; init; }

    public LoanType? LoanType { get; init; }

    public Guid? EmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }

    // Reference, purpose, or the borrower's name.
    public string? Search { get; init; }
}

public sealed record LoanRequest
{
    // Omitted means the signed-in employee is applying for themselves, which
    // is the self-service case. Naming somebody else is HR recording it on
    // their behalf and needs the register's own permission.
    public Guid? EmployeeId { get; init; }

    public required LoanType LoanType { get; init; }

    public required decimal Amount { get; init; }

    public required int RepaymentMonths { get; init; }

    public required string Purpose { get; init; }

    public decimal? InterestRatePercent { get; init; }

    // Files already uploaded through the files module, carried onto the
    // approval chain — a signed repayment undertaking, usually.
    public IReadOnlyList<Guid> FileIds { get; init; } = [];
}

public sealed record LoanRepaymentRequest
{
    public required decimal Amount { get; init; }

    // Omitted means today, in the company's own calendar.
    public DateOnly? PaidOn { get; init; }

    public LoanRepaymentMethod Method { get; init; } = LoanRepaymentMethod.Cash;

    public string? Reference { get; init; }

    public string? Note { get; init; }
}

public enum LoanOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,
    Refused,

    // The caller is asking about somebody else's loan without the register's
    // permission. Distinct from NotFound so the API does not have to lie,
    // which it only needs to avoid where the id itself is the secret.
    Forbidden,

    // The chain could not be started: no template for loan_request, or the
    // engine refused it.
    CouldNotStartApproval,
}

public sealed record LoanResult
{
    public required LoanOutcome Outcome { get; init; }

    public LoanDto? Loan { get; init; }

    public LoanRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == LoanOutcome.Succeeded;

    public static LoanResult Failed(LoanOutcome outcome) => new() { Outcome = outcome };

    public static LoanResult Refused(LoanRefusal refusal) =>
        new() { Outcome = LoanOutcome.Refused, Refusal = refusal };

    public static LoanResult Ok(LoanDto loan) =>
        new() { Outcome = LoanOutcome.Succeeded, Loan = loan };
}
