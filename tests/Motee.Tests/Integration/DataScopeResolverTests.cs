using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Authorization;
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

// "Their own department" turned into "these departments", once, before any query sees it.
//
// The resolution exists so nothing downstream has to know self-relative scopes are a
// thing — a query layer that handled them would need the viewer's department threaded
// through every query object, and every module added later would have to remember.
[Collection(PostgresCollection.Name)]
public class DataScopeResolverTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _engineeringId;
    private Guid _salesId;
    private Guid _lagosId;

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

        _engineeringId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _salesId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        _lagosId = (await Resolve<IBranchService>(provider).CreateAsync(new BranchRequest
        {
            Name = "Lagos",
            Code = "LAG",
        })).Branch!.Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        string email,
        Guid departmentId,
        Guid? branchId = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = email,
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            BranchId = branchId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
        })).Employee!.Id;

    // A department is required at creation, so somebody ends up in none only by it being
    // cleared afterwards — a department deleted out from under them, which the FK does
    // with SetNull. Reproduced directly, because the service will not create that state.
    private async Task ClearDepartmentAsync(Guid employeeId)
    {
        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        Employee employee = await context.Employees
            .FirstAsync(candidate => candidate.Id == employeeId);

        employee.DepartmentId = null;

        await context.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task TheirOwnDepartmentBecomesTheDepartmentTheyAreIn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada", "ada@acme.com", _engineeringId);

        DataScope resolved = await Resolve<IDataScopeResolver>(provider)
            .ResolveAsync(DataScope.TheirDepartment, ada);

        Assert.Equal(DataScopeKind.Department, resolved.Kind);
        Assert.Equal([_engineeringId], resolved.DepartmentIds);
    }

    [SkippableFact]
    public async Task TheirOwnBranchBecomesTheSiteTheyArePostedTo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada", "ada@acme.com", _engineeringId, _lagosId);

        DataScope resolved = await Resolve<IDataScopeResolver>(provider)
            .ResolveAsync(DataScope.TheirBranch, ada);

        Assert.Equal(DataScopeKind.Branch, resolved.Kind);
        Assert.Equal([_lagosId], resolved.BranchIds);
    }

    // The case both the old designs got wrong. A level scoped to "their own department",
    // held by somebody in none, genuinely grants nothing — and failing open here would
    // hand them the company.
    [SkippableFact]
    public async Task SomebodyInNoDepartmentReachesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada", "ada@acme.com", _engineeringId);

        await ClearDepartmentAsync(ada);

        DataScope resolved = await Resolve<IDataScopeResolver>(provider)
            .ResolveAsync(DataScope.TheirDepartment, ada);

        Assert.Equal(DataScopeKind.Department, resolved.Kind);
        Assert.True(resolved.ReachesNothing);
    }

    // The admin who registered the tenant before anybody was hired, or a platform
    // operator. There is nothing to be relative to.
    [SkippableFact]
    public async Task SomebodyWithNoEmployeeRecordReachesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DataScope resolved = await Resolve<IDataScopeResolver>(provider)
            .ResolveAsync(DataScope.TheirDepartment, null);

        Assert.True(resolved.ReachesNothing);
    }

    // An employee id that no longer matches a row — deleted, or from another company,
    // which the tenant filter makes indistinguishable and should.
    [SkippableFact]
    public async Task AnUnknownHolderReachesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DataScope resolved = await Resolve<IDataScopeResolver>(provider)
            .ResolveAsync(DataScope.TheirBranch, Guid.NewGuid());

        Assert.True(resolved.ReachesNothing);
    }

    // Named scopes are not self-relative and must come back untouched — the resolver is
    // on the path of every authorised request.
    [SkippableFact]
    public async Task ANamedScopePassesThroughUnchanged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada", "ada@acme.com", _engineeringId);

        DataScope named = DataScope.Departments(_salesId);

        DataScope resolved = await Resolve<IDataScopeResolver>(provider)
            .ResolveAsync(named, ada);

        // Sales, not Engineering: the level named it, and the holder's own department
        // must not override that.
        Assert.Equal([_salesId], resolved.DepartmentIds);
    }

    [SkippableFact]
    public async Task EverythingAndNothingPassThroughUnchanged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IDataScopeResolver resolver = Resolve<IDataScopeResolver>(provider);

        Assert.Equal(DataScopeKind.All, (await resolver.ResolveAsync(DataScope.Everything, null)).Kind);
        Assert.Equal(DataScopeKind.None, (await resolver.ResolveAsync(DataScope.Nothing, null)).Kind);
    }

    // What the resolution is for: once resolved, the ordinary named path does the work.
    [SkippableFact]
    public async Task AResolvedScopeReachesTheHoldersOwnDepartmentAndNoOther()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada", "ada@acme.com", _engineeringId);
        await HireAsync(provider, "Ben", "ben@acme.com", _engineeringId);
        await HireAsync(provider, "Cara", "cara@acme.com", _salesId);

        DataScope resolved = await Resolve<IDataScopeResolver>(provider)
            .ResolveAsync(DataScope.TheirDepartment, ada);

        PagedResult<EmployeeListItemDto> reached = await Resolve<IEmployeeService>(provider)
            .ListAsync(new EmployeeQuery { Scope = resolved, ViewerEmployeeId = ada });

        Assert.Equal(2, reached.TotalItems);
        Assert.DoesNotContain(
            reached.Items, row => row.Name.StartsWith("Cara", StringComparison.Ordinal));
    }

    // The backstop. If a self-relative scope ever reached a query unresolved, it must
    // match nothing rather than everything — the resolution is a convenience, not the
    // only thing standing between a level and the whole company.
    [SkippableFact]
    public async Task AnUnresolvedSelfRelativeScopeReachesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada", "ada@acme.com", _engineeringId);

        PagedResult<EmployeeListItemDto> reached = await Resolve<IEmployeeService>(provider)
            .ListAsync(new EmployeeQuery
            {
                Scope = DataScope.TheirDepartment,
                ViewerEmployeeId = ada,
            });

        Assert.Empty(reached.Items);
    }
}
