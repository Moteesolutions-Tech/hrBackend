using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Eor;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Eor;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Workers employed abroad through a provider, out of the database.
//
// The two things these are really about. A worker is an employee with an
// engagement, not a second people record — the screen collects the whole
// employee profile again, and employees already holds every one of those
// columns. And the figures are derived: the management fee from the provider's
// own percentage, the USD cost from a dated rate, the worker count from the
// engagements. The screen types all three by hand.
//
// Written before the services.
[Collection(PostgresCollection.Name)]
public class EorServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly Today = new(2026, 6, 15);

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _adaId;
    private Guid _boluId;

    // ── providers ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AddsAProvider()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        EorResult<EorProviderDto> result = await Eor(provider).AddProviderAsync(Partner());

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
        Assert.Equal("Globalise", result.Value!.Name);
        Assert.Equal(0, result.Value.WorkerCount);
    }

    // A provider covering nowhere can employ nobody, so recording one is
    // recording a partner no engagement can ever name.
    [SkippableFact]
    public async Task RefusesAProviderCoveringNowhere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            EorRefusal.NoCountriesCovered,
            (await Eor(provider).AddProviderAsync(Partner() with { CountriesCovered = [] }))
                .Refusal);
    }

    [SkippableFact]
    public async Task RefusesAFeeThatCannotBeRight()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            EorRefusal.FeePercentOutOfRange,
            (await Eor(provider).AddProviderAsync(Partner() with { ManagementFeePercent = 90m }))
                .Refusal);
    }

    // Counted from the engagements, never stored. The screen keeps workerCount
    // on the provider and the figure sits on the card beside the list it
    // disagrees with.
    [SkippableFact]
    public async Task AProvidersWorkerCountFollowsTheEngagements()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        await Engage(provider, providerId, _adaId);

        Assert.Equal(1, (await Eor(provider).ProviderAsync(providerId))!.WorkerCount);
    }

    [SkippableFact]
    public async Task EndingAnEngagementDropsTheProvidersWorkerCount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        Guid engagementId = (await Engage(provider, providerId, _adaId)).Value!.Id;

        await Eor(provider).EndAsync(
            engagementId, new EndEngagementRequest { EndDate = Today.AddDays(-1) });

        Assert.Equal(0, (await Eor(provider).ProviderAsync(providerId))!.WorkerCount);
    }

    [SkippableFact]
    public async Task RefusesToRetireAProviderWithWorkersStillEngaged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        await Engage(provider, providerId, _adaId);

        Assert.Equal(
            EorRefusal.ProviderHasLiveEngagements,
            (await Eor(provider).RetireProviderAsync(providerId)).Refusal);
    }

    // Made inactive, not deleted: invoices and ended engagements point at the
    // record, and who employed somebody three years ago is the point of keeping
    // any of this.
    [SkippableFact]
    public async Task RetiringAProviderLeavesTheRecordBehind()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        EorResult<EorProviderDto> retired = await Eor(provider).RetireProviderAsync(providerId);

        Assert.True(retired.Succeeded);
        Assert.Equal(EorProviderStatus.Inactive, retired.Value!.Status);
        Assert.NotNull(await Eor(provider).ProviderAsync(providerId));
    }

    // Dropping a country somebody is engaged in would leave that worker with no
    // legal employer and nothing on the screen saying so.
    [SkippableFact]
    public async Task RefusesToDropACountrySomebodyIsEngagedIn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        await Engage(provider, providerId, _adaId, country: "Kenya");

        EorResult<EorProviderDto> updated = await Eor(provider).UpdateProviderAsync(
            providerId, Partner() with { CountriesCovered = ["India"] });

        Assert.Equal(EorRefusal.ProviderDoesNotCoverCountry, updated.Refusal);
    }

    // ── engaging somebody ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task EngagesAnExistingEmployee()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        EorResult<EorWorkerDto> result = await Engage(provider, providerId, _adaId);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        EorWorkerDto worker = result.Value!;

        // The person's details come from the employee record rather than being
        // collected again.
        Assert.Equal("Ada Okafor", worker.Name);
        Assert.Equal("ada@acme.com", worker.Email);
        Assert.Equal("Engineering", worker.Department);
        Assert.Equal(_adaId, worker.EmployeeId);
    }

    [SkippableFact]
    public async Task RefusesToEngageSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        Assert.Equal(
            EorRefusal.EmployeeNotFound,
            (await Engage(provider, providerId, Guid.NewGuid())).Refusal);
    }

    // The check the screen's country and provider dropdowns never make against
    // each other.
    [SkippableFact]
    public async Task RefusesACountryTheProviderCannotEmployIn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        Assert.Equal(
            EorRefusal.ProviderDoesNotCoverCountry,
            (await Engage(provider, providerId, _adaId, country: "Brazil")).Refusal);
    }

    [SkippableFact]
    public async Task RefusesASecondLiveEngagementForTheSamePerson()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        await Engage(provider, providerId, _adaId);

        Assert.Equal(
            EorRefusal.AlreadyEngaged,
            (await Engage(provider, providerId, _adaId)).Refusal);
    }

    // Moving between providers: end one, start another. The history is worth
    // having, and two rows both claiming to be current is not.
    [SkippableFact]
    public async Task SomebodyCanMoveToAnotherProviderOnceTheFirstEnds()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid first = await PartneredAsync(provider);
        Guid engagementId = (await Engage(provider, first, _adaId)).Value!.Id;

        await Eor(provider).EndAsync(
            engagementId, new EndEngagementRequest { EndDate = Today });

        Guid second = await PartneredAsync(provider, name: "Remote People");

        Assert.True((await Engage(provider, second, _adaId)).Succeeded);
    }

    [SkippableFact]
    public async Task RefusesAnEngagementThroughARetiredProvider()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        await Eor(provider).RetireProviderAsync(providerId);

        Assert.Equal(
            EorRefusal.ProviderInactive,
            (await Engage(provider, providerId, _adaId)).Refusal);
    }

    // Where somebody is legally employed is a fact about them, not only about
    // the engagement, and the employee screens read that column.
    [SkippableFact]
    public async Task EngagingSomebodyRecordsTheirCountryOnTheirOwnRecord()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        await Engage(provider, providerId, _adaId, country: "Kenya");

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        Employee employee = await context.Employees.SingleAsync(row => row.Id == _adaId);

        Assert.Equal("Kenya", employee.CountryOfEmployment);
    }

    // ── what it costs ──────────────────────────────────────────────────────

    // From the provider's own percentage. The screen types a fee per worker
    // alongside a percentage on the provider and reconciles neither.
    [SkippableFact]
    public async Task TheManagementFeeComesFromTheProvider()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider, feePercent: 12m);

        EorWorkerDto worker =
            (await Engage(provider, providerId, _adaId, gross: 5_000m)).Value!;

        Assert.Equal(600m, worker.ManagementFee);
        Assert.Equal(6_600m, worker.TotalMonthly);
    }

    [SkippableFact]
    public async Task RaisingTheProvidersFeeMovesEveryWorkersCost()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider, feePercent: 10m);
        Guid engagementId = (await Engage(provider, providerId, _adaId, gross: 5_000m)).Value!.Id;

        await Eor(provider).UpdateProviderAsync(
            providerId, Partner() with { ManagementFeePercent = 20m });

        Assert.Equal(1_000m, (await Eor(provider).WorkerAsync(engagementId))!.ManagementFee);
    }

    [SkippableFact]
    public async Task TheConvertedCostComesFromTheRecordedRate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider, feePercent: 10m);

        EorWorkerDto worker = (await Engage(
            provider,
            providerId,
            _adaId,
            gross: 5_000m,
            employerCost: 1_000m,
            rate: 0.01m,
            rateOn: Today)).Value!;

        Assert.Equal(65m, worker.TotalUsd);
        Assert.False(worker.RateIsStale);
    }

    // Null, not zero. A worker contributing nothing to the total is one whose
    // cost has been forgotten, and zero hides that inside a sum.
    [SkippableFact]
    public async Task WithNoRateThereIsNoConvertedCost()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        EorWorkerDto worker = (await Engage(provider, providerId, _adaId)).Value!;

        Assert.Null(worker.TotalUsd);
        Assert.True(worker.RateIsStale);
    }

    [SkippableFact]
    public async Task RefusesARateWithNoDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        Assert.Equal(
            EorRefusal.RateDateMissing,
            (await Engage(provider, providerId, _adaId, rate: 0.01m)).Refusal);
    }

    // ── compliance ─────────────────────────────────────────────────────────

    // The screen defaults the employment contract to complete, so a worker
    // added a minute ago is recorded as having signed one.
    [SkippableFact]
    public async Task ANewEngagementStartsWithEveryRequirementOutstanding()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        EorWorkerDto worker = (await Engage(provider, providerId, _adaId)).Value!;

        Assert.Equal(ComplianceCatalogue.All.Count, worker.Compliance.Count);
        Assert.All(
            worker.Compliance,
            item => Assert.Equal(ComplianceState.Pending, item.State));
        Assert.False(worker.FullyCompliant);
        Assert.NotEmpty(worker.Blockers);
    }

    [SkippableFact]
    public async Task RecordingARequirementClearsItsBlocker()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        Guid engagementId = (await Engage(provider, providerId, _adaId)).Value!.Id;

        EorResult<EorWorkerDto> after = await Compliance(provider).RecordAsync(
            engagementId,
            ComplianceKey.Contract,
            new ComplianceRequest { State = ComplianceState.Complete });

        Assert.True(after.Succeeded, $"{after.Outcome} / {after.Refusal}");
        Assert.DoesNotContain(
            after.Value!.Blockers, blocker => blocker.Contains("Employment contract"));
    }

    // The expiry wins over the tick, which is the whole reason the expiry
    // exists. Somebody ticking Complete in March does not make a permit valid
    // in December.
    [SkippableFact]
    public async Task APermitPastItsExpiryBlocksWhateverWasTicked()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        Guid engagementId = (await Engage(provider, providerId, _adaId)).Value!.Id;

        EorResult<EorWorkerDto> after = await Compliance(provider).RecordAsync(
            engagementId,
            ComplianceKey.WorkPermit,
            new ComplianceRequest
            {
                State = ComplianceState.Complete,
                ExpiresOn = Today.AddDays(-1),
            });

        ComplianceItemDto permit =
            after.Value!.Compliance.Single(item => item.Key == ComplianceKey.WorkPermit);

        Assert.Equal(ComplianceState.Complete, permit.State);
        Assert.Equal(ComplianceStanding.Expired, permit.Standing);
        Assert.Contains(after.Value.Blockers, blocker => blocker.Contains("expired on"));
    }

    [SkippableFact]
    public async Task APermitRunningOutSoonIsFlaggedWithoutBlocking()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        Guid engagementId = (await Engage(provider, providerId, _adaId)).Value!.Id;

        await CompleteAllAsync(provider, engagementId, permitExpires: Today.AddDays(20));

        EorWorkerDto worker = (await Eor(provider).WorkerAsync(engagementId))!;

        Assert.Equal(
            ComplianceStanding.ExpiringSoon,
            worker.Compliance.Single(item => item.Key == ComplianceKey.WorkPermit).Standing);
        Assert.Empty(worker.Blockers);
    }

    // An expiry on a signed contract would read as expired a year later and
    // block a worker who is perfectly lawful.
    [SkippableFact]
    public async Task AnExpiryOnSomethingThatCannotLapseIsNotKept()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        Guid engagementId = (await Engage(provider, providerId, _adaId)).Value!.Id;

        EorResult<EorWorkerDto> after = await Compliance(provider).RecordAsync(
            engagementId,
            ComplianceKey.Contract,
            new ComplianceRequest
            {
                State = ComplianceState.Complete,
                ExpiresOn = Today.AddDays(-1),
            });

        Assert.Null(
            after.Value!.Compliance.Single(item => item.Key == ComplianceKey.Contract).ExpiresOn);
    }

    // An unverified "complete" is the thing this register exists to avoid.
    [SkippableFact]
    public async Task MarkingSomethingCompleteRecordsWhenItWasChecked()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        Guid engagementId = (await Engage(provider, providerId, _adaId)).Value!.Id;

        EorResult<EorWorkerDto> after = await Compliance(provider).RecordAsync(
            engagementId,
            ComplianceKey.Contract,
            new ComplianceRequest { State = ComplianceState.Complete });

        Assert.Equal(
            Today,
            after.Value!.Compliance.Single(item => item.Key == ComplianceKey.Contract).VerifiedOn);
    }

    // Reported rather than refused: a provider confirming something by email is
    // still a record, and the register should show what rests on somebody's
    // word.
    [SkippableFact]
    public async Task SomethingCompleteWithNoDocumentIsReportedAsUnevidenced()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        Guid engagementId = (await Engage(provider, providerId, _adaId)).Value!.Id;

        EorResult<EorWorkerDto> after = await Compliance(provider).RecordAsync(
            engagementId,
            ComplianceKey.Contract,
            new ComplianceRequest { State = ComplianceState.Complete });

        Assert.True(
            after.Value!.Compliance.Single(item => item.Key == ComplianceKey.Contract)
                .Unevidenced);
    }

    [SkippableFact]
    public async Task TheComplianceRegisterCarriesEveryWorker()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        await Engage(provider, providerId, _adaId);
        await Engage(provider, providerId, _boluId);

        IReadOnlyList<ComplianceRowDto> register = await Compliance(provider).RegisterAsync();

        Assert.Equal(2, register.Count);
        Assert.All(register, row => Assert.NotEmpty(row.Items));
    }

    // ── the register ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheWorkersListCanBeNarrowedToThoseWithSomethingOutstanding()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        Guid clear = (await Engage(provider, providerId, _adaId)).Value!.Id;
        await Engage(provider, providerId, _boluId);

        await CompleteAllAsync(provider, clear);

        PagedResult<EorWorkerDto> blocked = await Eor(provider).WorkersAsync(
            new EorWorkerQuery { WithBlockers = true });

        Assert.Equal(1, blocked.TotalItems);
        Assert.Equal(_boluId, blocked.Items[0].EmployeeId);
    }

    [SkippableFact]
    public async Task TheWorkersListCanBeNarrowedByStage()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        Guid engagementId = (await Engage(provider, providerId, _adaId)).Value!.Id;
        await Engage(provider, providerId, _boluId);

        await Eor(provider).EndAsync(
            engagementId, new EndEngagementRequest { EndDate = Today.AddDays(-1) });

        PagedResult<EorWorkerDto> ended = await Eor(provider).WorkersAsync(
            new EorWorkerQuery { Stage = EngagementStage.Ended });

        Assert.Equal(1, ended.TotalItems);
    }

    [SkippableFact]
    public async Task RefusesAnEndDateBeforeTheStart()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        Guid engagementId = (await Engage(provider, providerId, _adaId)).Value!.Id;

        Assert.Equal(
            EorRefusal.EndsBeforeStart,
            (await Eor(provider).EndAsync(
                engagementId,
                new EndEngagementRequest { EndDate = new DateOnly(2025, 1, 1) })).Refusal);
    }

    // The engagement goes; the person stays. Deleting the employee would take
    // their documents, leave and assets with them.
    [SkippableFact]
    public async Task RemovingAnEngagementLeavesThePersonBehind()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);
        Guid engagementId = (await Engage(provider, providerId, _adaId)).Value!.Id;

        Assert.Equal(EorOutcome.Succeeded, await Eor(provider).RemoveAsync(engagementId));

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        Assert.True(await context.Employees.AnyAsync(row => row.Id == _adaId));
        Assert.False(await context.EorEngagements.AnyAsync());
    }

    // ── the summary ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheSummaryTotalsEachCurrencyExactly()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider, feePercent: 10m);

        await Engage(
            provider, providerId, _adaId, gross: 5_000m, employerCost: 1_000m, currency: "KES");
        await Engage(
            provider,
            providerId,
            _boluId,
            gross: 80_000m,
            employerCost: 10_000m,
            currency: "INR",
            country: "India");

        EorSummary summary = await Eor(provider).SummaryAsync();

        Assert.Equal(2, summary.Workers);
        Assert.Equal(2, summary.Countries);
        Assert.Equal(
            6_500m, summary.ByCurrency.Single(total => total.Currency == "KES").MonthlyCost);
        Assert.Equal(
            98_000m, summary.ByCurrency.Single(total => total.Currency == "INR").MonthlyCost);
    }

    // A spend figure missing a worker is worse than one that says so.
    [SkippableFact]
    public async Task TheSummarySaysWhenItCouldNotConvertEverything()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        await Engage(provider, providerId, _adaId, rate: 0.01m, rateOn: Today);
        await Engage(provider, providerId, _boluId);

        EorSummary summary = await Eor(provider).SummaryAsync();

        Assert.Equal(2, summary.Workers);
        Assert.Equal(1, summary.ConvertedWorkers);
        Assert.False(summary.CostIsComplete);
    }

    // The card row defect: workers and spend excluded ended engagements while
    // countries included them, so the figure named countries the company had
    // left.
    [SkippableFact]
    public async Task ACountryTheCompanyHasLeftIsNotCounted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        Guid engagementId =
            (await Engage(provider, providerId, _adaId, country: "India")).Value!.Id;
        await Engage(provider, providerId, _boluId, country: "Kenya");

        await Eor(provider).EndAsync(
            engagementId, new EndEngagementRequest { EndDate = Today.AddDays(-1) });

        EorSummary summary = await Eor(provider).SummaryAsync();

        Assert.Equal(1, summary.Workers);
        Assert.Equal(1, summary.Countries);
    }

    [SkippableFact]
    public async Task TheSummaryCountsWhoHasSomethingOutstanding()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid providerId = await PartneredAsync(provider);

        Guid clear = (await Engage(provider, providerId, _adaId)).Value!.Id;
        await Engage(provider, providerId, _boluId);

        await CompleteAllAsync(provider, clear);

        EorSummary summary = await Eor(provider).SummaryAsync();

        Assert.Equal(1, summary.WorkersWithBlockers);
    }

    // ── tenancy ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanysProvidersAreNotAnothers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PartneredAsync(provider);

        RegisterTenantResult other = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Other",
            LastName = "Owner",
            Email = "owner@other.com",
            CompanyName = "Other Limited",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = other.TenantId;

        Assert.Empty(await Eor(provider).ProvidersAsync());
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

    private static IEorService Eor(ServiceProvider provider) =>
        Resolve<IEorService>(provider);

    private static IEorComplianceService Compliance(ServiceProvider provider) =>
        Resolve<IEorComplianceService>(provider);

    private static EorProviderRequest Partner() => new()
    {
        Name = "Globalise",
        CountriesCovered = ["Kenya", "India"],
        ManagementFeePercent = 10m,
        PartneredOn = new DateOnly(2024, 3, 1),
        Website = "https://globalise.test",
    };

    private static async Task<Guid> PartneredAsync(
        ServiceProvider provider,
        string name = "Globalise",
        decimal feePercent = 10m)
    {
        EorResult<EorProviderDto> result = await Eor(provider).AddProviderAsync(
            Partner() with { Name = name, ManagementFeePercent = feePercent });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Value!.Id;
    }

    private static Task<EorResult<EorWorkerDto>> Engage(
        ServiceProvider provider,
        Guid providerId,
        Guid employeeId,
        string country = "Kenya",
        string currency = "KES",
        decimal gross = 5_000m,
        decimal employerCost = 1_000m,
        decimal? rate = null,
        DateOnly? rateOn = null) =>
        Eor(provider).EngageAsync(new EorEngagementRequest
        {
            EmployeeId = employeeId,
            ProviderId = providerId,
            CountryOfEngagement = country,
            Currency = currency,
            GrossMonthly = gross,
            EmployerCostMonthly = employerCost,
            StartDate = new DateOnly(2026, 1, 1),
            UsdRate = rate,
            RateOn = rateOn,
        });

    private static async Task CompleteAllAsync(
        ServiceProvider provider,
        Guid engagementId,
        DateOnly? permitExpires = null)
    {
        foreach (ComplianceRequirement requirement in ComplianceCatalogue.All)
        {
            EorResult<EorWorkerDto> result = await Compliance(provider).RecordAsync(
                engagementId,
                requirement.Key,
                new ComplianceRequest
                {
                    State = ComplianceState.Complete,
                    ExpiresOn = requirement.Key == ComplianceKey.WorkPermit
                        ? permitExpires
                        : null,
                });

            Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
        }
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
