using Motee.Domain.Onboarding;

namespace Motee.Tests.Onboarding;

// UK PAYE starting tax codes. These decide what somebody is actually paid in their first
// month, so the rules are pinned individually rather than through the form that collects
// them — a derivation bug is a wrong payslip, and the employee notices before we do.
public class StarterTaxDerivationTests
{
    private static readonly DateOnly StartedJuly2026 = new(2026, 7, 1);

    private static EmployeeStatementAnswers Answers(
        bool anotherJob = false,
        bool pension = false,
        bool recentPayments = false) => new()
    {
        HasAnotherJob = anotherJob,
        ReceivesPension = pension,
        RecentPaymentsSince6April = recentPayments,
    };

    private static StarterChecklistDetails Checklist(
        StarterDeclaration declaration,
        bool studentLoan = false,
        StudentLoanPlan? plan = null,
        bool postgraduate = false) => new()
    {
        EmployeeStatement = Answers(),
        StarterDeclaration = declaration,
        StudentLoan = new StudentLoanDetails
        {
            HasPlan = studentLoan,
            Plan = plan,
            PostgraduateLoan = postgraduate,
        },
    };

    private static P45Details P45(
        DateOnly leavingDate,
        string taxCode = "1257L",
        bool week1Month1 = false,
        bool continueStudentLoan = false) => new()
    {
        LeavingDate = leavingDate,
        TaxCodeAtLeaving = taxCode,
        Week1Month1 = week1Month1,
        ContinueStudentLoan = continueStudentLoan,
    };

    // The 6 April boundary, which is the whole subtlety: five days of the calendar year
    // belong to the tax year that started the previous April, and getting it wrong makes
    // a perfectly current P45 look stale.
    [Theory]
    [InlineData(2026, 4, 6, 2026)]
    [InlineData(2026, 4, 5, 2025)]
    [InlineData(2026, 1, 1, 2025)]
    [InlineData(2026, 12, 31, 2026)]
    [InlineData(2026, 4, 7, 2026)]
    public void TheTaxYearTurnsOnTheSixthOfApril(int year, int month, int day, int expectedYear)
    {
        DateOnly start = StarterTaxDerivation.TaxYearStart(new DateOnly(year, month, day));

        Assert.Equal(new DateOnly(expectedYear, 4, 6), start);
    }

    // The current tax year plus three, ending 5 April — the year end, not the start.
    [Fact]
    public void RecordsAreKeptForTheTaxYearPlusThree()
    {
        Assert.Equal(
            new DateOnly(2030, 4, 5),
            StarterTaxDerivation.RetainUntil(StartedJuly2026));

        // A January start belongs to the previous April's year, so it expires a year
        // earlier than the date alone suggests.
        Assert.Equal(
            new DateOnly(2030, 4, 5),
            StarterTaxDerivation.RetainUntil(new DateOnly(2027, 1, 15)));
    }

    // Q8 → Q9 → Q10. Another job or a pension means the allowance is already spoken for,
    // so C regardless of the third answer.
    [Theory]
    [InlineData(false, false, false, StarterDeclaration.A)]
    [InlineData(false, false, true, StarterDeclaration.B)]
    [InlineData(true, false, false, StarterDeclaration.C)]
    [InlineData(false, true, false, StarterDeclaration.C)]
    [InlineData(true, false, true, StarterDeclaration.C)]
    [InlineData(true, true, true, StarterDeclaration.C)]
    public void TheDeclarationIsResolvedFromTheAnswers(
        bool anotherJob, bool pension, bool recent, StarterDeclaration expected) =>
        Assert.Equal(expected, StarterTaxDerivation.Resolve(Answers(anotherJob, pension, recent)));

    [Fact]
    public void StatementAGetsTheFullAllowanceCumulatively()
    {
        DerivedTax derived = StarterTaxDerivation.Derive(
            StarterTaxSource.StarterChecklist,
            StartedJuly2026,
            null,
            Checklist(StarterDeclaration.A));

        Assert.Equal("1257L", derived.TaxCode);
        Assert.Equal(TaxBasis.Cumulative, derived.Basis);
    }

    // Same allowance, but Week 1 / Month 1 — we do not know what they have already used
    // this year, so each period is taxed in isolation.
    [Fact]
    public void StatementBGetsTheAllowanceOnAWeekOneBasis()
    {
        DerivedTax derived = StarterTaxDerivation.Derive(
            StarterTaxSource.StarterChecklist,
            StartedJuly2026,
            null,
            Checklist(StarterDeclaration.B));

        Assert.Equal("1257L", derived.TaxCode);
        Assert.Equal(TaxBasis.Week1Month1, derived.Basis);
    }

    // Basic rate, and no basis at all: a flat 20% on everything has nothing to be
    // cumulative about.
    [Fact]
    public void StatementCGetsBasicRateWithNoBasis()
    {
        DerivedTax derived = StarterTaxDerivation.Derive(
            StarterTaxSource.StarterChecklist,
            StartedJuly2026,
            null,
            Checklist(StarterDeclaration.C));

        Assert.Equal("BR", derived.TaxCode);
        Assert.Null(derived.Basis);
    }

