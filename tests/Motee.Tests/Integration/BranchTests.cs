using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Sites, and the axis they add. A branch is where somebody physically is — distinct from
// their department, which is what they do, and from the business unit, which is which
// part of the group they belong to.
//
// The point of the axis is that it cuts across the other two: "everyone at the Lagos
// office" spans every department there, and neither of the others can express it.
[Collection(PostgresCollection.Name)]
public class BranchTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _departmentId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        _tenantId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = _tenantId,
                Name = "Acme",
                Slug = $"acme-{_tenantId:N}",
                CountryCode = CountryCode.Nigeria,
            });

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static Task<BranchResult> BranchAsync(
        ServiceProvider provider,
        string name = "Lagos",
        string code = "LAG",
        Guid? manager = null,
        int? target = null) =>
        Resolve<IBranchService>(provider).CreateAsync(new BranchRequest
        {
            Name = name,
            Code = code,
            Kind = BranchKind.Branch,
            AddressLines = ["12 Adeola Odeku Street"],
            City = "Lagos",
            Country = "Nigeria",
            TimeZone = "Africa/Lagos",
            ManagerEmployeeId = manager,
            HeadcountTarget = target,
        });

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        string email,
        Guid? branchId = null,
        Guid? departmentId = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = email,
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId ?? _departmentId,
            BranchId = branchId,
            EmploymentType = EmploymentType.FullTime,

            // Explicit: a new employee is Pending by default, and every count here is of
            // active staff.
            Status = EmployeeStatus.Active,
        })).Employee!.Id;

    [SkippableFact]
    public async Task ASiteIsCreatedWithItsAddressFlattenedForDisplay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        BranchResult created = await BranchAsync(provider);

        Assert.True(created.Succeeded, created.Outcome.ToString());
        Assert.Equal("LAG", created.Branch!.Code);
        Assert.Equal("12 Adeola Odeku Street, Lagos, Nigeria", created.Branch.AddressLabel);
        Assert.Equal(0, created.Branch.EmployeeCount);
    }

    // The code appears on a badge and in a picker, where two identical ones cannot be
    // chosen between. Normalised too, so "lag" and "LAG" collide.
    [SkippableFact]
    public async Task TwoSitesCannotShareACode()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await BranchAsync(provider);

        BranchResult second = await BranchAsync(provider, "Lagos Annexe", "lag");

        Assert.Equal(BranchOutcome.DuplicateCode, second.Outcome);
    }

    // The counts the screen leads with, and the reason a site is more than a label: it is
    // where departments overlap.
    [SkippableFact]
    public async Task ASiteCountsItsPeopleAndTheDepartmentsPresentThere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid lagos = (await BranchAsync(provider, target: 5)).Branch!.Id;

        Guid sales = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        await HireAsync(provider, "Ada", "ada@acme.com", lagos);
        await HireAsync(provider, "Ben", "ben@acme.com", lagos);
        await HireAsync(provider, "Cara", "cara@acme.com", lagos, sales);

        BranchDto branch = (await Resolve<IBranchService>(provider).GetAsync(lagos))!;

        Assert.Equal(3, branch.EmployeeCount);
        Assert.Equal(2, branch.DepartmentCount);

        // Five planned, three posted.
        Assert.Equal(2, branch.OpenPositions);
    }

    [SkippableFact]
    public async Task NoTargetMeansNoOpenPositionsRatherThanZero()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        BranchDto branch = (await BranchAsync(provider)).Branch!;

        Assert.Null(branch.OpenPositions);
    }

    // Deleting would leave those people pointing at nothing, which reads as "no branch"
    // and drops them out of every site-scoped list — a fire register included.
    [SkippableFact]
    public async Task ASiteWithPeopleCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid lagos = (await BranchAsync(provider)).Branch!.Id;

        await HireAsync(provider, "Ada", "ada@acme.com", lagos);

        Assert.Equal(
            BranchOutcome.InUse,
            await Resolve<IBranchService>(provider).DeleteAsync(lagos));
    }

    [SkippableFact]
    public async Task AnEmptySiteCanBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid lagos = (await BranchAsync(provider)).Branch!.Id;

        Assert.Equal(
            BranchOutcome.Succeeded,
            await Resolve<IBranchService>(provider).DeleteAsync(lagos));
    }

    [SkippableFact]
    public async Task AManagerWhoIsNotAnEmployeeIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        BranchResult created = await BranchAsync(provider, manager: Guid.NewGuid());

        Assert.Equal(BranchOutcome.UnknownManager, created.Outcome);
    }

    [SkippableFact]
    public async Task TheManagersNameIsResolvedForDisplay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada", "ada@acme.com");

        BranchDto branch = (await BranchAsync(provider, manager: ada)).Branch!;

        Assert.Equal("Ada Okafor", branch.ManagerName);
    }

    // The axis itself: a site cuts across departments, so scoping to one reaches people
    // in several and excludes people in the same department elsewhere.
    [SkippableFact]
    public async Task TheBranchScopeReachesEveryoneOnSiteAndNobodyElsewhere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid lagos = (await BranchAsync(provider, "Lagos", "LAG")).Branch!.Id;
        Guid london = (await BranchAsync(provider, "London", "LDN")).Branch!.Id;

        Guid sales = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        // Two departments in Lagos, and one of those same departments in London.
        await HireAsync(provider, "Ada", "ada@acme.com", lagos);
        await HireAsync(provider, "Ben", "ben@acme.com", lagos, sales);
        await HireAsync(provider, "Cara", "cara@acme.com", london);

        PagedResult<EmployeeListItemDto> onSite = await Resolve<IEmployeeService>(provider)
            .ListAsync(new EmployeeQuery { Scope = DataScope.Branches(lagos) });

        Assert.Equal(2, onSite.TotalItems);
        Assert.DoesNotContain(onSite.Items, row => row.Name.StartsWith("Cara", StringComparison.Ordinal));
    }

    // Narrower of the two wins, as everywhere else: holding a second level must never
    // widen what the first reaches.
    [SkippableFact]
    public async Task TwoBranchScopesIntersect()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid lagos = Guid.NewGuid();
        Guid london = Guid.NewGuid();
        Guid abuja = Guid.NewGuid();

        DataScope merged = DataScope.Intersect(
            DataScope.Branches(lagos, london),
            DataScope.Branches(london, abuja));

        Assert.Equal(DataScopeKind.Branch, merged.Kind);
        Assert.Equal([london], merged.BranchIds);
    }

    // An empty allowlist is not an open one. Consistent with Department and BusinessUnit,
    // and deliberately different from the frontend's own mock, which treats an empty
    // branch list as "the holder's own site".
    [SkippableFact]
    public async Task ABranchScopeNamingNoSitesReachesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid lagos = (await BranchAsync(provider)).Branch!.Id;
        await HireAsync(provider, "Ada", "ada@acme.com", lagos);

        Assert.True(DataScope.Branches().ReachesNothing);

        PagedResult<EmployeeListItemDto> reached = await Resolve<IEmployeeService>(provider)
            .ListAsync(new EmployeeQuery { Scope = DataScope.Branches() });

        Assert.Empty(reached.Items);
    }

    // The posting travels with the employee record, so a profile shows where somebody
    // sits and where they are without a second call.
    [SkippableFact]
    public async Task AnEmployeeCarriesTheirSite()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid lagos = (await BranchAsync(provider)).Branch!.Id;
        Guid ada = await HireAsync(provider, "Ada", "ada@acme.com", lagos);

        EmployeeDto employee = (await Resolve<IEmployeeService>(provider).GetAsync(ada))!;

        Assert.Equal(lagos, employee.BranchId);
        Assert.Equal("Lagos", employee.BranchName);
    }

    // A company that has not set up any sites must not find its directory broken.
    [SkippableFact]
    public async Task SomebodyWithNoSiteIsStillAnOrdinaryEmployee()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada", "ada@acme.com");

        EmployeeDto employee = (await Resolve<IEmployeeService>(provider).GetAsync(ada))!;

        Assert.Null(employee.BranchId);
        Assert.Null(employee.BranchName);
    }

    [SkippableFact]
    public async Task OneCompanysSitesAreInvisibleToAnother()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await BranchAsync(provider);

        Guid otherTenantId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = otherTenantId,
                Name = "Other",
                Slug = $"other-{otherTenantId:N}",
                CountryCode = CountryCode.Nigeria,
            });

            await seed.SaveChangesAsync();
        }

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = otherTenantId;

        Assert.Empty(await Resolve<IBranchService>(provider).ListAsync());

        // And the code is free again for them, because uniqueness is per tenant.
        Assert.True((await BranchAsync(provider)).Succeeded);
    }

    // Counts are of active staff. A leaver still on the books would overstate every site,
    // and the same number decides whether the site can be deleted.
    [SkippableFact]
    public async Task LeaversDoNotCountTowardsASitesHeadcount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid lagos = (await BranchAsync(provider)).Branch!.Id;
        Guid ada = await HireAsync(provider, "Ada", "ada@acme.com", lagos);

        await using (MoteeDbContext context = fixture.CreateContext(_tenantId))
        {
            Employee employee = await context.Employees.FirstAsync(e => e.Id == ada);
            employee.Status = EmployeeStatus.Inactive;
            await context.SaveChangesAsync();
        }

        BranchDto branch = (await Resolve<IBranchService>(provider).GetAsync(lagos))!;

        Assert.Equal(0, branch.EmployeeCount);

        // But their record still says where they worked, so the site is not deletable.
        Assert.Equal(
            BranchOutcome.InUse,
            await Resolve<IBranchService>(provider).DeleteAsync(lagos));
    }
}
