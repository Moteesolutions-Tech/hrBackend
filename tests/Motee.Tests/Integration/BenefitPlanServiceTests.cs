using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Benefits;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Benefits;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The benefits catalogue, out of the database.
//
// What these prove is that nothing about an entitlement is stored against a
// person: widening a plan reaches everybody immediately, a draft reaches
// nobody, and somebody whose employment type nobody recorded reaches nothing
// at all rather than quietly collecting the full-time package.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class BenefitPlanServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(new DateOnly(2026, 11, 3).ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;

    private async Task<ServiceProvider> ArrangeAsync(string country = "NG")
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = country,
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IBenefitPlanService Benefits(ServiceProvider provider) =>
        Resolve<IBenefitPlanService>(provider);

    private static BenefitPlanRequest Request(
        string name = "Creche Subsidy",
        string description = "A contribution towards registered childcare.",
        BenefitCategory category = BenefitCategory.Wellness,
        BenefitPlanStatus status = BenefitPlanStatus.Draft,
        BenefitScope scope = BenefitScope.Everyone,
        IReadOnlyList<EmploymentType>? types = null,
        BenefitEnrollment enrollment = BenefitEnrollment.Core,
        string? coverage = null,
        int? waitingPeriodDays = null,
        string? country = null) => new()
        {
            Name = name,
            Description = description,
            Category = category,
            Status = status,
            Scope = scope,
            EmploymentTypes = types,
            Enrollment = enrollment,
            Coverage = coverage,
            WaitingPeriodDays = waitingPeriodDays,
            Country = country,
        };

    private static async Task<BenefitPlanDto> CreatedAsync(
        ServiceProvider provider,
        BenefitPlanRequest request)
    {
        BenefitPlanResult result = await Benefits(provider).CreateAsync(request);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Plan!;
    }

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        EmploymentType type = EmploymentType.FullTime,
        DateOnly? startDate = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = type,
            Status = EmployeeStatus.Active,
            StartDate = startDate ?? new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    // The create endpoint requires an employment type, so somebody without one
    // has to be seeded the way a part-finished import leaves them.
    private async Task<Guid> HireWithNoEmploymentTypeAsync(string first)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Employee employee = new()
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            FirstName = first,
            LastName = "Adeyemi",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            EmploymentType = null,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        seed.Employees.Add(employee);

        await seed.SaveChangesAsync();

        return employee.Id;
    }

    private static async Task<BenefitPlanDto> ActivateAsync(
        ServiceProvider provider,
        Guid planId)
    {
        BenefitPlanResult result = await Benefits(provider)
            .SetStatusAsync(planId, BenefitPlanStatus.Active);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Plan!;
    }

    // ── the catalogue a tenant starts with ──────────────────────────────────

    [SkippableFact]
    public async Task SeedsTheCountrysCatalogueAndNotTheOthers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync("NG");

        BenefitPlanListDto listed = await Benefits(provider)
            .ListAsync(new BenefitPlanQuery { PageSize = 200 });

        Assert.Equal(
            BenefitPlanSeeds.For(Motee.Domain.Common.CountryCode.Nigeria).Count,
            listed.Plans.TotalItems);

        Assert.Contains(listed.Plans.Items, plan => plan.SeedKey == "ng-hmo");
        Assert.Contains(listed.Plans.Items, plan => plan.SeedKey == "annual-leave");

        // A Nigerian company has no use for a cycle-to-work scheme, and twenty
        // rows about salary sacrifice have to be archived before their own
        // catalogue is readable.
        Assert.DoesNotContain(listed.Plans.Items, plan => plan.SeedKey == "uk-cycle-to-work");
    }

    [SkippableFact]
    public async Task SeedsTheUnitedKingdomCatalogueForAUkTenant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync("GB");

        BenefitPlanListDto listed = await Benefits(provider)
            .ListAsync(new BenefitPlanQuery { PageSize = 200 });

        Assert.Contains(listed.Plans.Items, plan => plan.SeedKey == "uk-pension");
        Assert.Contains(listed.Plans.Items, plan => plan.SeedKey == "annual-leave");
        Assert.DoesNotContain(listed.Plans.Items, plan => plan.SeedKey == "ng-hmo");
    }

    // The whole catalogue arrives as a draft. An HMO row that arrived active,
    // naming an insurer and a 90-day qualifying period, would tell a new
    // joiner they have cover the company has not bought.
    [SkippableFact]
    public async Task SeedsEveryPlanAsADraftWithNoTermsFilledIn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanListDto listed = await Benefits(provider)
            .ListAsync(new BenefitPlanQuery { PageSize = 200 });

        Assert.All(listed.Plans.Items, plan =>
        {
            Assert.Equal(BenefitPlanStatus.Draft, plan.Status);
            Assert.Null(plan.Provider);
            Assert.Null(plan.EmployerContributionPercent);
            Assert.Null(plan.EmployeeContributionPercent);
            Assert.Null(plan.WaitingPeriodDays);
            Assert.Null(plan.CoverageLevelId);

            // Nothing is an entitlement yet, which is the truthful figure.
            Assert.Equal(0, plan.EligibleEmployees);
        });
    }

    [SkippableFact]
    public async Task SeedsTheThreeCoverageLevelsTheFormOffers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<BenefitCoverageLevelDto> levels =
            await Benefits(provider).CoverageLevelsAsync();

        Assert.Equal(
            ["Employee Only", "Married Cover", "Family Cover"],
            levels.Select(level => level.Name));

        Assert.All(levels, level => Assert.Equal(0, level.PlanCount));
    }

    [SkippableFact]
    public async Task ACataloguePlanCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanDto seeded = (await Benefits(provider)
            .ListAsync(new BenefitPlanQuery { PageSize = 200 }))
            .Plans.Items.First(plan => plan.SeedKey == "ng-hmo");

        Assert.False(seeded.CanDelete);

        BenefitPlanResult result = await Benefits(provider).DeleteAsync(seeded.Id);

        Assert.Equal(BenefitPlanOutcome.Refused, result.Outcome);
        Assert.Equal(BenefitPlanRefusal.CataloguePlanCannotBeDeleted, result.Refusal);
    }

    [SkippableFact]
    public async Task ACataloguePlanCanBeArchivedInstead()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanDto seeded = (await Benefits(provider)
            .ListAsync(new BenefitPlanQuery { PageSize = 200 }))
            .Plans.Items.First(plan => plan.SeedKey == "ng-meal-subsidy");

        BenefitPlanResult result = await Benefits(provider)
            .SetStatusAsync(seeded.Id, BenefitPlanStatus.Archived);

        Assert.True(result.Succeeded);
        Assert.Equal(BenefitPlanStatus.Archived, result.Plan!.Status);
    }

    // ── creating and editing ────────────────────────────────────────────────

    [SkippableFact]
    public async Task CreatesAPlanTheCompanyWroteItself()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanDto plan = await CreatedAsync(provider, Request());

        Assert.Null(plan.SeedKey);
        Assert.True(plan.CanDelete);
        Assert.Equal(BenefitPlanStatus.Draft, plan.Status);
        Assert.Equal("Creche Subsidy", plan.Name);
    }

    [SkippableFact]
    public async Task DeletesAPlanTheCompanyWroteItself()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanDto plan = await CreatedAsync(provider, Request());

        Assert.True((await Benefits(provider).DeleteAsync(plan.Id)).Succeeded);
        Assert.Null(await Benefits(provider).GetAsync(plan.Id));
    }

    // Widening entitlement should never be the fallback for a forgotten
    // checkbox — the frontend turns this into "every employee", which hands
    // the company's HMO to its interns.
    [SkippableFact]
    public async Task RefusesARestrictedPlanWithNoEmploymentTypes()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanResult result = await Benefits(provider).CreateAsync(
            Request(scope: BenefitScope.EmploymentTypes, types: []));

        Assert.Equal(BenefitPlanRefusal.ScopeHasNoEmploymentTypes, result.Refusal);
    }

    [SkippableFact]
    public async Task RefusesACoverageLabelThatIsNotOneOfTheTenantsLevels()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanResult result = await Benefits(provider)
            .CreateAsync(Request(coverage: "Platinum Cover"));

        Assert.Equal(BenefitPlanRefusal.CoverageLevelUnknown, result.Refusal);
    }

    [SkippableFact]
    public async Task AcceptsACoverageLabelFromTheTenantsOwnLevels()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanDto plan = await CreatedAsync(provider, Request(coverage: "family cover"));

        // Matched without regard to case, and reported back as the level spells
        // it rather than as the caller typed it.
        Assert.Equal("Family Cover", plan.Coverage);
        Assert.NotNull(plan.CoverageLevelId);
    }

    // A Nigerian company has no business defining a UK scheme: the tenant is
    // the country, and a plan filed under another one would be invisible on
    // every screen that reads it.
    [SkippableFact]
    public async Task RefusesAPlanForACountryThisCompanyDoesNotOperateIn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync("NG");

        BenefitPlanResult result = await Benefits(provider).CreateAsync(Request(country: "GB"));

        Assert.Equal(BenefitPlanRefusal.CountryNotThisTenants, result.Refusal);
    }

    [SkippableFact]
    public async Task UpdatesEveryFieldTheFormCarries()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanDto plan = await CreatedAsync(provider, Request());

        BenefitPlanResult result = await Benefits(provider).UpdateAsync(plan.Id, new BenefitPlanRequest
        {
            Name = "Childcare Support",
            Description = "A monthly contribution towards registered childcare.",
            Category = BenefitCategory.Financial,
            Provider = "Acme Childcare Partners",
            Coverage = "Married Cover",
            CoverageDetails = "Up to two children under six",
            EmployerContributionPercent = 60m,
            EmployeeContributionPercent = 40m,
            CostNote = "Split with the employee",
            WaitingPeriodDays = 180,
            Scope = BenefitScope.EmploymentTypes,
            EmploymentTypes = [EmploymentType.FullTime, EmploymentType.Remote],
            Status = BenefitPlanStatus.Active,
            Enrollment = BenefitEnrollment.Optional,
        });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        BenefitPlanDto saved = result.Plan!;

        Assert.Equal("Childcare Support", saved.Name);
        Assert.Equal(BenefitCategory.Financial, saved.Category);
        Assert.Equal("Acme Childcare Partners", saved.Provider);
        Assert.Equal("Married Cover", saved.Coverage);
        Assert.Equal(60m, saved.EmployerContributionPercent);
        Assert.Equal(180, saved.WaitingPeriodDays);
        Assert.Equal(BenefitScope.EmploymentTypes, saved.Scope);
        Assert.Equal(
            [EmploymentType.FullTime, EmploymentType.Remote],
            saved.EmploymentTypes);
        Assert.Equal(BenefitEnrollment.Optional, saved.Enrollment);
        Assert.Equal(BenefitPlanStatus.Active, saved.Status);
    }

    [SkippableFact]
    public async Task ClearsACoverageLevelWhenTheFormSendsNone()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanDto plan = await CreatedAsync(provider, Request(coverage: "Family Cover"));

        BenefitPlanResult result = await Benefits(provider)
            .UpdateAsync(plan.Id, Request(coverage: null));

        Assert.True(result.Succeeded);
        Assert.Null(result.Plan!.CoverageLevelId);
        Assert.Null(result.Plan.Coverage);
    }

    // Duplicating an active plan into another active plan would double
    // everybody's entitlement on a misclick.
    [SkippableFact]
    public async Task DuplicatesAsADraftCopy()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanDto plan = await CreatedAsync(provider, Request(status: BenefitPlanStatus.Active));

        BenefitPlanResult result = await Benefits(provider).DuplicateAsync(plan.Id);

        Assert.True(result.Succeeded);
        Assert.NotEqual(plan.Id, result.Plan!.Id);
        Assert.Equal(BenefitPlanStatus.Draft, result.Plan.Status);
        Assert.Equal("Creche Subsidy (Copy)", result.Plan.Name);

        // A copy of a catalogue plan belongs to the tenant, so it can be
        // deleted where the original could not.
        Assert.True(result.Plan.CanDelete);
    }

    [SkippableFact]
    public async Task DuplicatingACataloguePlanLeavesTheOriginalAlone()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanDto seeded = (await Benefits(provider)
            .ListAsync(new BenefitPlanQuery { PageSize = 200 }))
            .Plans.Items.First(plan => plan.SeedKey == "ng-hmo");

        BenefitPlanResult copy = await Benefits(provider).DuplicateAsync(seeded.Id);

        Assert.True(copy.Succeeded);
        Assert.Null(copy.Plan!.SeedKey);
        Assert.False((await Benefits(provider).GetAsync(seeded.Id))!.CanDelete);
    }

    // ── who a plan reaches ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task CountsOnlyTheEmployeesAnActivePlanReaches()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ngozi", EmploymentType.FullTime);
        await HireAsync(provider, "Tunde", EmploymentType.FullTime);
        await HireAsync(provider, "Bisi", EmploymentType.Internship);

        BenefitPlanDto restricted = await CreatedAsync(provider, Request(
            name: "Study Leave",
            scope: BenefitScope.EmploymentTypes,
            types: [EmploymentType.FullTime],
            status: BenefitPlanStatus.Active));

        Assert.Equal(2, (await Benefits(provider).GetAsync(restricted.Id))!.EligibleEmployees);
    }

    // Nothing is stored per employee, so widening the scope reaches everybody
    // the moment it is saved.
    [SkippableFact]
    public async Task WideningAPlanReachesEverybodyImmediately()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ngozi", EmploymentType.FullTime);
        await HireAsync(provider, "Bisi", EmploymentType.Internship);

        BenefitPlanDto plan = await CreatedAsync(provider, Request(
            scope: BenefitScope.EmploymentTypes,
            types: [EmploymentType.FullTime],
            status: BenefitPlanStatus.Active));

        Assert.Equal(1, plan.EligibleEmployees);

        BenefitPlanResult widened = await Benefits(provider).UpdateAsync(plan.Id, Request(
            scope: BenefitScope.Everyone,
            status: BenefitPlanStatus.Active));

        Assert.Equal(2, widened.Plan!.EligibleEmployees);
    }

    [SkippableFact]
    public async Task ADraftPlanReachesNobody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ngozi");

        BenefitPlanDto plan = await CreatedAsync(provider, Request(status: BenefitPlanStatus.Draft));

        Assert.Equal(0, plan.EligibleEmployees);
        Assert.Equal(1, (await ActivateAsync(provider, plan.Id)).EligibleEmployees);
    }

    [SkippableFact]
    public async Task SomebodyWhoHasLeftIsNotCounted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid leaverId = await HireAsync(provider, "Ngozi");

        await using (MoteeDbContext update = fixture.CreateContext(_tenantId))
        {
            Employee leaver = await update.Employees.FirstAsync(row => row.Id == leaverId);

            leaver.Status = EmployeeStatus.Inactive;

            await update.SaveChangesAsync();
        }

        BenefitPlanDto plan = await CreatedAsync(provider, Request(status: BenefitPlanStatus.Active));

        Assert.Equal(0, plan.EligibleEmployees);
    }

    // ── the hub's figures ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task SummaryReportsWhatIsLiveAndHowFarItReaches()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ngozi", EmploymentType.FullTime);
        await HireAsync(provider, "Tunde", EmploymentType.FullTime);
        await HireAsync(provider, "Bisi", EmploymentType.Internship);

        await CreatedAsync(provider, Request(
            name: "Study Leave",
            scope: BenefitScope.EmploymentTypes,
            types: [EmploymentType.FullTime],
            status: BenefitPlanStatus.Active));

        await CreatedAsync(provider, Request(
            name: "Book Allowance",
            enrollment: BenefitEnrollment.Optional,
            status: BenefitPlanStatus.Active));

        BenefitSummaryDto summary = await Benefits(provider).SummaryAsync();

        Assert.Equal(2, summary.Active);
        Assert.Equal(1, summary.CoreBenefits);
        Assert.Equal(1, summary.OptionalBenefits);
        Assert.Equal(3, summary.Employees);
        Assert.Equal(0, summary.EmployeesWithoutEmploymentType);

        // Everybody reaches the company-wide optional plan.
        Assert.Equal(3, summary.CoveredEmployees);

        // Nothing automatic lands on the intern.
        Assert.Equal(1, summary.EmployeesWithoutCoreCover);

        // (2 × 2 + 1 × 1) / 3 = 1.666… → 1.7.
        Assert.Equal(1.7m, summary.AveragePlansPerEmployee);

        // Every seeded plan is still a draft, so the catalogue is large and
        // the live part of it is two.
        Assert.True(summary.Draft > 10, $"draft was {summary.Draft}");
    }

    // The frontend's employmentTypeFromName falls back to full_time, which
    // hands a company's full-time package to everybody whose record nobody
    // finished — and moves the average while it does so.
    [SkippableFact]
    public async Task SummaryReportsSomebodyWithNoEmploymentTypeSeparately()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ngozi", EmploymentType.FullTime);
        await HireWithNoEmploymentTypeAsync("Kemi");

        await CreatedAsync(provider, Request(status: BenefitPlanStatus.Active));

        BenefitSummaryDto summary = await Benefits(provider).SummaryAsync();

        Assert.Equal(1, summary.Employees);
        Assert.Equal(1, summary.EmployeesWithoutEmploymentType);
        Assert.Equal(1, summary.CoveredEmployees);
        Assert.Equal(1m, summary.AveragePlansPerEmployee);
    }

    [SkippableFact]
    public async Task SummaryCountsActivePlansByCategory()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await CreatedAsync(provider, Request(
            name: "Creche Subsidy",
            category: BenefitCategory.Wellness,
            status: BenefitPlanStatus.Active));

        await CreatedAsync(provider, Request(
            name: "Gym Subsidy",
            category: BenefitCategory.Wellness,
            status: BenefitPlanStatus.Active));

        await CreatedAsync(provider, Request(
            name: "Book Allowance",
            category: BenefitCategory.Development,
            status: BenefitPlanStatus.Draft));

        BenefitSummaryDto summary = await Benefits(provider).SummaryAsync();

        Assert.Equal(
            2,
            summary.ByCategory.Single(row => row.Category == BenefitCategory.Wellness).Count);

        Assert.DoesNotContain(
            summary.ByCategory,
            row => row.Category == BenefitCategory.Development);
    }

    // ── one person's entitlements ───────────────────────────────────────────

    [SkippableFact]
    public async Task SplitsAnEmployeesBenefitsIntoAutomaticAndOptional()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Ngozi", EmploymentType.FullTime);

        await CreatedAsync(provider, Request(
            name: "Group Health",
            category: BenefitCategory.Health,
            status: BenefitPlanStatus.Active));

        await CreatedAsync(provider, Request(
            name: "Gym Subsidy",
            enrollment: BenefitEnrollment.Optional,
            status: BenefitPlanStatus.Active));

        await CreatedAsync(provider, Request(name: "Not Live Yet"));

        EmployeeBenefitsDto? benefits = await Benefits(provider).ForEmployeeAsync(employeeId);

        Assert.NotNull(benefits);
        Assert.Equal(EmploymentType.FullTime, benefits.EmploymentType);
        Assert.Equal(["Group Health"], benefits.Core.Select(row => row.Plan.Name));
        Assert.Equal(["Gym Subsidy"], benefits.Optional.Select(row => row.Plan.Name));
    }

    [SkippableFact]
    public async Task LeavesOutAPlanThisEmploymentTypeIsNotInScopeFor()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Bisi", EmploymentType.Internship);

        await CreatedAsync(provider, Request(
            name: "Group Health",
            scope: BenefitScope.EmploymentTypes,
            types: [EmploymentType.FullTime],
            status: BenefitPlanStatus.Active));

        EmployeeBenefitsDto? benefits = await Benefits(provider).ForEmployeeAsync(employeeId);

        Assert.Empty(benefits!.Core);
        Assert.Empty(benefits.Optional);
    }

    [SkippableFact]
    public async Task SomebodyWithNoRecordedEmploymentTypeIsEntitledToNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireWithNoEmploymentTypeAsync("Kemi");

        await CreatedAsync(provider, Request(status: BenefitPlanStatus.Active));

        EmployeeBenefitsDto? benefits = await Benefits(provider).ForEmployeeAsync(employeeId);

        Assert.NotNull(benefits);
        Assert.Null(benefits.EmploymentType);
        Assert.Empty(benefits.Core);
    }

    // Reporting "entitled" would send somebody to a hospital that will turn
    // them away.
    [SkippableFact]
    public async Task ReportsWhenAWaitingPeriodHasNotBeenServedYet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        // The clock is pinned to 3 November 2026; ninety days from the start
        // date lands on 1 January 2027.
        Guid employeeId = await HireAsync(
            provider,
            "Ngozi",
            EmploymentType.FullTime,
            new DateOnly(2026, 10, 3));

        await CreatedAsync(provider, Request(
            name: "Group Health",
            status: BenefitPlanStatus.Active,
            waitingPeriodDays: 90));

        EmployeeBenefitsDto? benefits = await Benefits(provider).ForEmployeeAsync(employeeId);

        EmployeeBenefitDto entitlement = Assert.Single(benefits!.Core);

        Assert.Equal(BenefitEntitlementState.Waiting, entitlement.State);
        Assert.Equal(new DateOnly(2027, 1, 1), entitlement.EffectiveFrom);
    }

    [SkippableFact]
    public async Task ReportsAServedWaitingPeriodAsEntitled()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(
            provider,
            "Ngozi",
            EmploymentType.FullTime,
            new DateOnly(2024, 1, 10));

        await CreatedAsync(provider, Request(status: BenefitPlanStatus.Active, waitingPeriodDays: 90));

        EmployeeBenefitsDto? benefits = await Benefits(provider).ForEmployeeAsync(employeeId);

        Assert.Equal(BenefitEntitlementState.Entitled, Assert.Single(benefits!.Core).State);
    }

    [SkippableFact]
    public async Task ReportsAnUnknownStartDateRatherThanGuessing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Ngozi");

        await using (MoteeDbContext update = fixture.CreateContext(_tenantId))
        {
            Employee employee = await update.Employees.FirstAsync(row => row.Id == employeeId);

            employee.StartDate = null;

            await update.SaveChangesAsync();
        }

        await CreatedAsync(provider, Request(status: BenefitPlanStatus.Active, waitingPeriodDays: 90));

        EmployeeBenefitsDto? benefits = await Benefits(provider).ForEmployeeAsync(employeeId);

        EmployeeBenefitDto entitlement = Assert.Single(benefits!.Core);

        Assert.Equal(BenefitEntitlementState.UnknownStart, entitlement.State);
        Assert.Null(entitlement.EffectiveFrom);
    }

    [SkippableFact]
    public async Task ReportsNothingForSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Null(await Benefits(provider).ForEmployeeAsync(Guid.NewGuid()));
    }

    // ── coverage levels ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AddsACoverageLevel()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitCoverageLevelResult result = await Benefits(provider)
            .AddCoverageLevelAsync("  Family Plus  ");

        Assert.True(result.Succeeded);
        Assert.Equal("Family Plus", result.Level!.Name);
    }

    [SkippableFact]
    public async Task RefusesACoverageLevelAlreadyOnTheList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitCoverageLevelResult result = await Benefits(provider)
            .AddCoverageLevelAsync("family cover");

        Assert.Equal(BenefitPlanRefusal.CoverageLevelNameTaken, result.Refusal);
    }

    [SkippableFact]
    public async Task RefusesABlankCoverageLevel()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitCoverageLevelResult result = await Benefits(provider).AddCoverageLevelAsync("   ");

        Assert.Equal(BenefitPlanRefusal.CoverageLevelNameRequired, result.Refusal);
    }

    // The plan holds a reference rather than a copy of the label, so there is
    // nothing to carry across and nothing to drift.
    [SkippableFact]
    public async Task RenamingALevelCarriesEveryPlanOnIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanDto plan = await CreatedAsync(provider, Request(coverage: "Family Cover"));

        Guid levelId = plan.CoverageLevelId!.Value;

        Assert.True((await Benefits(provider)
            .RenameCoverageLevelAsync(levelId, "Household Cover")).Succeeded);

        Assert.Equal("Household Cover", (await Benefits(provider).GetAsync(plan.Id))!.Coverage);
    }

    [SkippableFact]
    public async Task RemovingALevelLeavesItsPlansWithNoCoverageLevel()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanDto plan = await CreatedAsync(provider, Request(coverage: "Family Cover"));

        Assert.Equal(
            BenefitPlanOutcome.Succeeded,
            await Benefits(provider).RemoveCoverageLevelAsync(plan.CoverageLevelId!.Value));

        BenefitPlanDto after = (await Benefits(provider).GetAsync(plan.Id))!;

        Assert.Null(after.CoverageLevelId);
        Assert.Null(after.Coverage);
    }

    // So the Manage dialog can say what removing a level will clear before it
    // clears it.
    [SkippableFact]
    public async Task CountsHowManyPlansAreOnEachLevel()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await CreatedAsync(provider, Request(name: "Creche Subsidy", coverage: "Family Cover"));
        await CreatedAsync(provider, Request(name: "Gym Subsidy", coverage: "Family Cover"));

        IReadOnlyList<BenefitCoverageLevelDto> levels =
            await Benefits(provider).CoverageLevelsAsync();

        Assert.Equal(2, levels.Single(level => level.Name == "Family Cover").PlanCount);
        Assert.Equal(0, levels.Single(level => level.Name == "Employee Only").PlanCount);
    }

    // ── the list ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task FiltersByStatus()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await CreatedAsync(provider, Request(name: "Creche Subsidy", status: BenefitPlanStatus.Active));

        BenefitPlanListDto listed = await Benefits(provider).ListAsync(
            new BenefitPlanQuery { Status = BenefitPlanStatus.Active, PageSize = 200 });

        Assert.Equal(["Creche Subsidy"], listed.Plans.Items.Select(plan => plan.Name));
    }

    [SkippableFact]
    public async Task FiltersByEnrollment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await CreatedAsync(provider, Request(
            name: "Gym Subsidy",
            enrollment: BenefitEnrollment.Optional,
            status: BenefitPlanStatus.Active));

        BenefitPlanListDto listed = await Benefits(provider).ListAsync(new BenefitPlanQuery
        {
            Enrollment = BenefitEnrollment.Optional,
            Status = BenefitPlanStatus.Active,
            PageSize = 200,
        });

        Assert.Equal(["Gym Subsidy"], listed.Plans.Items.Select(plan => plan.Name));
    }

    [SkippableFact]
    public async Task SearchesNameProviderAndCoverageDetails()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await CreatedAsync(provider, new BenefitPlanRequest
        {
            Name = "Childcare Support",
            Description = "A monthly contribution towards registered childcare.",
            Category = BenefitCategory.Wellness,
            Provider = "Bright Beginnings",
            CoverageDetails = "Up to two children under six",
            Scope = BenefitScope.Everyone,
        });

        foreach (string term in (string[])["childcare", "BRIGHT", "two children"])
        {
            BenefitPlanListDto listed = await Benefits(provider).ListAsync(
                new BenefitPlanQuery { Search = term, PageSize = 200 });

            Assert.Contains(listed.Plans.Items, plan => plan.Name == "Childcare Support");
        }
    }

    // A plan legitimately named "Contract (100%)" would otherwise match every
    // other plan in the catalogue.
    [SkippableFact]
    public async Task DoesNotTreatASearchTermAsAWildcard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await CreatedAsync(provider, Request(name: "Contract (100%)"));

        BenefitPlanListDto listed = await Benefits(provider).ListAsync(
            new BenefitPlanQuery { Search = "100%", PageSize = 200 });

        Assert.Equal(["Contract (100%)"], listed.Plans.Items.Select(plan => plan.Name));
    }

    [SkippableFact]
    public async Task FiltersToPlansWithNoCoverageLevel()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await CreatedAsync(provider, Request(name: "Creche Subsidy", coverage: "Family Cover"));
        await CreatedAsync(provider, Request(name: "Gym Subsidy"));

        BenefitPlanListDto listed = await Benefits(provider).ListAsync(new BenefitPlanQuery
        {
            WithoutCoverageLevel = true,
            Category = BenefitCategory.Wellness,
            PageSize = 200,
        });

        Assert.Contains(listed.Plans.Items, plan => plan.Name == "Gym Subsidy");
        Assert.DoesNotContain(listed.Plans.Items, plan => plan.Name == "Creche Subsidy");
    }

    // Picking a category must not empty the rail that offered the choice, so
    // the counts are taken before the category filter is applied.
    [SkippableFact]
    public async Task CategoryCountsIgnoreTheCategoryFilter()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await CreatedAsync(provider, Request(
            name: "Creche Subsidy",
            category: BenefitCategory.Wellness,
            status: BenefitPlanStatus.Active));

        await CreatedAsync(provider, Request(
            name: "Book Allowance",
            category: BenefitCategory.Development,
            status: BenefitPlanStatus.Active));

        BenefitPlanListDto listed = await Benefits(provider).ListAsync(new BenefitPlanQuery
        {
            Status = BenefitPlanStatus.Active,
            Category = BenefitCategory.Wellness,
            PageSize = 200,
        });

        Assert.Equal(["Creche Subsidy"], listed.Plans.Items.Select(plan => plan.Name));

        Assert.Equal(
            1,
            listed.CategoryCounts.Single(row => row.Category == BenefitCategory.Development).Count);
    }

    [SkippableFact]
    public async Task PagesTheList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        BenefitPlanListDto listed = await Benefits(provider)
            .ListAsync(new BenefitPlanQuery { Page = 1, PageSize = 5 });

        Assert.Equal(5, listed.Plans.Items.Count);
        Assert.True(listed.Plans.TotalItems > 5);
        Assert.True(listed.Plans.HasNextPage);
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersCatalogue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await CreatedAsync(provider, Request());

        RegisterTenantResult other = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ola",
            LastName = "Bello",
            Email = "ola@other.com",
            CompanyName = "Other Limited",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(other.Succeeded, string.Join("; ", other.Errors));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = other.TenantId;

        BenefitPlanListDto listed = await Benefits(provider)
            .ListAsync(new BenefitPlanQuery { PageSize = 200 });

        Assert.DoesNotContain(listed.Plans.Items, plan => plan.Name == "Creche Subsidy");

        // Their own catalogue is there, seeded independently.
        Assert.Contains(listed.Plans.Items, plan => plan.SeedKey == "ng-hmo");
    }
}
