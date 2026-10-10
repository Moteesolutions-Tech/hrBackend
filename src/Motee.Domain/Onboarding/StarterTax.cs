namespace Motee.Domain.Onboarding;

// How a UK joiner's tax position was declared.
//
// P45 and Starter Checklist are mutually exclusive: a joiner has one or the other, and
// sometimes neither. Modelled as a discriminator with two nullable branches rather than
// two independent records, because "both" is not a state that exists and a shape that
// allows it will eventually contain it.
public enum StarterTaxSource
{
    // Neither form. Attracts the emergency code until corrected, which is deliberate:
    // over-taxing is recoverable, under-taxing leaves the employee owing money.
    None,

    // Issued by the previous employer. Document-derived — an upload plus transcribed
    // figures, and the figures are what matters.
    P45,

    // The self-completed form, formerly P46. Form-native: structured answers, no
    // attachment.
    StarterChecklist,
}

// The formal RTI Starter Declaration. Not a free choice — it is resolved from the
// joiner's answers to the official questions, because somebody asked to pick A, B or C
// directly will pick the wrong one.
public enum StarterDeclaration
{
    A,
    B,
    C,
}

public enum StudentLoanPlan
{
    Plan1,
    Plan2,

    // Scotland.
    Plan4,

    Plan5,
}

public enum TaxBasis
{
    Cumulative,

    // Each pay period taxed in isolation, ignoring the year to date.
    Week1Month1,
}

// The joiner's answers to the Starter Checklist's employee-statement questions.
//
// Stored as the answers rather than only the resolved declaration, so a disputed tax code
// can be traced back to what the person actually said. The declaration is derivable from
// these; these are not derivable from it.
public sealed record EmployeeStatementAnswers
{
    // Q8 — Do you have another job?
    public bool HasAnotherJob { get; init; }

    // Q9 — Do you receive a State, workplace or private pension?
    public bool ReceivesPension { get; init; }

    // Q10 — Since 6 April, payments from another job that has ended, or JSA / ESA /
    // Incapacity Benefit?
    public bool RecentPaymentsSince6April { get; init; }
}

public sealed record StudentLoanDetails
{
    // Q11 yes AND Q12 no — a deduction is genuinely due. Two questions collapse into one
    // fact here because the pair only means anything together.
    public bool HasPlan { get; init; }

    public StudentLoanPlan? Plan { get; init; }

    public bool PostgraduateLoan { get; init; }
}

public sealed record StarterChecklistDetails
{
    public required EmployeeStatementAnswers EmployeeStatement { get; init; }

    // Resolved from the answers above, stored because it is the field RTI submits.
    public required StarterDeclaration StarterDeclaration { get; init; }

    public required StudentLoanDetails StudentLoan { get; init; }
}

// Figures transcribed from the P45. Box numbers refer to the official P45 (Continuous)
// form, parts 2 and 3.
public sealed record P45Details
{
    // The uploaded scan, if there is one. Optional: the structured figures are what
    // payroll uses, and a joiner reading them off a paper copy is ordinary.
    public Guid? DocumentFileId { get; init; }

    // Box 1, left and right — the employer PAYE reference, e.g. "120" and "AB456".
    // Kept as two fields because that is how the form is laid out and how somebody
    // copying it will type it.
    public string? PayeOfficeNumber { get; init; }

    public string? PayeReferenceNumber { get; init; }

    // Box 2.
    public string? NiNumber { get; init; }

    // Box 4.
    public required DateOnly LeavingDate { get; init; }

    // Box 5.
    public bool ContinueStudentLoan { get; init; }

    // Box 6.
    public required string TaxCodeAtLeaving { get; init; }

    // Box 6 — the 'X' indicator.
    public bool Week1Month1 { get; init; }

    // Box 7.
    public int? WeekNumber { get; init; }

    public int? MonthNumber { get; init; }

    public decimal TotalPayToDate { get; init; }

    public decimal TotalTaxToDate { get; init; }

    // "120/AB456", for display. Composed rather than stored separately so the two cannot
    // drift from their combined form.
    public string PayeReference =>
        string.IsNullOrWhiteSpace(PayeOfficeNumber) || string.IsNullOrWhiteSpace(PayeReferenceNumber)
            ? string.Empty
            : $"{PayeOfficeNumber}/{PayeReferenceNumber}";
}

// The starting tax position, worked out from whichever form was filed.
//
// Derived rather than entered. A joiner does not know their tax code and an HR admin
// should not be guessing it, so the one thing payroll reads is computed from the
// declaration — and DerivationSource records which rule produced it, because "why am I
// on 0T" is a question somebody will ask on their first payslip.
public sealed record DerivedTax
{
    public required string TaxCode { get; init; }

    // Null when the basis does not apply — BR is a flat rate on all pay.
    public TaxBasis? Basis { get; init; }

    public bool StudentLoanDeduction { get; init; }

    public StudentLoanPlan? StudentLoanPlan { get; init; }

    public bool PostgraduateLoan { get; init; }

    public required string DerivationSource { get; init; }
}
