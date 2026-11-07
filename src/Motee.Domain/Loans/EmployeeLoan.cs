using Motee.Domain.Approvals;
using Motee.Domain.Common;

namespace Motee.Domain.Loans;

// The products each country offers. Not a free-text column: a season ticket
// loan is a British commuting arrangement and a cooperative loan is a Nigerian
// one, and the register is read by product.
public enum LoanType
{
    StaffLoan,
    SalaryAdvance,
    CooperativeLoan,
    EmergencyLoan,
    SeasonTicketLoan,
    EmployeeLoan,
}

public enum LoanStatus
{
    Pending,
    Active,
    Closed,
    Defaulted,
    Rejected,
}

public enum InstalmentStatus
{
    Paid,

    // Falls due in the month the register is being read. Payroll has not run
    // yet, so this is not a missed payment.
    Due,

    Overdue,
    Upcoming,
}

// Where the money came from. Payroll is the one that matters: once payroll
// posts deductions against these loans, "who recorded this" is the difference
// between a reconciled ledger and two systems disagreeing about a balance.
public enum LoanRepaymentMethod
{
    Payroll,
    Cash,
    BankTransfer,
    Other,
}

// A staff loan or salary advance on the register.
//
// Only decisions are stored: what was asked for, when it was issued, whether
// it was refused by hand, whether it has been written off. The status, the
// outstanding balance and the repayment schedule are all derived — which is
// why there is no amount_repaid column. That figure is the sum of the
// repayment ledger, and a stored copy of it is the one number in this module
// that absolutely cannot be allowed to drift: it decides what the company
// tells somebody they still owe.
public class EmployeeLoan : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // LOAN-2026-00042. Quotable in an email, which an opaque id is not.
    public required string Reference { get; set; }

    public Guid EmployeeId { get; set; }

    public LoanType LoanType { get; set; }

    public decimal Amount { get; set; }

    // Recorded on the loan rather than read from the tenant, because a loan
    // agreed in one currency stays denominated in it even if the company's
    // reporting currency later changes.
    public required string Currency { get; set; }

    public required string Purpose { get; set; }

    public int RepaymentMonths { get; set; }

    // Null for the interest-free arrangements most of these are. Zero would
    // read the same on screen but is a different statement — somebody set it.
    public decimal? InterestRatePercent { get; set; }

    public DateOnly RequestedOn { get; set; }

    // When the money was released. Null until it is — and when an approval
    // chain is what released it, the chain's decision date answers this
    // instead, so the register never waits for somebody to write the date in.
    public DateOnly? IssuedOn { get; set; }

    // Refused without a chain: an imported loan, or one HR declined directly.
    public DateOnly? RejectedOn { get; set; }

    // Written off. A decision, so it is stored — nothing in the repayment
    // history distinguishes "stopped paying" from "paying late".
    public DateOnly? DefaultedOn { get; set; }

    public Guid? ApprovalInstanceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One repayment received.
