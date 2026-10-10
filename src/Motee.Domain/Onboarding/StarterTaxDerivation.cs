namespace Motee.Domain.Onboarding;

// Works out a UK joiner's starting tax code from what they declared.
//
// Pure arithmetic over the record, with no clock of its own: every date it needs comes
// from the record, so the same inputs give the same code whenever it is asked. A payslip
// that cannot be re-derived a year later is one nobody can explain.
public static class StarterTaxDerivation
{
    // The standard personal allowance code. A figure that changes with each Budget, and
    // the one thing here that will need revisiting annually — named so it is findable
    // rather than buried in three branches.
    public const string StandardTaxCode = "1257L";

    // Flat 20% on everything, for somebody whose allowance is already used by another job.
    public const string BasicRateCode = "BR";

    // No allowance at all. The deliberate over-tax when nothing was declared: being owed
    // a refund is recoverable, owing HMRC money at year end is not.
    public const string NoAllowanceCode = "0T";

    // The 6 April boundary. A date on or after 6 April belongs to the year starting that
    // April; anything earlier belongs to the previous one — which is the whole subtlety,
    // and getting it wrong makes a current P45 look stale for five days a year.
    public static DateOnly TaxYearStart(DateOnly reference)
    {
        bool onOrAfterApril6 = reference.Month > 4
            || (reference.Month == 4 && reference.Day >= 6);

        return new DateOnly(onOrAfterApril6 ? reference.Year : reference.Year - 1, 4, 6);
    }

    // Kept for the current tax year plus three, which is HMRC's retention period. Returns
    // 5 April — the year end, not the year start.
    public static DateOnly RetainUntil(DateOnly employmentStartDate) =>
        new(TaxYearStart(employmentStartDate).Year + 1 + 3, 4, 5);

    // The official decision flow, Q8 → Q9 → Q10. Another job or a pension means the
    // allowance is already spoken for, so C regardless of anything else.
    public static StarterDeclaration Resolve(EmployeeStatementAnswers answers) =>
        answers.HasAnotherJob || answers.ReceivesPension
            ? StarterDeclaration.C
            : answers.RecentPaymentsSince6April
                ? StarterDeclaration.B
                : StarterDeclaration.A;

    public static DerivedTax Derive(
        StarterTaxSource source,
        DateOnly employmentStartDate,
        P45Details? p45,
        StarterChecklistDetails? checklist)
    {
        if (source == StarterTaxSource.StarterChecklist && checklist is not null)
        {
            return FromChecklist(checklist, "starter_declaration");
        }

        if (source == StarterTaxSource.P45 && p45 is not null)
        {
            // A P45 from a previous tax year says nothing useful about this one: the
            // figures are against an allowance that has since reset. Carrying the code
            // forward would tax somebody on a year that has already ended.
            if (p45.LeavingDate < TaxYearStart(employmentStartDate))
            {
                return checklist is not null
                    ? FromChecklist(checklist, "p45_stale_ignored")
                    : NoForm("p45_stale_ignored");
            }

            return new DerivedTax
            {
                TaxCode = p45.TaxCodeAtLeaving,
                Basis = p45.Week1Month1 ? TaxBasis.Week1Month1 : TaxBasis.Cumulative,
                StudentLoanDeduction = p45.ContinueStudentLoan,
                DerivationSource = "p45_current_year",
            };
        }

        return NoForm("no_form_default_0T");
    }

    private static DerivedTax FromChecklist(StarterChecklistDetails checklist, string reason)
    {
        StudentLoanDetails loan = checklist.StudentLoan;

        return checklist.StarterDeclaration switch
        {
            // First job this year, no benefits since April: the full allowance, cumulative.
            StarterDeclaration.A => new DerivedTax
            {
                TaxCode = StandardTaxCode,
                Basis = TaxBasis.Cumulative,
                StudentLoanDeduction = loan.HasPlan,
                StudentLoanPlan = loan.Plan,
                PostgraduateLoan = loan.PostgraduateLoan,
                DerivationSource = $"{reason}_A",
            },

            // Had another job or claimed benefits, but no P45. Same allowance, but on a
            // Week 1 / Month 1 basis because we do not know what they have already used.
            StarterDeclaration.B => new DerivedTax
            {
                TaxCode = StandardTaxCode,
                Basis = TaxBasis.Week1Month1,
                StudentLoanDeduction = loan.HasPlan,
                StudentLoanPlan = loan.Plan,
                PostgraduateLoan = loan.PostgraduateLoan,
                DerivationSource = $"{reason}_B",
            },

            // Another job or a pension: the allowance is used there, so everything here
            // is taxed at basic rate. Basis is null because a flat rate has none.
            _ => new DerivedTax
            {
                TaxCode = BasicRateCode,
                Basis = null,
                StudentLoanDeduction = loan.HasPlan,
                StudentLoanPlan = loan.Plan,
                PostgraduateLoan = loan.PostgraduateLoan,
                DerivationSource = $"{reason}_C",
            },
        };
    }

    private static DerivedTax NoForm(string reason) => new()
    {
        TaxCode = NoAllowanceCode,
        Basis = TaxBasis.Week1Month1,
        StudentLoanDeduction = false,
        DerivationSource = reason,
    };
}
