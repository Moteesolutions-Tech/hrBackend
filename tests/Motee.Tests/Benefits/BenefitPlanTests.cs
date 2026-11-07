using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Domain.Benefits;
using Motee.Domain.Common;
using Motee.Domain.Organisation;

namespace Motee.Tests.Benefits;

public class BenefitPlanTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static BenefitPlan Plan(
        string name = "Group Health Insurance",
        string description = "In-patient and out-patient medical cover.",
        BenefitPlanStatus status = BenefitPlanStatus.Active,
        BenefitScope scope = BenefitScope.Everyone,
        IEnumerable<EmploymentType>? types = null,
        BenefitEnrollment enrollment = BenefitEnrollment.Core,
        int? waitingPeriodDays = null,
        decimal? employerPercent = null,
        decimal? employeePercent = null,
        CountryCode? country = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            Category = BenefitCategory.Health,
            Status = status,
            Scope = scope,
            EmploymentTypes = [.. types ?? []],
            Enrollment = enrollment,
            WaitingPeriodDays = waitingPeriodDays,
            EmployerContributionPercent = employerPercent,
            EmployeeContributionPercent = employeePercent,
            Country = country,
        };

    // ── the taxonomy the frontend reads ─────────────────────────────────────

    [Fact]
    public void CoversTheFrontendsCategories()
    {
        Assert.Equal(8, Enum.GetValues<BenefitCategory>().Length);
    }

    [Theory]
    [InlineData(BenefitCategory.Health, "\"health\"")]
    [InlineData(BenefitCategory.Retirement, "\"retirement\"")]
    [InlineData(BenefitCategory.Development, "\"development\"")]
    [InlineData(BenefitPlanStatus.Archived, "\"archived\"")]
    [InlineData(BenefitEnrollment.Optional, "\"optional\"")]
    public void SerialisesAsCamelCase(object value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Web));
    }

    // ── what the form will not accept ───────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("H")]
    public void RefusesANameTooShortToMeanAnything(string name)
    {
        Assert.Equal(BenefitPlanRefusal.NameRequired, BenefitPlans.Refusal(Plan(name: name)));
    }

    [Fact]
    public void RefusesANameLongerThanTheFormAllows()
    {
        Assert.Equal(
            BenefitPlanRefusal.NameTooLong,
            BenefitPlans.Refusal(Plan(name: new string('a', BenefitPlans.LongestName + 1))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("HMO")]
    public void RefusesADescriptionTooShortToExplainTheBenefit(string description)
    {
        Assert.Equal(
            BenefitPlanRefusal.DescriptionRequired,
            BenefitPlans.Refusal(Plan(description: description)));
    }

    [Fact]
    public void RefusesADescriptionLongerThanTheFormAllows()
    {
        Assert.Equal(
            BenefitPlanRefusal.DescriptionTooLong,
            BenefitPlans.Refusal(Plan(description: new string('a', BenefitPlans.LongestDescription + 1))));
    }

    [Theory]
    [InlineData(-1d, null)]
    [InlineData(null, -0.5d)]
    [InlineData(101d, null)]
    [InlineData(null, 120d)]
    public void RefusesAContributionThatIsNotAPercentage(double? employer, double? employee)
    {
        Assert.Equal(
            BenefitPlanRefusal.ContributionOutOfRange,
            BenefitPlans.Refusal(Plan(
                employerPercent: (decimal?)employer,
                employeePercent: (decimal?)employee)));
    }

    // 10% employer / 8% employee is Nigeria's statutory pension split, and the
    // two sides are percentages of the same salary, so a combined 18% is
    // ordinary rather than a figure to question.
    [Fact]
    public void AcceptsTwoContributionsThatTogetherExceedNeitherSide()
    {
        Assert.Equal(
            BenefitPlanRefusal.None,
            BenefitPlans.Refusal(Plan(employerPercent: 10m, employeePercent: 8m)));
    }

    [Fact]
    public void RefusesANegativeWaitingPeriod()
    {
        Assert.Equal(
            BenefitPlanRefusal.WaitingPeriodNegative,
            BenefitPlans.Refusal(Plan(waitingPeriodDays: -1)));
    }

    // A typed 9000 meant something other than days.
    [Fact]
    public void RefusesAWaitingPeriodLongerThanAnyRealScheme()
    {
        Assert.Equal(
            BenefitPlanRefusal.WaitingPeriodTooLong,
            BenefitPlans.Refusal(Plan(waitingPeriodDays: BenefitPlans.LongestWaitingPeriodDays + 1)));
    }

    [Fact]
    public void AcceptsNoWaitingPeriodAtAll()
    {
        Assert.Equal(BenefitPlanRefusal.None, BenefitPlans.Refusal(Plan(waitingPeriodDays: null)));
    }

    // The frontend quietly turns "restricted to employment types, none ticked"
    // into "every employee" — which grants the company's HMO to its interns
    // and freelancers on a forgotten checkbox. Refusing is the only safe
    // direction: widening entitlement should never be the fallback.
    [Fact]
    public void RefusesARestrictedScopeWithNoEmploymentTypesTicked()
    {
        Assert.Equal(
            BenefitPlanRefusal.ScopeHasNoEmploymentTypes,
            BenefitPlans.Refusal(Plan(scope: BenefitScope.EmploymentTypes, types: [])));
    }

    [Fact]
    public void AcceptsARestrictedScopeWithOneTypeTicked()
    {
        Assert.Equal(
            BenefitPlanRefusal.None,
            BenefitPlans.Refusal(Plan(
                scope: BenefitScope.EmploymentTypes,
                types: [EmploymentType.FullTime])));
    }

    // ── who a plan reaches ──────────────────────────────────────────────────

    [Fact]
    public void AnEveryoneScopeCoversEveryEmploymentType()
    {
        BenefitPlan plan = Plan();

        Assert.All(
            Enum.GetValues<EmploymentType>(),
            type => Assert.True(BenefitPlans.CoversType(plan, type)));
    }

    [Fact]
    public void ARestrictedScopeCoversOnlyTheTypesListed()
    {
        BenefitPlan plan = Plan(
            scope: BenefitScope.EmploymentTypes,
            types: [EmploymentType.FullTime, EmploymentType.Remote]);

        Assert.True(BenefitPlans.CoversType(plan, EmploymentType.Remote));
        Assert.False(BenefitPlans.CoversType(plan, EmploymentType.Internship));
    }

    // A draft is what the catalogue looks like before anybody has confirmed
    // the company actually offers it. Resolving it into an entitlement would
    // tell an employee they have cover nobody has bought.
    [Theory]
    [InlineData(BenefitPlanStatus.Draft)]
    [InlineData(BenefitPlanStatus.Archived)]
    public void OnlyAnActivePlanIsAnEntitlement(BenefitPlanStatus status)
    {
        Assert.False(BenefitPlans.IsEligible(Plan(status: status), EmploymentType.FullTime));
    }

    [Fact]
    public void AnActivePlanInScopeIsAnEntitlement()
    {
        Assert.True(BenefitPlans.IsEligible(Plan(), EmploymentType.Casual));
    }

    // The frontend's employmentTypeFromName falls back to full_time for an
    // unset type, which hands a company's full-time package to everybody whose
    // record nobody finished. Nothing is eligible until the type is recorded.
    [Fact]
    public void SomebodyWithNoRecordedEmploymentTypeIsEligibleForNothing()
    {
        Assert.False(BenefitPlans.IsEligible(Plan(), null));
    }

    // ── the country catalogue ───────────────────────────────────────────────

    [Fact]
    public void APlanWithNoCountryAppliesEverywhere()
    {
        BenefitPlan plan = Plan(country: null);

        Assert.True(BenefitPlans.AppliesInCountry(plan, CountryCode.Nigeria));
        Assert.True(BenefitPlans.AppliesInCountry(plan, CountryCode.UnitedKingdom));
    }

    [Fact]
    public void ACountryPlanAppliesOnlyThere()
    {
        BenefitPlan plan = Plan(country: CountryCode.Nigeria);

        Assert.True(BenefitPlans.AppliesInCountry(plan, CountryCode.Nigeria));
        Assert.False(BenefitPlans.AppliesInCountry(plan, CountryCode.UnitedKingdom));
    }

    // ── waiting periods ─────────────────────────────────────────────────────

    [Fact]
    public void NoWaitingPeriodMeansInForceFromTheStartDate()
    {
        BenefitPlan plan = Plan(waitingPeriodDays: null);

        Assert.Null(BenefitPlans.EffectiveFrom(plan, new DateOnly(2026, 3, 2)));
        Assert.Equal(
            BenefitEntitlementState.Entitled,
            Entitlements.StateOf(plan, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 2)));
    }

    [Fact]
    public void AWaitingPeriodCountsForwardFromTheStartDate()
    {
        BenefitPlan plan = Plan(waitingPeriodDays: 90);

        Assert.Equal(
            new DateOnly(2026, 5, 31),
            BenefitPlans.EffectiveFrom(plan, new DateOnly(2026, 3, 2)));
    }

    [Fact]
    public void SomebodyPastTheWaitingPeriodIsEntitled()
    {
        BenefitPlan plan = Plan(waitingPeriodDays: 90);

        Assert.Equal(
            BenefitEntitlementState.Entitled,
            Entitlements.StateOf(plan, new DateOnly(2026, 3, 2), new DateOnly(2026, 5, 31)));
    }

    [Fact]
    public void SomebodyStillInsideTheWaitingPeriodIsWaiting()
    {
        BenefitPlan plan = Plan(waitingPeriodDays: 90);

        Assert.Equal(
            BenefitEntitlementState.Waiting,
            Entitlements.StateOf(plan, new DateOnly(2026, 3, 2), new DateOnly(2026, 5, 30)));
    }

    // Reporting "entitled" would send somebody to a hospital that will turn
    // them away; reporting "waiting" would hide cover they may well have. The
    // honest answer is that the start date is missing.
    [Fact]
    public void AWaitingPeriodWithNoStartDateCannotBeResolved()
    {
        Assert.Equal(
            BenefitEntitlementState.UnknownStart,
            Entitlements.StateOf(Plan(waitingPeriodDays: 90), null, new DateOnly(2026, 5, 30)));
    }

    [Fact]
    public void NoStartDateAndNoWaitingPeriodIsStillEntitled()
    {
        Assert.Equal(
            BenefitEntitlementState.Entitled,
            Entitlements.StateOf(Plan(waitingPeriodDays: null), null, new DateOnly(2026, 5, 30)));
    }

    // ── what can be deleted ─────────────────────────────────────────────────

    [Fact]
    public void ACataloguePlanIsArchivedRatherThanDeleted()
    {
        BenefitPlan plan = Plan();
        plan.SeedKey = "ng-hmo";

        Assert.False(BenefitPlans.CanDelete(plan));
    }

    [Fact]
    public void APlanTheCompanyWroteItselfCanBeDeleted()
    {
        Assert.True(BenefitPlans.CanDelete(Plan()));
    }

    // ── how far the catalogue reaches ───────────────────────────────────────

    [Fact]
    public void ReachCountsNobodyWhenThereAreNoEmployees()
    {
        BenefitReachTally tally = BenefitReach.Of([Plan()], new Dictionary<EmploymentType, int>(), 0);

        Assert.Equal(0, tally.Employees);
        Assert.Equal(0, tally.Covered);
        Assert.Equal(0m, tally.AveragePlansPerEmployee);
    }

    [Fact]
    public void ReachAveragesOverTheWholeWorkforce()
    {
        // Two plans for full-timers, one of them company-wide. Four full-time
        // and one casual: (4 × 2 + 1 × 1) / 5 = 1.8.
        List<BenefitPlan> plans =
        [
            Plan(),
            Plan(scope: BenefitScope.EmploymentTypes, types: [EmploymentType.FullTime]),
        ];

        BenefitReachTally tally = BenefitReach.Of(
            plans,
            new Dictionary<EmploymentType, int>
            {
                [EmploymentType.FullTime] = 4,
                [EmploymentType.Casual] = 1,
            },
            0);

        Assert.Equal(5, tally.Employees);
        Assert.Equal(5, tally.Covered);
        Assert.Equal(1.8m, tally.AveragePlansPerEmployee);
    }

    [Fact]
    public void ReachRoundsTheAverageToOneDecimal()
    {
        BenefitReachTally tally = BenefitReach.Of(
            [Plan(scope: BenefitScope.EmploymentTypes, types: [EmploymentType.FullTime])],
            new Dictionary<EmploymentType, int>
            {
                [EmploymentType.FullTime] = 1,
                [EmploymentType.Casual] = 2,
            },
            0);

        Assert.Equal(0.3m, tally.AveragePlansPerEmployee);
    }

    // The figure the hub shows in amber: an employment type the company gives
    // nothing automatic to.
    [Fact]
    public void ReachCountsWhoGetsNoCoreBenefitAtAll()
    {
        List<BenefitPlan> plans =
        [
            Plan(scope: BenefitScope.EmploymentTypes, types: [EmploymentType.FullTime]),
            Plan(enrollment: BenefitEnrollment.Optional),
        ];

        BenefitReachTally tally = BenefitReach.Of(
            plans,
            new Dictionary<EmploymentType, int>
            {
                [EmploymentType.FullTime] = 3,
                [EmploymentType.Internship] = 2,
            },
            0);

        // The interns reach the optional plan, so they are covered — but
        // nothing lands on them automatically.
        Assert.Equal(5, tally.Covered);
        Assert.Equal(2, tally.WithoutCoreCover);
    }

    [Fact]
    public void ReachLeavesOutAnEmploymentTypeNobodyRecorded()
    {
        BenefitReachTally tally = BenefitReach.Of(
            [Plan()],
            new Dictionary<EmploymentType, int> { [EmploymentType.FullTime] = 3 },
            withoutEmploymentType: 2);

        // Counting the two as full-time would hand them a package nobody
        // decided they get, and would move the average while it did so.
        Assert.Equal(3, tally.Employees);
        Assert.Equal(3, tally.Covered);
        Assert.Equal(2, tally.WithoutEmploymentType);
        Assert.Equal(1m, tally.AveragePlansPerEmployee);
    }

    [Fact]
    public void ReachIgnoresPlansThatAreNotActive()
    {
        BenefitReachTally tally = BenefitReach.Of(
            [Plan(status: BenefitPlanStatus.Draft), Plan(status: BenefitPlanStatus.Archived)],
            new Dictionary<EmploymentType, int> { [EmploymentType.FullTime] = 3 },
            0);

        Assert.Equal(0, tally.Covered);
        Assert.Equal(3, tally.WithoutCoreCover);
    }

    // ── the catalogue a new tenant starts with ──────────────────────────────

    [Fact]
    public void EveryCataloguePlanHasADistinctKey()
    {
        List<string> keys = [.. BenefitPlanSeeds.All.Select(seed => seed.Key)];

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryCataloguePlanPassesItsOwnValidation()
    {
        Assert.All(BenefitPlanSeeds.All, seed =>
        {
            BenefitPlan plan = Plan(
                name: seed.Name,
                description: seed.Description,
                scope: seed.Scope,
                types: seed.EmploymentTypes,
                enrollment: seed.Enrollment,
                country: seed.Country);

            Assert.Equal(BenefitPlanRefusal.None, BenefitPlans.Refusal(plan));
        });
    }

    [Fact]
    public void ARestrictedCataloguePlanNamesAtLeastOneType()
    {
        Assert.All(
            BenefitPlanSeeds.All.Where(seed => seed.Scope == BenefitScope.EmploymentTypes),
            seed => Assert.NotEmpty(seed.EmploymentTypes));
    }

    [Fact]
    public void AnEveryoneCataloguePlanNamesNoTypes()
    {
        Assert.All(
            BenefitPlanSeeds.All.Where(seed => seed.Scope == BenefitScope.Everyone),
            seed => Assert.Empty(seed.EmploymentTypes));
    }

    [Theory]
    [InlineData("ng")]
    [InlineData("gb")]
    public void EachCountryStartsWithACatalogueWorthReading(string code)
    {
        IReadOnlyList<BenefitPlanSeed> forCountry = BenefitPlanSeeds.For(CountryCode.Parse(code));

        Assert.NotEmpty(forCountry);
        Assert.All(forCountry, seed =>
            Assert.True(seed.Country is null || seed.Country == CountryCode.Parse(code)));
    }

    // A Nigerian company has no use for a cycle-to-work scheme, and seeding
    // one leaves twenty rows the tenant has to archive before their own
    // catalogue is readable.
    [Fact]
    public void NeitherCountryGetsTheOthersPlans()
    {
        IReadOnlyList<BenefitPlanSeed> nigeria = BenefitPlanSeeds.For(CountryCode.Nigeria);
        IReadOnlyList<BenefitPlanSeed> kingdom = BenefitPlanSeeds.For(CountryCode.UnitedKingdom);

        Assert.DoesNotContain(nigeria, seed => seed.Country == CountryCode.UnitedKingdom);
        Assert.DoesNotContain(kingdom, seed => seed.Country == CountryCode.Nigeria);

        // The country-less plans are in both.
        Assert.Contains(nigeria, seed => seed.Country is null);
        Assert.Contains(kingdom, seed => seed.Country is null);
    }

    [Fact]
    public void TheDefaultCoverageLevelsAreTheThreeTheFormOffers()
    {
        Assert.Equal(
            ["Employee Only", "Married Cover", "Family Cover"],
            BenefitPlanSeeds.CoverageLevels);
    }
}
