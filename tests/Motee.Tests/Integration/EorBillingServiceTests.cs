using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Eor;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Eor;
using Motee.Domain.Organisation;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Provider invoices, out of the database.
//
// The comparison is the point: whether the provider's own parts make their own
// total, and whether the headcount they billed matches the workers we had
// engaged that month. The screen renders invoices checked against nothing, so a
// provider billing for nine when seven are engaged reads as a normal month —
// and an invoice sits at "Pending" past its due date until somebody opens the
// row.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class EorBillingServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly Today = new(2026, 6, 15);

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _adaId;
    private Guid _boluId;

    // ── recording one ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RecordsAnInvoice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        EorResult<EorInvoiceDto> result = await Billing(provider).RecordAsync(
            Billed(providerId));

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
        Assert.Equal("May 2026", result.Value!.PeriodLabel);
        Assert.Equal("Globalise", result.Value.ProviderName);
    }

    // A period is a month, not a day. Two invoices dated the 1st and the 31st
    // of May would otherwise both be stored and both counted.
    [SkippableFact]
    public async Task ThePeriodIsNormalisedToTheMonth()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        EorResult<EorInvoiceDto> result = await Billing(provider).RecordAsync(
            Billed(providerId) with { Period = new DateOnly(2026, 5, 28) });

        Assert.Equal(new DateOnly(2026, 5, 1), result.Value!.Period);
    }

    [SkippableFact]
    public async Task RefusesANegativeAmount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        Assert.Equal(
            EorRefusal.AmountNegative,
            (await Billing(provider).RecordAsync(
                Billed(providerId) with { GrossPay = -1m })).Refusal);
    }

    [SkippableFact]
    public async Task RefusesAnInvoiceFromAProviderThatDoesNotExist()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            EorRefusal.ProviderNotFound,
            (await Billing(provider).RecordAsync(Billed(Guid.NewGuid()))).Refusal);
    }

    // ── does it add up ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnInvoiceWhoseTotalDisagreesWithItsPartsIsFlagged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        EorResult<EorInvoiceDto> result = await Billing(provider).RecordAsync(
            Billed(providerId) with { Total = 99_000m });

        Assert.False(result.Value!.TotalAddsUp);

        // Their figure is kept either way: a credit note or a one-off charge
        // shows up here, and replacing it with our sum would hide a dispute.
        Assert.Equal(99_000m, result.Value.Total);
        Assert.Equal(64_900m, result.Value.SumOfParts);
    }

    [SkippableFact]
    public async Task AnInvoiceThatAddsUpSaysSo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        Assert.True(
            (await Billing(provider).RecordAsync(Billed(providerId))).Value!.TotalAddsUp);
    }

    // ── billed against engaged ─────────────────────────────────────────────

    // The reason to hold invoices at all.
    [SkippableFact]
    public async Task TheHeadcountBilledIsComparedWithTheWorkersEngaged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        await Engage(provider, providerId, _adaId);
        await Engage(provider, providerId, _boluId, country: "India");

        EorResult<EorInvoiceDto> result = await Billing(provider).RecordAsync(
            Billed(providerId) with { BilledWorkerCount = 2 });

        Assert.Equal(2, result.Value!.EngagedWorkerCount);
        Assert.True(result.Value.HeadcountMatches);
    }

    [SkippableFact]
    public async Task BillingForMoreWorkersThanAreEngagedIsFlagged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        await Engage(provider, providerId, _adaId);

        EorResult<EorInvoiceDto> result = await Billing(provider).RecordAsync(
            Billed(providerId) with { BilledWorkerCount = 3 });

        Assert.Equal(1, result.Value!.EngagedWorkerCount);
        Assert.False(result.Value.HeadcountMatches);
    }

    // Somebody who started on the 20th is on that month's invoice for the
    // part-month, so leaving them out would make every joining month look
    // over-billed.
    [SkippableFact]
    public async Task SomebodyWhoStartedMidMonthIsCounted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        await Engage(provider, providerId, _adaId, start: new DateOnly(2026, 5, 20));

        EorResult<EorInvoiceDto> result = await Billing(provider).RecordAsync(
            Billed(providerId) with { BilledWorkerCount = 1 });

        Assert.Equal(1, result.Value!.EngagedWorkerCount);
    }

    [SkippableFact]
    public async Task SomebodyEngagedAfterThePeriodIsNotCounted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        await Engage(provider, providerId, _adaId, start: new DateOnly(2026, 6, 1));

        EorResult<EorInvoiceDto> result = await Billing(provider).RecordAsync(
            Billed(providerId) with { BilledWorkerCount = 1 });

        Assert.Equal(0, result.Value!.EngagedWorkerCount);
        Assert.False(result.Value.HeadcountMatches);
    }

    // ── where it stands ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnUnpaidInvoiceInsideItsTermsIsPending()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        EorResult<EorInvoiceDto> result = await Billing(provider).RecordAsync(
            Billed(providerId) with { DueOn = Today.AddDays(15) });

        Assert.Equal(InvoiceStanding.Pending, result.Value!.Standing);
        Assert.Equal(0, result.Value.DaysOverdue);
    }

    // An overdue invoice that reads as pending is one nobody chases.
    [SkippableFact]
    public async Task AnInvoicePastItsDueDateIsOverdueWithoutAnybodyTouchingIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        Guid invoiceId = (await Billing(provider).RecordAsync(
            Billed(providerId) with { DueOn = Today.AddDays(5) })).Value!.Id;

        _clock.Advance(TimeSpan.FromDays(20));

        EorInvoiceDto after = (await Billing(provider).InvoicesAsync())
            .Single(invoice => invoice.Id == invoiceId);

        Assert.Equal(InvoiceStanding.Overdue, after.Standing);
        Assert.Equal(15, after.DaysOverdue);
    }

    [SkippableFact]
    public async Task SettlingAnInvoiceMarksItPaid()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        Guid invoiceId = (await Billing(provider).RecordAsync(Billed(providerId))).Value!.Id;

        EorResult<EorInvoiceDto> settled = await Billing(provider).SettleAsync(
            invoiceId, new SettleInvoiceRequest { PaidOn = Today });

        Assert.Equal(InvoiceStanding.Paid, settled.Value!.Standing);
        Assert.Equal(Today, settled.Value.PaidOn);
    }

    // For one marked paid by mistake. Paid, overdue and pending all follow from
    // the date, so there is no status to correct separately.
    [SkippableFact]
    public async Task AnInvoiceSettledByMistakeCanBeReopened()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        Guid invoiceId = (await Billing(provider).RecordAsync(Billed(providerId))).Value!.Id;

        await Billing(provider).SettleAsync(
            invoiceId, new SettleInvoiceRequest { PaidOn = Today });

        EorResult<EorInvoiceDto> reopened = await Billing(provider).SettleAsync(
            invoiceId, new SettleInvoiceRequest { PaidOn = null });

        Assert.Equal(InvoiceStanding.Pending, reopened.Value!.Standing);
        Assert.Null(reopened.Value.PaidOn);
    }

    [SkippableFact]
    public async Task APaidInvoiceStaysPaidPastItsDueDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        Guid invoiceId = (await Billing(provider).RecordAsync(
            Billed(providerId) with { DueOn = Today.AddDays(1) })).Value!.Id;

        await Billing(provider).SettleAsync(
            invoiceId, new SettleInvoiceRequest { PaidOn = Today });

        _clock.Advance(TimeSpan.FromDays(60));

        Assert.Equal(
            InvoiceStanding.Paid,
            (await Billing(provider).InvoicesAsync()).Single().Standing);
    }

    // ── the list ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheInvoicesCanBeNarrowedToOneProvider()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid first = await PartneredAsync(provider);
        Guid second = await PartneredAsync(provider, name: "Remote People");

        await Billing(provider).RecordAsync(Billed(first));
        await Billing(provider).RecordAsync(Billed(second));

        Assert.Single(await Billing(provider).InvoicesAsync(first));
        Assert.Equal(2, (await Billing(provider).InvoicesAsync()).Count);
    }

    [SkippableFact]
    public async Task TheNewestPeriodComesFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        await Billing(provider).RecordAsync(
            Billed(providerId) with { Period = new DateOnly(2026, 3, 1) });
        await Billing(provider).RecordAsync(
            Billed(providerId) with { Period = new DateOnly(2026, 5, 1) });

        Assert.Equal(
            new DateOnly(2026, 5, 1), (await Billing(provider).InvoicesAsync())[0].Period);
    }

    [SkippableFact]
    public async Task RemovesAnInvoice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        Guid invoiceId = (await Billing(provider).RecordAsync(Billed(providerId))).Value!.Id;

        Assert.Equal(EorOutcome.Succeeded, await Billing(provider).RemoveAsync(invoiceId));
        Assert.Empty(await Billing(provider).InvoicesAsync());
    }

    [SkippableFact]
    public async Task RemovingAnInvoiceThatIsNotThereSaysSo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(EorOutcome.NotFound, await Billing(provider).RemoveAsync(Guid.NewGuid()));
    }

    // ── arrange ────────────────────────────────────────────────────────────

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Owner",
            LastName = "Okafor",
            Email = "owner@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            registration.UserId.ToString();

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _adaId = await HireAsync(provider, "Ada");
        _boluId = await HireAsync(provider, "Bolu");

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IEorBillingService Billing(ServiceProvider provider) =>
        Resolve<IEorBillingService>(provider);

    private static IEorService Eor(ServiceProvider provider) =>
        Resolve<IEorService>(provider);

    private static EorInvoiceRequest Billed(Guid providerId) => new()
    {
        ProviderId = providerId,
        Period = new DateOnly(2026, 5, 1),
        Reference = "INV-2026-05",
        Currency = "USD",
        BilledWorkerCount = 1,
        GrossPay = 50_000m,
        EmployerCosts = 9_000m,
        ManagementFee = 5_900m,
        Total = 64_900m,
        IssuedOn = new DateOnly(2026, 6, 1),
        DueOn = new DateOnly(2026, 6, 30),
    };

    private static async Task<Guid> PartneredAsync(
        ServiceProvider provider,
        string name = "Globalise")
    {
        EorResult<EorProviderDto> result = await Eor(provider).AddProviderAsync(
            new EorProviderRequest
            {
                Name = name,
                CountriesCovered = ["Kenya", "India"],
                ManagementFeePercent = 10m,
            });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Value!.Id;
    }

    private static async Task Engage(
        ServiceProvider provider,
        Guid providerId,
        Guid employeeId,
        string country = "Kenya",
        DateOnly? start = null)
    {
        EorResult<EorWorkerDto> result = await Eor(provider).EngageAsync(
            new EorEngagementRequest
            {
                EmployeeId = employeeId,
                ProviderId = providerId,
                CountryOfEngagement = country,
                Currency = "KES",
                GrossMonthly = 5_000m,
                EmployerCostMonthly = 1_000m,
                StartDate = start ?? new DateOnly(2026, 1, 1),
            });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
    }

    private async Task<Guid> HireAsync(ServiceProvider provider, string first) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;
}