//
// A ledger rather than a running total on the loan. Payroll will post rows
// here, and a counter two systems both increment is a counter that ends up
// wrong with nothing to reconcile against — whereas a duplicated row is
// visible and reversible.
public class LoanRepayment : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid LoanId { get; set; }

    public decimal Amount { get; set; }

    public DateOnly PaidOn { get; set; }

    public LoanRepaymentMethod Method { get; set; }

    // The payroll run, bank reference or receipt number this came from.
    public string? Reference { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum LoanRefusal
{
    None,
    AmountNotPositive,
    MonthsOutOfRange,
    PurposeRequired,
    InterestOutOfRange,

    // A cooperative loan at a UK company, or a season ticket loan at a
    // Nigerian one.
    TypeNotOfferedInThisCountry,

    // Recording a repayment against money that never left the company.
    NotIssued,

    RepaymentNotPositive,

    // More than is owed. Refused rather than clamped, because the difference
    // is money that was really received.
    RepaymentExceedsBalance,

    AlreadyClosed,

    // Writing off something that is not running.
    NotActive,
}

public sealed record Instalment
{
    public required int Number { get; init; }

    public required DateOnly DueOn { get; init; }

    public required decimal Amount { get; init; }

    // What is still owed once this instalment is paid.
    public required decimal BalanceAfter { get; init; }

    public required InstalmentStatus Status { get; init; }
}

public static class EmployeeLoans
{
    // The form's own ceiling, so a term the screen accepted is never refused
    // by the API and one it refused never arrives by another route.
    public const int LongestTermMonths = 36;

    private const int ShortestPurpose = 3;

    private static readonly LoanType[] Nigeria =
    [
        LoanType.StaffLoan,
        LoanType.SalaryAdvance,
        LoanType.CooperativeLoan,
        LoanType.EmergencyLoan,
    ];

    private static readonly LoanType[] UnitedKingdom =
    [
        LoanType.EmployeeLoan,
        LoanType.SeasonTicketLoan,
        LoanType.SalaryAdvance,
    ];

    public static IReadOnlyList<LoanType> OfferedIn(CountryCode country) =>
        country == CountryCode.UnitedKingdom ? UnitedKingdom : Nigeria;

    public static bool IsOfferedIn(LoanType type, CountryCode country) =>
        OfferedIn(country).Contains(type);

    public static string Reference(int year, int sequence) =>
        $"LOAN-{year}-{sequence:D5}";

    public static LoanRefusal Refusal(EmployeeLoan loan, CountryCode country)
    {
        if (loan.Amount <= 0m)
        {
            return LoanRefusal.AmountNotPositive;
        }

        if (loan.RepaymentMonths < 1 || loan.RepaymentMonths > LongestTermMonths)
        {
            return LoanRefusal.MonthsOutOfRange;
        }

        if ((loan.Purpose?.Trim().Length ?? 0) < ShortestPurpose)
        {
            return LoanRefusal.PurposeRequired;
        }

        if (loan.InterestRatePercent is decimal rate && (rate < 0m || rate > 100m))
        {
            return LoanRefusal.InterestOutOfRange;
        }

        // Last, so a form error reads as a form error rather than as the
        // country rule.
        return IsOfferedIn(loan.LoanType, country)
            ? LoanRefusal.None
            : LoanRefusal.TypeNotOfferedInThisCountry;
    }

    // Equal instalments, rounded to the penny. The last one absorbs the
    // remainder — see Schedule — because twelve payments of 416.67 against a
    // 5,000 loan would collect 5,000.04.
    public static decimal MonthlyRepayment(decimal amount, int months) =>
        months > 0 ? Round(amount / months) : amount;

    public static decimal Outstanding(LoanStatus status, decimal amount, decimal repaid) =>
        status is LoanStatus.Pending or LoanStatus.Rejected
            ? 0m
            : Math.Max(0m, Round(amount - repaid));

    // When the money was released: the recorded date if there is one, and
    // otherwise the day the chain approved it. Derived in that order because
    // the recorded date is what actually happened, and the chain's date is
    // what the register should say until somebody records one.
    public static DateOnly? IssuedOn(EmployeeLoan loan, DateOnly? approvedOn) =>
        loan.IssuedOn ?? approvedOn;

    // Derived rather than stored, so a loan cannot read "pending" after its
    // chain approved it, or "defaulted" after the debt was settled.
    public static LoanStatus Derive(
        EmployeeLoan loan,
        ApprovalStatus? approval,
        DateOnly? approvedOn,
        decimal repaid)
    {
        if (loan.RejectedOn is not null
            || approval is ApprovalStatus.Rejected or ApprovalStatus.Cancelled)
        {
            return LoanStatus.Rejected;
        }

        if (IssuedOn(loan, approvedOn) is null)
        {
            return LoanStatus.Pending;
        }

        // Recovering the money is the end of the matter, default or not — a
        // register that still read "defaulted" would keep somebody on a list
        // they had cleared.
        if (repaid >= loan.Amount)
        {
            return LoanStatus.Closed;
        }

        return loan.DefaultedOn is not null ? LoanStatus.Defaulted : LoanStatus.Active;
    }

    // The repayment schedule, first instalment one month after issue.
    //
    // Instalments are marked paid in order until the amount repaid runs out,
    // which is how a ledger with no instalment numbers on it still produces a
    // schedule somebody can read. Exact to the penny: the frontend carries a
    // half-penny tolerance here only because its arithmetic is floating point.
    public static IReadOnlyList<Instalment> Schedule(
        EmployeeLoan loan,
        LoanStatus status,
        decimal repaid,
        DateOnly asAt)
    {
        DateOnly? issued = loan.IssuedOn;

        if (issued is not DateOnly start
            || status is LoanStatus.Pending or LoanStatus.Rejected)
        {
            return [];
        }

        int months = Math.Max(1, loan.RepaymentMonths);
        decimal each = MonthlyRepayment(loan.Amount, months);
        decimal budget = repaid;
        decimal balance = loan.Amount;

        List<Instalment> schedule = [];

        for (int number = 1; number <= months; number++)
        {
            decimal amount = number == months ? Round(balance) : each;

            // Counted from the issue date every time rather than from the last
            // instalment, and AddMonths clamps to the end of a short month —
            // so a loan issued on 31 January falls due on 28 February and then
            // 31 March. The frontend's setUTCMonth overflows instead, pushing
            // February's instalment to 3 March, which is a date payroll misses.
            DateOnly dueOn = start.AddMonths(number);

            balance = Round(balance - amount);

            bool paid = budget >= amount;

            if (paid)
            {
                budget = Round(budget - amount);
            }

            schedule.Add(new Instalment
            {
                Number = number,
                DueOn = dueOn,
                Amount = amount,
                BalanceAfter = Math.Max(0m, balance),
                Status = paid
                    ? InstalmentStatus.Paid
                    : StatusOf(dueOn, asAt),
            });
        }

        return schedule;
    }

    // Within the month it falls due it is simply due, even once the date has
    // passed: monthly payroll has not run yet, and calling it overdue on the
    // second of the month would put the whole book in arrears.
    private static InstalmentStatus StatusOf(DateOnly dueOn, DateOnly asAt)
    {
        if (dueOn.Year == asAt.Year && dueOn.Month == asAt.Month)
        {
            return InstalmentStatus.Due;
        }

        return dueOn < asAt ? InstalmentStatus.Overdue : InstalmentStatus.Upcoming;
    }

    public static Instalment? NextInstalment(
        EmployeeLoan loan,
        LoanStatus status,
        decimal repaid,
        DateOnly asAt) =>
        Schedule(loan, status, repaid, asAt)
            .FirstOrDefault(instalment => instalment.Status != InstalmentStatus.Paid);

    public static LoanRefusal CanRecordRepayment(
        LoanStatus status,
        decimal amount,
        decimal outstanding)
    {
        if (status is LoanStatus.Pending or LoanStatus.Rejected)
        {
            return LoanRefusal.NotIssued;
        }

        if (status == LoanStatus.Closed)
        {
            return LoanRefusal.AlreadyClosed;
        }

        if (amount <= 0m)
        {
            return LoanRefusal.RepaymentNotPositive;
        }

        return amount > outstanding
            ? LoanRefusal.RepaymentExceedsBalance
            : LoanRefusal.None;
    }

    // To the penny, away from zero on a half — matching the frontend's
    // Math.round(n * 100) / 100 for the positive amounts this module deals in.
    //
    // Public because every figure that reaches a screen has to be rounded the
    // same way, and a second rounding rule in the service is how a schedule
    // and a balance end up a penny apart.
    public static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

// One loan, reduced to what the register's figures need. Passed in rather than
// queried, so the arithmetic is testable without a database and the service
// reads the roster once.
public sealed record LoanBookEntry
{
    public required LoanStatus Status { get; init; }

    public required LoanType LoanType { get; init; }

    public required decimal Amount { get; init; }

    public required decimal Repaid { get; init; }

    public DateOnly? IssuedOn { get; init; }

    public required int RepaymentMonths { get; init; }
}

public sealed record LoanBookTally
{
    public required int ActiveLoans { get; init; }

    // Principal still on the books: active and defaulted. A closed loan is
    // not money the company is owed.
    public required decimal LoanValue { get; init; }

    public required decimal Outstanding { get; init; }

    public required int DueThisMonth { get; init; }

    public required int Pending { get; init; }

    public required int Defaulted { get; init; }

    // Principal recovered across everything ever disbursed, 0–100. A hundred
    // where nothing has been lent: no lending is not a recovery failure.
    public required int RecoveryRate { get; init; }
}

public sealed record LoanTypeTally
{
    public required LoanType LoanType { get; init; }

    public required int Count { get; init; }

    public required decimal Value { get; init; }

    public required decimal Outstanding { get; init; }
}

public static class LoanBook
{
    public static LoanBookTally Of(IReadOnlyList<LoanBookEntry> entries, DateOnly asAt)
    {
        List<LoanBookEntry> live =
        [
            .. entries.Where(entry =>
                entry.Status is LoanStatus.Active or LoanStatus.Defaulted),
        ];

        // Everything the company actually paid out, closed loans included —
        // the recovery rate is about the whole history, not the open book.
        List<LoanBookEntry> disbursed =
        [
            .. entries.Where(entry =>
                entry.Status is not (LoanStatus.Pending or LoanStatus.Rejected)),
        ];

        decimal principal = disbursed.Sum(entry => entry.Amount);

        decimal recovered = disbursed.Sum(entry => Math.Min(entry.Amount, entry.Repaid));

        return new LoanBookTally
        {
            ActiveLoans = entries.Count(entry => entry.Status == LoanStatus.Active),
            LoanValue = EmployeeLoans.Round(live.Sum(entry => entry.Amount)),
            Outstanding = EmployeeLoans.Round(live.Sum(entry =>
                EmployeeLoans.Outstanding(entry.Status, entry.Amount, entry.Repaid))),
            DueThisMonth = entries.Count(entry =>
                entry.Status == LoanStatus.Active && HasInstalmentDue(entry, asAt)),
            Pending = entries.Count(entry => entry.Status == LoanStatus.Pending),
            Defaulted = entries.Count(entry => entry.Status == LoanStatus.Defaulted),
            RecoveryRate = principal == 0m
                ? 100
                : (int)Math.Round(recovered / principal * 100m, MidpointRounding.AwayFromZero),
        };
    }

    private static bool HasInstalmentDue(LoanBookEntry entry, DateOnly asAt) =>
        EmployeeLoans.Schedule(
                new EmployeeLoan
                {
                    Reference = string.Empty,
                    Currency = string.Empty,
                    Purpose = string.Empty,
                    Amount = entry.Amount,
                    RepaymentMonths = entry.RepaymentMonths,
                    IssuedOn = entry.IssuedOn,
                },
                entry.Status,
                entry.Repaid,
                asAt)
            .Any(instalment => instalment.Status == InstalmentStatus.Due);

    // Every product the company has lent under, biggest book first. Refused
    // requests are left out; pending ones are not, because a product nobody
    // has approved yet is still one people are asking for.
    public static IReadOnlyList<LoanTypeTally> ByType(IReadOnlyList<LoanBookEntry> entries) =>
    [
        .. entries
            .Where(entry => entry.Status != LoanStatus.Rejected)
            .GroupBy(entry => entry.LoanType)
            .Select(group => new LoanTypeTally
            {
                LoanType = group.Key,
                Count = group.Count(),
                Value = EmployeeLoans.Round(group.Sum(entry => entry.Amount)),
                Outstanding = EmployeeLoans.Round(group.Sum(entry =>
                    EmployeeLoans.Outstanding(entry.Status, entry.Amount, entry.Repaid))),
            })
            .OrderByDescending(row => row.Value)
            .ThenBy(row => row.LoanType),
    ];
}
