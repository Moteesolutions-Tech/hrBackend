using Motee.Domain.Eor;

namespace Motee.Tests.Eor;

// Workers employed through a provider in their own country, and what one costs.
//
// The two things these are really about. An engagement's stage comes from its
// dates rather than a dropdown beside them — the screen lets a worker be Active
// with an end date two months past, or Ended with no end date at all. And the
// cost is derived from the provider's own fee percentage and a dated exchange
// rate, where the screen asks somebody to type a USD total that never revalues.
//
// Written before the rules.
public class EngagementTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    private static EorProvider Provider(
        decimal feePercent = 10m,
        EorProviderStatus status = EorProviderStatus.Active,
        params string[] countries) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Globalise",
            Status = status,
            ManagementFeePercent = feePercent,
            CountriesCovered = [.. countries.Length == 0 ? ["Kenya", "India"] : countries],
        };

    private static EorEngagement Engagement(
        Guid providerId,
        string country = "Kenya",
        decimal gross = 5_000m,
        decimal employerCost = 1_000m,
        DateOnly? start = null,
        DateOnly? end = null,
        decimal? rate = null,
        DateOnly? rateOn = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EmployeeId = Guid.NewGuid(),
            ProviderId = providerId,
            CountryOfEngagement = country,
            Currency = "KES",
            GrossMonthly = gross,
            EmployerCostMonthly = employerCost,
            StartDate = start ?? new DateOnly(2026, 1, 1),
            EndDate = end,
            UsdRate = rate,
            RateOn = rateOn,
        };

    // ── where an engagement has got to ─────────────────────────────────────

    [Fact]
    public void AnEngagementUnderwayIsActive()
    {
        EorProvider provider = Provider();

        Assert.Equal(
            EngagementStage.Active,
            Engagements.StageOf(Engagement(provider.Id), Today));
    }

    [Fact]
    public void AnEngagementStartingLaterIsOnboarding()
    {
        EorProvider provider = Provider();

        Assert.Equal(
            EngagementStage.Onboarding,
            Engagements.StageOf(
                Engagement(provider.Id, start: Today.AddDays(14)), Today));
    }

    [Fact]
    public void AnEngagementWithAnEndDateToComeIsOffboarding()
    {
        EorProvider provider = Provider();

        Assert.Equal(
            EngagementStage.Offboarding,
            Engagements.StageOf(Engagement(provider.Id, end: Today.AddDays(30)), Today));
    }

    [Fact]
    public void AnEngagementPastItsEndDateHasEnded()
    {
        EorProvider provider = Provider();

        Assert.Equal(
            EngagementStage.Ended,
            Engagements.StageOf(Engagement(provider.Id, end: Today.AddDays(-1)), Today));
    }

    [Fact]
    public void AnEngagementEndingTodayHasEnded()
    {
        EorProvider provider = Provider();

        Assert.Equal(
            EngagementStage.Ended,
            Engagements.StageOf(Engagement(provider.Id, end: Today), Today));
    }

    // Every figure that counts EOR workers or their cost has to agree on this,
    // the same way EmployeeLifecycle.InPost settles who is in post.
    [Fact]
    public void EverythingButEndedIsLive()
    {
        EorProvider provider = Provider();

        Assert.True(Engagements.IsLive(Engagement(provider.Id), Today));
        Assert.True(Engagements.IsLive(
            Engagement(provider.Id, start: Today.AddDays(7)), Today));
        Assert.True(Engagements.IsLive(
            Engagement(provider.Id, end: Today.AddDays(7)), Today));
        Assert.False(Engagements.IsLive(
            Engagement(provider.Id, end: Today.AddDays(-7)), Today));
    }

    // ── what it costs ──────────────────────────────────────────────────────

    // From the provider's own percentage. The screen has a management fee field
    // on the worker and a percentage on the provider and reconciles neither, so
    // raising the provider's rate changes no worker's cost.
    [Fact]
    public void TheManagementFeeComesFromTheProvidersPercentage()
    {
        EorProvider provider = Provider(feePercent: 12m);

        Assert.Equal(
            600m, Engagements.ManagementFee(Engagement(provider.Id, gross: 5_000m), provider));
    }

    [Fact]
    public void RaisingTheProvidersFeeRaisesEveryWorkersCost()
    {
        EorProvider provider = Provider(feePercent: 10m);
        EorEngagement engagement = Engagement(provider.Id, gross: 5_000m);

        decimal before = Engagements.TotalMonthly(engagement, provider);

        provider.ManagementFeePercent = 15m;

        Assert.True(Engagements.TotalMonthly(engagement, provider) > before);
    }

    [Fact]
    public void TheMonthlyTotalIsGrossPlusEmployerCostPlusFee()
    {
        EorProvider provider = Provider(feePercent: 10m);

        Assert.Equal(
            6_500m,
            Engagements.TotalMonthly(
                Engagement(provider.Id, gross: 5_000m, employerCost: 1_000m), provider));
    }

    [Fact]
    public void AFeeOfZeroIsLegitimate()
    {
        EorProvider provider = Provider(feePercent: 0m);

        Assert.Equal(
            0m, Engagements.ManagementFee(Engagement(provider.Id), provider));
    }

    // ── the exchange rate ──────────────────────────────────────────────────

    [Fact]
    public void TheUsdTotalIsConvertedAtTheRecordedRate()
    {
        EorProvider provider = Provider(feePercent: 10m);

        EorEngagement engagement = Engagement(
            provider.Id, gross: 5_000m, employerCost: 1_000m, rate: 0.0077m, rateOn: Today);

        // 6,500 local at 0.0077 is 50.05.
        Assert.Equal(50.05m, Engagements.TotalUsd(engagement, provider));
    }

    // A pay rise revalues the total, which is the entire reason a rate is held
    // instead of a converted figure.
    [Fact]
    public void APayRiseMovesTheConvertedTotal()
    {
        EorProvider provider = Provider(feePercent: 10m);

        EorEngagement engagement = Engagement(
            provider.Id, gross: 5_000m, rate: 0.01m, rateOn: Today);

        decimal? before = Engagements.TotalUsd(engagement, provider);

        engagement.GrossMonthly = 6_000m;

        Assert.True(Engagements.TotalUsd(engagement, provider) > before);
    }

    // Null, not zero. A worker contributing nothing to the total is a worker
    // whose cost has been forgotten, and zero hides that inside a sum.
    [Fact]
    public void WithNoRateThereIsNoConvertedTotal()
    {
        EorProvider provider = Provider();

        Assert.Null(Engagements.TotalUsd(Engagement(provider.Id), provider));
    }

    [Fact]
    public void AFreshRateIsNotStale()
    {
        EorProvider provider = Provider();

        Assert.False(Engagements.RateIsStale(
            Engagement(provider.Id, rate: 0.01m, rateOn: Today.AddDays(-30)), Today));
    }

    [Fact]
    public void ARateOlderThanAQuarterIsStale()
    {
        EorProvider provider = Provider();

        Assert.True(Engagements.RateIsStale(
            Engagement(provider.Id, rate: 0.01m, rateOn: Today.AddDays(-120)), Today));
    }

    [Fact]
    public void ARateWithNoDateIsStale()
    {
        EorProvider provider = Provider();

        Assert.True(Engagements.RateIsStale(Engagement(provider.Id), Today));
    }

    // ── what is refused ────────────────────────────────────────────────────

    // The check the screen's country and provider dropdowns never make against
    // each other. Engaging somebody where the provider cannot employ leaves a
    // worker with no legal employer.
    [Fact]
    public void AProviderCannotEngageSomebodyInACountryItDoesNotCover()
    {
        EorProvider provider = Provider(countries: ["Kenya"]);

        Assert.Equal(
            EorRefusal.ProviderDoesNotCoverCountry,
            Engagements.Check(
                Engagement(provider.Id, country: "Brazil"), provider, alreadyEngaged: false));
    }

    [Fact]
    public void CoverageIsNotCaseOrSpaceSensitive()
    {
        EorProvider provider = Provider(countries: ["Kenya"]);

        Assert.True(EorProviders.Covers(provider, " kenya "));
    }

    [Fact]
    public void AnInactiveProviderCannotTakeOnNewWorkers()
    {
        EorProvider provider = Provider(status: EorProviderStatus.Inactive);

        Assert.Equal(
            EorRefusal.ProviderInactive,
            Engagements.Check(Engagement(provider.Id), provider, alreadyEngaged: false));
    }

    // One live engagement per person. Somebody moving between providers ends
    // one and starts another, rather than having two rows both claiming to be
    // current.
    [Fact]
    public void SomebodyAlreadyEngagedCannotBeEngagedTwice()
    {
        EorProvider provider = Provider();

        Assert.Equal(
            EorRefusal.AlreadyEngaged,
            Engagements.Check(Engagement(provider.Id), provider, alreadyEngaged: true));
    }

    [Fact]
    public void AnEngagementCannotEndBeforeItStarts()
    {
        EorProvider provider = Provider();

        Assert.Equal(
            EorRefusal.EndsBeforeStart,
            Engagements.Check(
                Engagement(
                    provider.Id,
                    start: new DateOnly(2026, 6, 1),
                    end: new DateOnly(2026, 5, 1)),
                provider,
                alreadyEngaged: false));
    }

    [Fact]
    public void AWorkerOnNothingIsRefused()
    {
        EorProvider provider = Provider();

        Assert.Equal(
            EorRefusal.GrossNotPositive,
            Engagements.Check(
                Engagement(provider.Id, gross: 0m), provider, alreadyEngaged: false));
    }

    [Fact]
    public void ANegativeEmployerCostIsRefused()
    {
        EorProvider provider = Provider();

        Assert.Equal(
            EorRefusal.CostNegative,
            Engagements.Check(
                Engagement(provider.Id, employerCost: -1m), provider, alreadyEngaged: false));
    }

    [Fact]
    public void ARateOfZeroIsRefused()
    {
        EorProvider provider = Provider();

        Assert.Equal(
            EorRefusal.RateNotPositive,
            Engagements.Check(
                Engagement(provider.Id, rate: 0m, rateOn: Today),
                provider,
                alreadyEngaged: false));
    }

    // The whole point of holding a rate instead of a converted total is that
    // its age is visible, which an undated rate is not.
    [Fact]
    public void ARateWithoutADateIsRefused()
    {
        EorProvider provider = Provider();

        Assert.Equal(
            EorRefusal.RateDateMissing,
            Engagements.Check(
                Engagement(provider.Id, rate: 0.01m), provider, alreadyEngaged: false));
    }

    [Fact]
    public void AGoodEngagementIsAccepted()
    {
        EorProvider provider = Provider();

        Assert.Equal(
            EorRefusal.None,
            Engagements.Check(
                Engagement(provider.Id, rate: 0.0077m, rateOn: Today),
                provider,
                alreadyEngaged: false));
    }

    // ── the provider itself ────────────────────────────────────────────────

    [Fact]
    public void AProviderNeedsAName()
    {
        Assert.Equal(EorRefusal.NameRequired, EorProviders.Check("  ", 10m, ["Kenya"]));
    }

    // A provider covering nowhere can employ nobody, so recording one is
    // recording a partner no engagement can ever name.
    [Fact]
    public void AProviderCoveringNowhereIsRefused()
    {
        Assert.Equal(EorRefusal.NoCountriesCovered, EorProviders.Check("Globalise", 10m, []));
        Assert.Equal(
            EorRefusal.NoCountriesCovered, EorProviders.Check("Globalise", 10m, ["  "]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(95)]
    public void AFeeThatCannotBeRightIsRefused(int percent)
    {
        Assert.Equal(
            EorRefusal.FeePercentOutOfRange,
            EorProviders.Check("Globalise", percent, ["Kenya"]));
    }

    [Fact]
    public void TheCountriesAreTidiedOnTheWayIn()
    {
        List<string> tidied = EorProviders.Tidy([" Kenya ", "kenya", "India", "  ", "India"]);

        Assert.Equal(2, tidied.Count);
        Assert.Contains("Kenya", tidied);
        Assert.Contains("India", tidied);
    }
}
