using Motee.Domain.Eor;

namespace Motee.Tests.Eor;

// The figures on the EOR cards.
//
// The spend card is the one worth testing hard. It reads "Monthly EOR Spend —
// total cost (USD)" and sums a monthlyCostUsd typed on each worker, so it is a
// total of hand-entered conversions that never revalue. And the cards disagree
// about who they are describing: workers and spend count the active ones while
// countries counts all of them, so "Countries of engagement" includes countries
// the company exited.
//
// Written before the rules.
public class EorRollupTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    private readonly EorProvider _globalise = new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Name = "Globalise",
        Status = EorProviderStatus.Active,
        ManagementFeePercent = 10m,
        CountriesCovered = ["Kenya", "India", "Brazil"],
    };

    private readonly EorProvider _retired = new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Name = "Old Partner",
        Status = EorProviderStatus.Inactive,
        ManagementFeePercent = 15m,
        CountriesCovered = ["Brazil"],
    };

    private EorEngagement Engagement(
        string country = "Kenya",
        string currency = "KES",
        decimal gross = 5_000m,
        decimal employerCost = 1_000m,
        DateOnly? end = null,
        decimal? rate = null,
        DateOnly? rateOn = null,
        EorProvider? provider = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EmployeeId = Guid.NewGuid(),
            ProviderId = (provider ?? _globalise).Id,
            CountryOfEngagement = country,
            Currency = currency,
            GrossMonthly = gross,
            EmployerCostMonthly = employerCost,
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = end,
            UsdRate = rate,
            RateOn = rateOn,
        };

    private EorSummary Summarise(params EorEngagement[] engagements) =>
        EorRollups.Summarise(
            engagements,
            [_globalise, _retired],
            new Dictionary<Guid, IReadOnlyList<EorComplianceItem>>(),
            Today);

    // ── who is being counted ───────────────────────────────────────────────

    [Fact]
    public void OnlyLiveEngagementsAreCounted()
    {
        EorSummary summary = Summarise(
            Engagement(),
            Engagement(end: Today.AddDays(-30)));

        Assert.Equal(1, summary.Workers);
    }

    // The defect on the card row: workers and spend exclude ended engagements
    // while countries included them, so the figure named countries the company
    // had left.
    [Fact]
    public void ACountryTheCompanyHasLeftIsNotACountryOfEngagement()
    {
        EorSummary summary = Summarise(
            Engagement(country: "Kenya"),
            Engagement(country: "Brazil", end: Today.AddDays(-30)));

        Assert.Equal(1, summary.Countries);
    }

    [Fact]
    public void TwoWorkersInOneCountryAreOneCountry()
    {
        Assert.Equal(1, Summarise(Engagement(), Engagement()).Countries);
    }

    [Fact]
    public void TheSameCountrySpeltDifferentlyIsOneCountry()
    {
        Assert.Equal(
            1, Summarise(Engagement(country: "Kenya"), Engagement(country: " kenya ")).Countries);
    }

    [Fact]
    public void ProvidersAreCountedAndSoAreTheActiveOnes()
    {
        EorSummary summary = Summarise(Engagement());

        Assert.Equal(2, summary.Providers);
        Assert.Equal(1, summary.ActiveProviders);
    }

    // ── cost, per currency ─────────────────────────────────────────────────

    // Exact, because nothing is converted to produce it. The answer a finance
    // team can reconcile against a provider's invoice.
    [Fact]
    public void EachCurrencyGetsItsOwnExactSubtotal()
    {
        EorSummary summary = Summarise(
            Engagement(currency: "KES", gross: 5_000m, employerCost: 1_000m),
            Engagement(currency: "INR", gross: 80_000m, employerCost: 10_000m));

        Assert.Equal(2, summary.ByCurrency.Count);

        CurrencyTotal kes = summary.ByCurrency.Single(total => total.Currency == "KES");
        CurrencyTotal inr = summary.ByCurrency.Single(total => total.Currency == "INR");

        Assert.Equal(6_500m, kes.MonthlyCost);
        Assert.Equal(98_000m, inr.MonthlyCost);
    }

    [Fact]
    public void WorkersSharingACurrencyShareASubtotal()
    {
        EorSummary summary = Summarise(
            Engagement(currency: "KES", gross: 5_000m, employerCost: 1_000m),
            Engagement(currency: "KES", gross: 5_000m, employerCost: 1_000m));

        CurrencyTotal kes = Assert.Single(summary.ByCurrency);

        Assert.Equal(2, kes.Workers);
        Assert.Equal(13_000m, kes.MonthlyCost);
    }

    [Fact]
    public void TheBiggestSubtotalComesFirst()
    {
        EorSummary summary = Summarise(
            Engagement(currency: "KES", gross: 5_000m, employerCost: 0m),
            Engagement(currency: "INR", gross: 80_000m, employerCost: 0m));

        Assert.Equal("INR", summary.ByCurrency[0].Currency);
    }

    // ── cost, converted ────────────────────────────────────────────────────

    [Fact]
    public void TheUsdTotalIsTheSumOfWhatCouldBeConverted()
    {
        EorSummary summary = Summarise(
            Engagement(gross: 5_000m, employerCost: 1_000m, rate: 0.01m, rateOn: Today),
            Engagement(gross: 5_000m, employerCost: 1_000m, rate: 0.01m, rateOn: Today));

        // 6,500 local each, at 0.01.
        Assert.Equal(130m, summary.MonthlyCostUsd);
        Assert.Equal(2, summary.ConvertedWorkers);
        Assert.True(summary.CostIsComplete);
    }

    // A spend figure missing two workers is worse than one that says so.
    [Fact]
    public void AWorkerWithNoRateIsReportedRatherThanDropped()
    {
        EorSummary summary = Summarise(
            Engagement(rate: 0.01m, rateOn: Today),
            Engagement());

        Assert.Equal(2, summary.Workers);
        Assert.Equal(1, summary.ConvertedWorkers);
        Assert.False(summary.CostIsComplete);
    }

    [Fact]
    public void ARateOlderThanAQuarterIsCountedAsStale()
    {
        EorSummary summary = Summarise(
            Engagement(rate: 0.01m, rateOn: Today.AddDays(-200)),
            Engagement(rate: 0.01m, rateOn: Today));

        Assert.Equal(1, summary.StaleRateWorkers);
        Assert.Equal(2, summary.ConvertedWorkers);
    }

    [Fact]
    public void WithNothingEngagedEveryFigureIsZeroRatherThanAbsent()
    {
        EorSummary summary = Summarise();

        Assert.Equal(0, summary.Workers);
        Assert.Equal(0, summary.Countries);
        Assert.Equal(0m, summary.MonthlyCostUsd);
        Assert.Empty(summary.ByCurrency);

        // Nothing to convert is complete coverage, not a gap.
        Assert.True(summary.CostIsComplete);
    }

    // An engagement pointing at a provider that is gone contributes nothing
    // rather than throwing. The cost needs the provider's fee percentage, so
    // there is no total to be had.
    [Fact]
    public void AnEngagementWithNoProviderDoesNotBreakTheSum()
    {
        EorEngagement orphan = Engagement();
        orphan.ProviderId = Guid.NewGuid();

        EorSummary summary = Summarise(orphan);

        Assert.Empty(summary.ByCurrency);
        Assert.Equal(0m, summary.MonthlyCostUsd);
    }

    // ── compliance across the register ─────────────────────────────────────

    [Fact]
    public void WorkersWithSomethingBlockingAreCounted()
    {
        EorEngagement blocked = Engagement();
        EorEngagement clear = Engagement();

        EorSummary summary = EorRollups.Summarise(
            [blocked, clear],
            [_globalise],
            new Dictionary<Guid, IReadOnlyList<EorComplianceItem>>
            {
                [blocked.Id] = [],
                [clear.Id] = Complete(clear.Id),
            },
            Today);

        Assert.Equal(1, summary.WorkersWithBlockers);
    }

    [Fact]
    public void ExpiringAndExpiredRequirementsAreCountedSeparately()
    {
        EorEngagement engagement = Engagement();

        List<EorComplianceItem> items =
        [
            Item(engagement.Id, ComplianceKey.WorkPermit, expires: Today.AddDays(10)),
            Item(engagement.Id, ComplianceKey.LocalId, expires: Today.AddDays(-10)),
        ];

        EorSummary summary = EorRollups.Summarise(
            [engagement],
            [_globalise],
            new Dictionary<Guid, IReadOnlyList<EorComplianceItem>> { [engagement.Id] = items },
            Today);

        Assert.Equal(1, summary.ExpiringCompliance);
        Assert.Equal(1, summary.ExpiredCompliance);
    }

    // A worker who left in June is not an outstanding right-to-work risk.
    [Fact]
    public void AnEndedEngagementIsNotAComplianceRisk()
    {
        EorEngagement gone = Engagement(end: Today.AddDays(-30));

        EorSummary summary = EorRollups.Summarise(
            [gone],
            [_globalise],
            new Dictionary<Guid, IReadOnlyList<EorComplianceItem>> { [gone.Id] = [] },
            Today);

        Assert.Equal(0, summary.WorkersWithBlockers);
    }

    // ── a provider's headcount ─────────────────────────────────────────────

    // Counted, never stored. The screen keeps workerCount on the provider and
    // the number sits on the card next to the list it disagrees with.
    [Fact]
    public void AProvidersHeadcountIsCounted()
    {
        List<EorEngagement> engagements =
        [
            Engagement(),
            Engagement(),
            Engagement(provider: _retired),
        ];

        Assert.Equal(2, EorRollups.WorkersFor(engagements, _globalise.Id, Today));
        Assert.Equal(1, EorRollups.WorkersFor(engagements, _retired.Id, Today));
    }

    [Fact]
    public void EndingAnEngagementDropsTheProvidersHeadcount()
    {
        List<EorEngagement> engagements =
        [
            Engagement(),
            Engagement(end: Today.AddDays(-1)),
        ];

        Assert.Equal(1, EorRollups.WorkersFor(engagements, _globalise.Id, Today));
    }

    private static List<EorComplianceItem> Complete(Guid engagementId) =>
        [.. ComplianceCatalogue.All.Select(requirement => Item(engagementId, requirement.Key))];

    private static EorComplianceItem Item(
        Guid engagementId,
        ComplianceKey key,
        DateOnly? expires = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EngagementId = engagementId,
            Key = key,
            State = ComplianceState.Complete,
            ExpiresOn = expires,
        };
}
