using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Tenancy;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class TenantSetupServiceTests(PostgresFixture fixture)
{
    private static TenantSetupRequest Setup() => new()
    {
        Industry = "Technology",
        CompanySize = "10–50",
        CompanyEmailDomain = "acme.com",
        CompanyPolicies = "Be excellent to each other.",
        ManagerTitle = "Team Lead",
        DepartmentLabel = "Division",
        StructureType = "flat",
        EnabledModules = ["employee-management", "payroll"],
    };

    private async Task<(Guid TenantId, ServiceProvider Provider)> ArrangeAsync(
        string email = "ada@acme.com")
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = email,
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        return (registration.TenantId, fixture.BuildProvider());
    }

    private static ITenantSetupService Service(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<ITenantSetupService>();

    // The wizard should open as review-and-adjust, not a blank form.
    [SkippableFact]
    public async Task PrefillsWhatRegistrationAlreadyKnows()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        TenantSetupDto? setup = await Service(owned).GetAsync(tenantId);

        Assert.NotNull(setup);
        Assert.Equal("Acme Corporation", setup.CompanyName);
        Assert.Equal("NG", setup.CountryCode);
        Assert.Equal("Nigeria", setup.Country);
        Assert.Null(setup.CompanyEmailDomain);
        Assert.False(setup.OnboardingCompleted);
    }

    // Nothing is inferred from the admin's address; the field starts empty and the
    // tenant fills in whatever they use.
    [SkippableFact]
    public async Task LeavesTheEmailDomainEmptyUntilItIsSaved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync("ada@gmail.com");
        await using ServiceProvider owned = provider;

        TenantSetupDto? setup = await Service(owned).GetAsync(tenantId);

        Assert.Null(setup!.CompanyEmailDomain);
    }

    [SkippableTheory]
    [InlineData("acme.com")]
    [InlineData("gmail.com")]
    [InlineData("northwind.co.uk")]
    public async Task AcceptsWhicheverDomainTheTenantUses(string domain)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).SaveAsync(tenantId, Setup() with { CompanyEmailDomain = domain });

        Assert.Equal(domain, (await Service(owned).GetAsync(tenantId))!.CompanyEmailDomain);
    }

    [SkippableFact]
    public async Task OpensWithTheDocumentedDefaultsRatherThanEmptyFields()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        TenantSetupDto setup = (await Service(owned).GetAsync(tenantId))!;

        Assert.Equal("Line Manager", setup.ManagerTitle);
        Assert.Equal("Department", setup.DepartmentLabel);
        Assert.Equal(StructureType.Hierarchical, setup.StructureType);
        Assert.Empty(setup.EnabledModules);
    }

    [SkippableFact]
    public async Task SavesAndReadsBackEverySetting()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).SaveAsync(tenantId, Setup());

        TenantSetupDto setup = (await Service(owned).GetAsync(tenantId))!;

        Assert.Equal("Technology", setup.Industry);
        Assert.Equal(CompanySize.Small, setup.CompanySize);
        Assert.Equal("Be excellent to each other.", setup.CompanyPolicies);
        Assert.Equal("Team Lead", setup.ManagerTitle);
        Assert.Equal("Division", setup.DepartmentLabel);
        Assert.Equal(StructureType.Flat, setup.StructureType);
        Assert.Equal(["employee-management", "payroll"], setup.EnabledModules);
    }

    // Saving a step must not silently move the tenant into a different jurisdiction.
    [SkippableFact]
    public async Task LeavesCompanyNameAndCountryAlone()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).SaveAsync(tenantId, Setup());

        await using MoteeDbContext context = fixture.CreateContext();
        Tenant tenant = await context.Tenants.FirstAsync(candidate => candidate.Id == tenantId);

        Assert.Equal("Acme Corporation", tenant.Name);
        Assert.Equal("acme-corporation", tenant.Slug);
        Assert.Equal(Motee.Domain.Common.CountryCode.Nigeria, tenant.CountryCode);
    }

    [SkippableFact]
    public async Task SavingTwiceKeepsTheLatestValues()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).SaveAsync(tenantId, Setup());
        await Service(owned).SaveAsync(tenantId, Setup() with { ManagerTitle = "Supervisor" });

        TenantSetupDto setup = (await Service(owned).GetAsync(tenantId))!;

        Assert.Equal("Supervisor", setup.ManagerTitle);
    }

    [SkippableFact]
    public async Task CompletingRecordsTheTimestamp()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Assert.False((await Service(owned).GetAsync(tenantId))!.OnboardingCompleted);

        await Service(owned).CompleteAsync(tenantId);

        Assert.True((await Service(owned).GetAsync(tenantId))!.OnboardingCompleted);
    }

    // A tenant that revisits settings later must not have their original completion
    // date rewritten.
    [SkippableFact]
    public async Task CompletingAgainDoesNotMoveTheOriginalTimestamp()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CompleteAsync(tenantId);
        DateTimeOffset? first = (await Service(owned).GetAsync(tenantId))!.OnboardingCompletedAt;

        await Service(owned).CompleteAsync(tenantId);
        DateTimeOffset? second = (await Service(owned).GetAsync(tenantId))!.OnboardingCompletedAt;

        Assert.Equal(first, second);
    }

    [SkippableFact]
    public async Task SavingAfterCompletionStillWorks()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CompleteAsync(tenantId);
        await Service(owned).SaveAsync(tenantId, Setup() with { DepartmentLabel = "Unit" });

        TenantSetupDto setup = (await Service(owned).GetAsync(tenantId))!;

        Assert.Equal("Unit", setup.DepartmentLabel);
        Assert.True(setup.OnboardingCompleted);
    }

    [SkippableFact]
    public async Task AnUnknownTenantHasNoSetup()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Assert.Null(await Service(owned).GetAsync(Guid.NewGuid()));
    }
}