    [Fact]
    public void AStudentLoanCarriesItsPlanThrough()
    {
        DerivedTax derived = StarterTaxDerivation.Derive(
            StarterTaxSource.StarterChecklist,
            StartedJuly2026,
            null,
            Checklist(StarterDeclaration.A, studentLoan: true, plan: StudentLoanPlan.Plan5,
                postgraduate: true));

        Assert.True(derived.StudentLoanDeduction);
        Assert.Equal(StudentLoanPlan.Plan5, derived.StudentLoanPlan);
        Assert.True(derived.PostgraduateLoan);
    }

    // The accurate path: a P45 from this tax year carries the previous employer's code
    // and basis forward.
    [Fact]
    public void ACurrentYearP45CarriesItsCodeForward()
    {
        DerivedTax derived = StarterTaxDerivation.Derive(
            StarterTaxSource.P45,
            StartedJuly2026,
            P45(new DateOnly(2026, 6, 30), taxCode: "1185L", week1Month1: true,
                continueStudentLoan: true),
            null);

        Assert.Equal("1185L", derived.TaxCode);
        Assert.Equal(TaxBasis.Week1Month1, derived.Basis);
        Assert.True(derived.StudentLoanDeduction);
        Assert.Equal("p45_current_year", derived.DerivationSource);
    }

    // A P45 from a previous year says nothing useful about this one: its figures are
    // against an allowance that has since reset. Carrying the code forward would tax
    // somebody against a year that has already ended.
    [Fact]
    public void AStaleP45IsIgnoredInFavourOfTheChecklist()
    {
        DerivedTax derived = StarterTaxDerivation.Derive(
            StarterTaxSource.P45,
            StartedJuly2026,

            // Left in March, before the 6 April reset.
            P45(new DateOnly(2026, 3, 20), taxCode: "1185L"),
            Checklist(StarterDeclaration.B));

        Assert.Equal("1257L", derived.TaxCode);
        Assert.Equal(TaxBasis.Week1Month1, derived.Basis);
        Assert.Equal("p45_stale_ignored_B", derived.DerivationSource);
    }

    [Fact]
    public void AStaleP45WithNoChecklistFallsToTheEmergencyCode()
    {
        DerivedTax derived = StarterTaxDerivation.Derive(
            StarterTaxSource.P45,
            StartedJuly2026,
            P45(new DateOnly(2026, 3, 20)),
            null);

        Assert.Equal("0T", derived.TaxCode);
        Assert.Equal("p45_stale_ignored", derived.DerivationSource);
    }

    // A P45 dated exactly on the boundary is current. Off-by-one here is a wrong tax code
    // for anybody who left on the first day of the year.
    [Fact]
    public void AP45DatedOnTheSixthOfAprilIsCurrent()
    {
        DerivedTax derived = StarterTaxDerivation.Derive(
            StarterTaxSource.P45,
            StartedJuly2026,
            P45(new DateOnly(2026, 4, 6), taxCode: "1185L"),
            null);

        Assert.Equal("1185L", derived.TaxCode);
        Assert.Equal("p45_current_year", derived.DerivationSource);
    }

    // Declaring nothing over-taxes until corrected, and that is the right direction to be
    // wrong: being owed a refund is recoverable, owing HMRC at year end is not.
    [Fact]
    public void NoFormAtAllGetsTheEmergencyCode()
    {
        DerivedTax derived = StarterTaxDerivation.Derive(
            StarterTaxSource.None, StartedJuly2026, null, null);

        Assert.Equal("0T", derived.TaxCode);
        Assert.Equal(TaxBasis.Week1Month1, derived.Basis);
        Assert.False(derived.StudentLoanDeduction);
    }

    // A source claiming a branch that was not supplied must not be trusted into it.
    [Fact]
    public void ASourceWithoutItsDetailsFallsBackRatherThanThrowing()
    {
        Assert.Equal(
            "0T",
            StarterTaxDerivation.Derive(StarterTaxSource.P45, StartedJuly2026, null, null).TaxCode);

        Assert.Equal(
            "0T",
            StarterTaxDerivation.Derive(
                StarterTaxSource.StarterChecklist, StartedJuly2026, null, null).TaxCode);
    }

    // The PAYE reference is two boxes on the form and one string everywhere else.
    [Fact]
    public void ThePayeReferenceComposesItsTwoBoxes()
    {
        P45Details p45 = P45(new DateOnly(2026, 6, 30)) with
        {
            PayeOfficeNumber = "120",
            PayeReferenceNumber = "AB456",
        };

        Assert.Equal("120/AB456", p45.PayeReference);

        // Half a reference is not a reference.
        Assert.Equal(string.Empty, (p45 with { PayeReferenceNumber = null }).PayeReference);
    }
}
