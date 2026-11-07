using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Domain.Approvals;
using Motee.Domain.Common;
using Motee.Domain.Loans;

namespace Motee.Tests.Loans;

// The loan register's arithmetic.
//
// The frontend ships its own vitest suite for this (src/lib/loans/loans.test.ts),
// with worked examples for the schedule, the balances and the dashboard
// figures. Those cases are ported here verbatim, because they are the client's
// stated expectations and the two have to agree on the penny — a schedule that
// disagrees with the one the employee signed is not a rounding difference, it
// is a dispute.
public class LoanTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // Their fixture: £5,000 over 12 months, issued 1 January 2026.
    private static readonly DateOnly Today = new(2026, 9, 24);

    private static readonly DateOnly Issued = new(2026, 1, 1);

    private static EmployeeLoan Loan(
        decimal amount = 5000m,
        int months = 12,
        DateOnly? issuedOn = null,
        DateOnly? defaultedOn = null,
        DateOnly? rejectedOn = null,
        LoanType type = LoanType.StaffLoan,
        string purpose = "Rent renewal",
        decimal? interest = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = Guid.NewGuid(),
            Reference = "LOAN-2026-00001",
            LoanType = type,
            Amount = amount,
            Currency = "GBP",
            Purpose = purpose,
            RepaymentMonths = months,
            InterestRatePercent = interest,
            RequestedOn = Issued,
            IssuedOn = issuedOn ?? Issued,
            DefaultedOn = defaultedOn,
            RejectedOn = rejectedOn,
        };

    // Money not yet released. Its own factory because the optional parameter
    // above cannot express it: null is how a caller asks for the default date.
    private static EmployeeLoan Unissued(DateOnly? rejectedOn = null)
    {
        EmployeeLoan loan = Loan(rejectedOn: rejectedOn);

        loan.IssuedOn = null;

        return loan;
    }

    // ── the wire shape ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(LoanStatus.Pending, "\"pending\"")]
    [InlineData(LoanStatus.Defaulted, "\"defaulted\"")]
    [InlineData(LoanType.SalaryAdvance, "\"salaryAdvance\"")]
    [InlineData(LoanType.SeasonTicketLoan, "\"seasonTicketLoan\"")]
    [InlineData(InstalmentStatus.Overdue, "\"overdue\"")]
    [InlineData(LoanRepaymentMethod.BankTransfer, "\"bankTransfer\"")]
    public void SerialisesAsCamelCase(object value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Web));
    }

    // ── what each country offers ────────────────────────────────────────────

    [Fact]
    public void NigeriaOffersTheProductsItsFormLists()
    {
        Assert.Equal(
            [
                LoanType.StaffLoan,
                LoanType.SalaryAdvance,
                LoanType.CooperativeLoan,
                LoanType.EmergencyLoan,
            ],
            EmployeeLoans.OfferedIn(CountryCode.Nigeria));
    }

    [Fact]
    public void TheUnitedKingdomOffersItsOwn()
    {
        Assert.Equal(
            [LoanType.EmployeeLoan, LoanType.SeasonTicketLoan, LoanType.SalaryAdvance],
            EmployeeLoans.OfferedIn(CountryCode.UnitedKingdom));
    }

    // A season ticket loan is a British commuting arrangement and a
    // cooperative loan is a Nigerian one. The frontend enforces this only by
    // not offering the option, which is no enforcement at all.
    [Theory]
    [InlineData(LoanType.CooperativeLoan, "GB")]
    [InlineData(LoanType.EmergencyLoan, "GB")]
    [InlineData(LoanType.SeasonTicketLoan, "NG")]
    [InlineData(LoanType.EmployeeLoan, "NG")]
    public void RefusesALoanProductTheCountryDoesNotOffer(LoanType type, string country)
    {
        Assert.Equal(
            LoanRefusal.TypeNotOfferedInThisCountry,
            EmployeeLoans.Refusal(Loan(type: type), CountryCode.Parse(country)));
    }

    // Both are wrong, and the one somebody can fix on the form they are
    // looking at is the one worth saying first. "That product is not offered
    // here" is not useful feedback on an amount of zero.
    [Fact]
    public void AFormErrorIsReportedBeforeTheCountryRule()
    {
        Assert.Equal(
            LoanRefusal.AmountNotPositive,
            EmployeeLoans.Refusal(
                Loan(amount: 0m, type: LoanType.CooperativeLoan),
                CountryCode.UnitedKingdom));
    }

    [Fact]
    public void SalaryAdvanceIsOfferedInBoth()
    {
        Assert.All(
            (CountryCode[])[CountryCode.Nigeria, CountryCode.UnitedKingdom],
            country => Assert.Equal(
                LoanRefusal.None,
                EmployeeLoans.Refusal(Loan(type: LoanType.SalaryAdvance), country)));
    }

    // ── what the form will not accept ───────────────────────────────────────
    //
    // All against Nigeria, where the default fixture's staff loan is a product
    // on offer — so each of these asserts the one field it names and nothing
    // about the country rule.

    [Theory]
    [InlineData(0d)]
    [InlineData(-100d)]
    public void RefusesAnAmountThatIsNotMoney(double amount)
    {
        Assert.Equal(
            LoanRefusal.AmountNotPositive,
            EmployeeLoans.Refusal(Loan(amount: (decimal)amount), CountryCode.Nigeria));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(37)]
    public void RefusesATermOutsideTheFormsRange(int months)
    {
        Assert.Equal(
            LoanRefusal.MonthsOutOfRange,
            EmployeeLoans.Refusal(Loan(months: months), CountryCode.Nigeria));
    }

    // A salary advance, which both countries offer, so this asserts the term
    // and nothing else. The default fixture is a staff loan — a Nigerian
    // product — and using it here would quietly make this a country test.
    [Theory]
    [InlineData("NG")]
    [InlineData("GB")]
    public void AcceptsTheLongestTermTheFormOffers(string country)
    {
        Assert.Equal(
            LoanRefusal.None,
            EmployeeLoans.Refusal(
                Loan(months: EmployeeLoans.LongestTermMonths, type: LoanType.SalaryAdvance),
                CountryCode.Parse(country)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ab")]
    public void RefusesALoanWithNoStatedPurpose(string purpose)
    {
        Assert.Equal(
            LoanRefusal.PurposeRequired,
            EmployeeLoans.Refusal(Loan(purpose: purpose), CountryCode.Nigeria));
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(101d)]
    public void RefusesAnInterestRateThatIsNotAPercentage(double rate)
    {
        Assert.Equal(
            LoanRefusal.InterestOutOfRange,
            EmployeeLoans.Refusal(Loan(interest: (decimal)rate), CountryCode.Nigeria));
    }

    // ── the instalment ──────────────────────────────────────────────────────

    [Fact]
    public void SplitsTheLoanIntoEqualInstalments()
    {
        Assert.Equal(416.67m, EmployeeLoans.MonthlyRepayment(5000m, 12));
    }

    [Fact]
    public void AMonthlessLoanIsRepayableInOne()
    {
        Assert.Equal(5000m, EmployeeLoans.MonthlyRepayment(5000m, 0));
    }

    // ── the schedule, from their own test ───────────────────────────────────

    [Fact]
    public void SchedulesEveryInstalmentWithTheLastAbsorbingTheRounding()
    {
        IReadOnlyList<Instalment> schedule =
            EmployeeLoans.Schedule(Loan(), LoanStatus.Active, 0m, Today);

        Assert.Equal(12, schedule.Count);
        Assert.Equal(new DateOnly(2026, 2, 1), schedule[0].DueOn);
        Assert.Equal(416.67m, schedule[0].Amount);

        // The point of the last instalment absorbing the remainder: twelve
        // payments of 416.67 would collect 5,000.04.
        Assert.Equal(5000m, schedule.Sum(instalment => instalment.Amount));
        Assert.Equal(416.63m, schedule[11].Amount);
        Assert.Equal(0m, schedule[11].BalanceAfter);
    }

    [Fact]
    public void MarksPaidInstalmentsInOrderThenOverdueThenTheMonthsOwn()
    {
        // Seven instalments (February to August) have fallen; five are paid.
        IReadOnlyList<Instalment> schedule =
            EmployeeLoans.Schedule(Loan(), LoanStatus.Active, 416.67m * 5, Today);

        Assert.All(schedule.Take(5), instalment =>
            Assert.Equal(InstalmentStatus.Paid, instalment.Status));

        Assert.Equal(new DateOnly(2026, 7, 1), schedule[5].DueOn);
        Assert.Equal(InstalmentStatus.Overdue, schedule[5].Status);
        Assert.Equal(InstalmentStatus.Overdue, schedule[6].Status);

        // Dated the first of this month and already past, but still the
        // instalment this month's payroll collects — not a missed one.
        Assert.Equal(new DateOnly(2026, 9, 1), schedule[7].DueOn);
        Assert.Equal(InstalmentStatus.Due, schedule[7].Status);

        Assert.Equal(InstalmentStatus.Upcoming, schedule[8].Status);
    }

    [Fact]
    public void HasNoScheduleUntilTheLoanIsIssued()
    {
        Assert.Empty(EmployeeLoans.Schedule(Loan(), LoanStatus.Pending, 0m, Today));
        Assert.Empty(EmployeeLoans.Schedule(Loan(), LoanStatus.Rejected, 0m, Today));
    }

    // A penny short is a penny outstanding. The frontend carries a half-penny
    // tolerance here purely because its arithmetic is floating point; this
    // works in decimal and needs none, so the comparison is exact.
    [Fact]
    public void APennyShortLeavesTheInstalmentUnpaid()
    {
        IReadOnlyList<Instalment> schedule =
            EmployeeLoans.Schedule(Loan(), LoanStatus.Active, 416.66m, Today);

        Assert.NotEqual(InstalmentStatus.Paid, schedule[0].Status);
    }

    // The frontend's setUTCMonth overflows: a loan issued on 31 January gets
    // its "February" instalment on 3 March. A repayment date that skips a
    // month is a date the payroll run will miss.
    [Fact]
    public void AnInstalmentFallsOnTheLastDayOfAShortMonthRatherThanOverflowing()
    {
        IReadOnlyList<Instalment> schedule = EmployeeLoans.Schedule(
            Loan(issuedOn: new DateOnly(2026, 1, 31)),
            LoanStatus.Active,
            0m,
            Today);

        Assert.Equal(new DateOnly(2026, 2, 28), schedule[0].DueOn);
        Assert.Equal(new DateOnly(2026, 3, 31), schedule[1].DueOn);
    }

    [Fact]
    public void TheNextInstalmentIsTheFirstOneUnpaid()
    {
        Instalment? next = EmployeeLoans.NextInstalment(Loan(), LoanStatus.Active, 416.67m * 5, Today);

        Assert.NotNull(next);
        Assert.Equal(6, next.Number);
    }

    [Fact]
    public void ThereIsNoNextInstalmentOnceEverythingIsPaid()
    {
        Assert.Null(EmployeeLoans.NextInstalment(Loan(), LoanStatus.Closed, 5000m, Today));
    }

    // ── the balance ─────────────────────────────────────────────────────────

    [Fact]
    public void TheBalanceIsPrincipalLessWhatHasBeenRepaid()
    {
        Assert.Equal(4000m, EmployeeLoans.Outstanding(LoanStatus.Active, 5000m, 1000m));
    }

    // Money never left the company, so there is nothing owed to it.
    [Theory]
    [InlineData(LoanStatus.Pending)]
    [InlineData(LoanStatus.Rejected)]
    public void NothingIsOwedOnALoanThatWasNeverDisbursed(LoanStatus status)
    {
        Assert.Equal(0m, EmployeeLoans.Outstanding(status, 5000m, 0m));
    }

    [Fact]
    public void TheBalanceNeverGoesBelowZero()
    {
        Assert.Equal(0m, EmployeeLoans.Outstanding(LoanStatus.Active, 5000m, 5200m));
    }

    // ── the status, derived from the chain ──────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(ApprovalStatus.Draft)]
    [InlineData(ApprovalStatus.InProgress)]
    [InlineData(ApprovalStatus.Returned)]
    public void ALoanIsPendingUntilSomethingDecidesIt(ApprovalStatus? approval)
    {
        Assert.Equal(
            LoanStatus.Pending,
            EmployeeLoans.Derive(Unissued(), approval, null, 0m));
    }

    [Theory]
    [InlineData(ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.Cancelled)]
    public void AChainThatRefusesTheMoneyRejectsTheLoan(ApprovalStatus approval)
    {
        Assert.Equal(
            LoanStatus.Rejected,
            EmployeeLoans.Derive(Unissued(), approval, null, 0m));
    }

    // The approval date is the issue date: the money is released because the
    // chain said so, so reading the status must not wait for somebody to come
    // back and write the date down.
    [Fact]
    public void AnApprovedChainIssuesTheLoanOnTheDayItWasApproved()
    {
        DateOnly approved = new(2026, 9, 3);

        Assert.Equal(
            LoanStatus.Active,
            EmployeeLoans.Derive(Unissued(), ApprovalStatus.Approved, approved, 0m));

        Assert.Equal(approved, EmployeeLoans.IssuedOn(Unissued(), approved));
    }

    // An imported loan, or one HR decided without a chain. The recorded date
    // wins: it is what actually happened.
    [Fact]
    public void ARecordedIssueDateBeatsTheChains()
    {
        DateOnly recorded = new(2026, 1, 1);

        Assert.Equal(recorded, EmployeeLoans.IssuedOn(Loan(issuedOn: recorded), new DateOnly(2026, 9, 3)));
        Assert.Equal(LoanStatus.Active, EmployeeLoans.Derive(Loan(issuedOn: recorded), null, null, 0m));
    }

    [Fact]
    public void ALoanRepaidInFullIsClosed()
    {
        Assert.Equal(LoanStatus.Closed, EmployeeLoans.Derive(Loan(), null, null, 5000m));
    }

    [Fact]
    public void ALoanMarkedDefaultedIsDefaulted()
    {
        Assert.Equal(
            LoanStatus.Defaulted,
            EmployeeLoans.Derive(Loan(defaultedOn: new DateOnly(2026, 8, 1)), null, null, 1000m));
    }

    // Recovering the money is the end of the matter. A register that still
    // read "defaulted" after the debt was settled would keep somebody on a
    // list they had cleared.
    [Fact]
    public void ADefaultedLoanRepaidInFullIsClosed()
    {
        Assert.Equal(
            LoanStatus.Closed,
            EmployeeLoans.Derive(Loan(defaultedOn: new DateOnly(2026, 8, 1)), null, null, 5000m));
    }

    [Fact]
    public void ALoanRefusedByHandIsRejected()
    {
        Assert.Equal(
            LoanStatus.Rejected,
            EmployeeLoans.Derive(
                Unissued(rejectedOn: new DateOnly(2026, 2, 1)),
                ApprovalStatus.InProgress,
                null,
                0m));
    }

    // ── recording a repayment ───────────────────────────────────────────────

    [Theory]
    [InlineData(LoanStatus.Pending)]
    [InlineData(LoanStatus.Rejected)]
    public void NothingCanBeRepaidAgainstALoanThatWasNeverDisbursed(LoanStatus status)
    {
        Assert.Equal(
            LoanRefusal.NotIssued,
            EmployeeLoans.CanRecordRepayment(status, 100m, 0m));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-50d)]
    public void ARepaymentHasToBeAnAmount(double amount)
    {
        Assert.Equal(
            LoanRefusal.RepaymentNotPositive,
            EmployeeLoans.CanRecordRepayment(LoanStatus.Active, (decimal)amount, 500m));
    }

    // The frontend clamps an overpayment to the balance, which quietly loses
    // the difference — money that was genuinely received and is no longer
    // recorded anywhere. Refusing makes somebody decide what the extra was.
    [Fact]
    public void ARepaymentLargerThanTheBalanceIsRefusedRatherThanClamped()
    {
        Assert.Equal(
            LoanRefusal.RepaymentExceedsBalance,
            EmployeeLoans.CanRecordRepayment(LoanStatus.Active, 500.01m, 500m));
    }

    [Fact]
    public void ARepaymentThatSettlesTheBalanceExactlyIsAccepted()
    {
        Assert.Equal(
            LoanRefusal.None,
            EmployeeLoans.CanRecordRepayment(LoanStatus.Active, 500m, 500m));
    }

    [Fact]
    public void ADefaultedLoanStillAcceptsRepayments()
    {
        Assert.Equal(
            LoanRefusal.None,
            EmployeeLoans.CanRecordRepayment(LoanStatus.Defaulted, 100m, 500m));
    }

    [Fact]
    public void AClosedLoanHasNothingLeftToRepay()
    {
        Assert.Equal(
            LoanRefusal.AlreadyClosed,
            EmployeeLoans.CanRecordRepayment(LoanStatus.Closed, 100m, 0m));
    }

    // ── the register's figures, from their own test ─────────────────────────

    private static LoanBookEntry Entry(
        LoanStatus status,
        decimal amount,
        decimal repaid,
        LoanType type = LoanType.StaffLoan,
        DateOnly? issuedOn = null,
        int months = 12) =>
        new()
        {
            Status = status,
            Amount = amount,
            Repaid = repaid,
            LoanType = type,
            IssuedOn = issuedOn ?? new DateOnly(2026, 1, 1),
            RepaymentMonths = months,
        };

    // Their fixture exactly: five loans across every status.
    private static readonly LoanBookEntry[] Book =
    [
        Entry(LoanStatus.Active, 1200m, 700m),
        Entry(LoanStatus.Defaulted, 1000m, 200m),
        Entry(LoanStatus.Closed, 500m, 500m, LoanType.SalaryAdvance),
        Entry(LoanStatus.Pending, 800m, 0m, LoanType.SalaryAdvance),
        Entry(LoanStatus.Rejected, 900m, 0m),
    ];

    [Fact]
    public void ReportsTheRegistersHeadlineFigures()
    {
        LoanBookTally tally = LoanBook.Of(Book, Today);

        Assert.Equal(1, tally.ActiveLoans);

        // Principal still on the books: active and defaulted only.
        Assert.Equal(2200m, tally.LoanValue);
        Assert.Equal(1300m, tally.Outstanding);

        // The active loan has paid February to August, so September's
        // instalment is the one due now.
        Assert.Equal(1, tally.DueThisMonth);
        Assert.Equal(1, tally.Pending);
        Assert.Equal(1, tally.Defaulted);

        // 1,400 recovered of 2,700 ever disbursed.
        Assert.Equal(52, tally.RecoveryRate);
    }

    // Nothing lent is not a recovery failure.
    [Fact]
    public void RecoveryIsCompleteWhenNothingHasBeenLent()
    {
        LoanBookTally tally = LoanBook.Of(
            [Entry(LoanStatus.Pending, 800m, 0m)],
            Today);

        Assert.Equal(100, tally.RecoveryRate);
        Assert.Equal(0m, tally.LoanValue);
    }

    [Fact]
    public void GroupsTheBookByProductLeavingRefusedRequestsOut()
    {
        IReadOnlyList<LoanTypeTally> byType = LoanBook.ByType(Book);

        Assert.Equal(
            [
                new LoanTypeTally
                {
                    LoanType = LoanType.StaffLoan,
                    Count = 2,
                    Value = 2200m,
                    Outstanding = 1300m,
                },
                new LoanTypeTally
                {
                    LoanType = LoanType.SalaryAdvance,
                    Count = 2,
                    Value = 1300m,
                    Outstanding = 0m,
                },
            ],
            byType);
    }

    // ── the reference ───────────────────────────────────────────────────────

    [Fact]
    public void ReferencesReadAsSomethingSomebodyCanQuote()
    {
        Assert.Equal("LOAN-2026-00042", EmployeeLoans.Reference(2026, 42));
    }
}
