using Motee.Domain.Eor;

namespace Motee.Tests.Eor;

// Provider invoices, and whether what we were billed matches what we engaged.
//
// That comparison is the reason to hold invoices at all. The screen renders
// them as a read-only list checked against nothing, so a provider billing for
// nine workers when seven are engaged reads as a normal month — and an invoice
// sits at "Pending" past its due date until somebody opens the row.
//
// Written before the rules.
public class EorBillingTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    private static EorInvoice Invoice(
        decimal gross = 50_000m,
        decimal employerCosts = 9_000m,
        decimal fee = 5_900m,
        decimal? total = null,
        DateOnly? due = null,
        DateOnly? paid = null,
        int billed = 7,
        DateOnly? period = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            Period = period ?? new DateOnly(2026, 5, 1),
            Currency = "USD",
            BilledWorkerCount = billed,
            GrossPay = gross,
            EmployerCosts = employerCosts,
            ManagementFee = fee,
            Total = total ?? gross + employerCosts + fee,
            IssuedOn = new DateOnly(2026, 6, 1),
            DueOn = due ?? new DateOnly(2026, 6, 30),
            PaidOn = paid,
        };

    // ── where an invoice stands ────────────────────────────────────────────

    [Fact]
    public void AnUnpaidInvoiceInsideItsTermsIsPending()
    {
        Assert.Equal(InvoiceStanding.Pending, Invoices.StandingOf(Invoice(), Today));
    }

    [Fact]
    public void APaidInvoiceIsPaid()
    {
        Assert.Equal(
            InvoiceStanding.Paid,
            Invoices.StandingOf(Invoice(paid: new DateOnly(2026, 6, 10)), Today));
    }

    // An overdue invoice that reads as pending is one nobody chases.
    [Fact]
    public void AnInvoicePastItsDueDateIsOverdue()
    {
        Assert.Equal(
            InvoiceStanding.Overdue,
            Invoices.StandingOf(Invoice(due: Today.AddDays(-1)), Today));
    }

    [Fact]
    public void AnInvoicePaidLateIsStillPaid()
    {
        Assert.Equal(
            InvoiceStanding.Paid,
            Invoices.StandingOf(
                Invoice(due: Today.AddDays(-30), paid: Today.AddDays(-2)), Today));
    }

    [Fact]
    public void AnInvoiceDueTodayIsNotYetOverdue()
    {
        Assert.Equal(
            InvoiceStanding.Pending, Invoices.StandingOf(Invoice(due: Today), Today));
    }

    [Fact]
    public void HowLateAnInvoiceIsIsCounted()
    {
        Assert.Equal(14, Invoices.DaysOverdue(Invoice(due: Today.AddDays(-14)), Today));
    }

    [Fact]
    public void AnInvoiceThatIsNotLateIsNotLateByAnyDays()
    {
        Assert.Equal(0, Invoices.DaysOverdue(Invoice(), Today));
        Assert.Equal(
            0, Invoices.DaysOverdue(Invoice(due: Today.AddDays(-30), paid: Today), Today));
    }

    // ── does it add up ─────────────────────────────────────────────────────

    [Fact]
    public void AnInvoiceWhosePartsMakeTheTotalAddsUp()
    {
        Assert.True(Invoices.TotalAddsUp(Invoice()));
    }

    [Fact]
    public void AnInvoiceWhoseTotalDisagreesIsFlagged()
    {
        Assert.False(Invoices.TotalAddsUp(Invoice(total: 70_000m)));
    }

    // Providers total in their own systems and a few minor units either way is
    // arithmetic, not a dispute.
    [Fact]
    public void RoundingIsNotADispute()
    {
        Assert.True(Invoices.TotalAddsUp(
            Invoice(gross: 50_000m, employerCosts: 9_000m, fee: 5_900m, total: 64_900.03m)));
    }

    // The provider's figure is kept rather than replaced with ours: a credit
    // note or a one-off charge shows up here, and silently overwriting it would
    // hide a dispute rather than raise it.
    [Fact]
    public void TheProvidersTotalIsKeptAlongsideOurSum()
    {
        EorInvoice invoice = Invoice(total: 70_000m);

        Assert.Equal(64_900m, Invoices.SumOfParts(invoice));
        Assert.Equal(70_000m, invoice.Total);
    }

    // ── billed against engaged ─────────────────────────────────────────────

    [Fact]
    public void TheWorkersEngagedThatMonthAreCounted()
    {
        Guid providerId = Guid.NewGuid();

        List<EorEngagement> engagements =
        [
            Engagement(providerId, new DateOnly(2026, 1, 1)),
            Engagement(providerId, new DateOnly(2026, 1, 1)),
        ];

        Assert.Equal(
            2, Invoices.EngagedDuring(engagements, providerId, new DateOnly(2026, 5, 1)));
    }

    // Any point in the month, not the whole of it: somebody who started on the
    // 20th is on that month's invoice for the part-month, and leaving them out
    // would make every joining month look over-billed.
    [Fact]
    public void SomebodyWhoStartedMidMonthCounts()
    {
        Guid providerId = Guid.NewGuid();

        List<EorEngagement> engagements = [Engagement(providerId, new DateOnly(2026, 5, 20))];

        Assert.Equal(
            1, Invoices.EngagedDuring(engagements, providerId, new DateOnly(2026, 5, 1)));
    }

    [Fact]
    public void SomebodyWhoLeftMidMonthCounts()
    {
        Guid providerId = Guid.NewGuid();

        List<EorEngagement> engagements =
        [
            Engagement(providerId, new DateOnly(2026, 1, 1), new DateOnly(2026, 5, 9)),
        ];

        Assert.Equal(
            1, Invoices.EngagedDuring(engagements, providerId, new DateOnly(2026, 5, 1)));
    }

    [Fact]
    public void SomebodyWhoLeftBeforeTheMonthDoesNotCount()
    {
        Guid providerId = Guid.NewGuid();

        List<EorEngagement> engagements =
        [
            Engagement(providerId, new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30)),
        ];

        Assert.Equal(
            0, Invoices.EngagedDuring(engagements, providerId, new DateOnly(2026, 5, 1)));
    }

    [Fact]
    public void SomebodyWhoStartsAfterTheMonthDoesNotCount()
    {
        Guid providerId = Guid.NewGuid();

        List<EorEngagement> engagements = [Engagement(providerId, new DateOnly(2026, 6, 1))];

        Assert.Equal(
            0, Invoices.EngagedDuring(engagements, providerId, new DateOnly(2026, 5, 1)));
    }

    [Fact]
    public void AnotherProvidersWorkersAreNotCounted()
    {
        Guid providerId = Guid.NewGuid();

        List<EorEngagement> engagements =
        [
            Engagement(providerId, new DateOnly(2026, 1, 1)),
            Engagement(Guid.NewGuid(), new DateOnly(2026, 1, 1)),
        ];

        Assert.Equal(
            1, Invoices.EngagedDuring(engagements, providerId, new DateOnly(2026, 5, 1)));
    }

    // ── what is refused ────────────────────────────────────────────────────

    [Fact]
    public void AnInvoiceNeedsAPeriod()
    {
        EorInvoice invoice = Invoice();
        invoice.Period = default;

        Assert.Equal(EorRefusal.PeriodRequired, Invoices.Check(invoice));
    }

    [Fact]
    public void AnInvoiceNeedsACurrency()
    {
        EorInvoice invoice = Invoice();
        invoice.Currency = " ";

        Assert.Equal(EorRefusal.CurrencyUnknown, Invoices.Check(invoice));
    }

    [Fact]
    public void ANegativeAmountIsRefused()
    {
        Assert.Equal(EorRefusal.AmountNegative, Invoices.Check(Invoice(gross: -1m)));
    }

    // A zero-value month happens when a provider has nobody engaged, and a
    // credit note is a real thing. Neither is an error.
    [Fact]
    public void AZeroInvoiceIsAccepted()
    {
        Assert.Equal(
            EorRefusal.None,
            Invoices.Check(Invoice(gross: 0m, employerCosts: 0m, fee: 0m, billed: 0)));
    }

    private static EorEngagement Engagement(Guid providerId, DateOnly start, DateOnly? end = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EmployeeId = Guid.NewGuid(),
            ProviderId = providerId,
            CountryOfEngagement = "Kenya",
            Currency = "KES",
            GrossMonthly = 5_000m,
            StartDate = start,
            EndDate = end,
        };
}
