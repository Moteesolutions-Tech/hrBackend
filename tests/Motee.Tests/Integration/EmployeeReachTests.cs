using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Authorization;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Which employees a scope reaches.
//
// One rule, used by every register, so it is tested once and thoroughly rather than
// sampled through whichever endpoint happens to be under test. Every register's
// narrowing is this query composed as a subquery, so a mistake here is a mistake in all
// of them at once — which is the point, and the reason it is worth pinning each scope
// kind rather than trusting the switch to read correctly.
[Collection(PostgresCollection.Name)]
public class EmployeeReachTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _engineeringId;
    private Guid _salesId;
    private Guid _platformUnitId;
    private Guid _lagosId;
    private Guid _londonId;

    // Ada manages Ben. Ada and Ben are Engineering/Lagos; Cara is Sales/London; Dele is
    // Sales/Lagos. Engineering sits under the Platform business unit and Sales does not,
    // so each axis separates the four differently and no two scopes can pass by accident.
    private Guid _adaId;
    private Guid _benId;
    private Guid _caraId;
    private Guid _deleId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        _tenantId = Guid.NewGuid();
        _engineeringId = Guid.NewGuid();
        _salesId = Guid.NewGuid();
        _platformUnitId = Guid.NewGuid();
        _lagosId = Guid.NewGuid();
        _londonId = Guid.NewGuid();
        _adaId = Guid.NewGuid();
        _benId = Guid.NewGuid();
        _caraId = Guid.NewGuid();
        _deleId = Guid.NewGuid();

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

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.BusinessUnits.Add(new BusinessUnit
            {
                Id = _platformUnitId,
                TenantId = _tenantId,
                Name = "Platform",
            });

            seed.Branches.Add(new Branch
            {
                Id = _lagosId,
                TenantId = _tenantId,
                Name = "Lagos",
                Code = "LAG",
            });

            seed.Branches.Add(new Branch
            {
                Id = _londonId,
                TenantId = _tenantId,
                Name = "London",
                Code = "LON",
            });

            await seed.SaveChangesAsync();
        }

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Departments.Add(new Department
            {
                Id = _engineeringId,
                TenantId = _tenantId,
                Name = "Engineering",
                Code = "ENG",
                BusinessUnitId = _platformUnitId,
            });

            seed.Departments.Add(new Department
            {
                Id = _salesId,
                TenantId = _tenantId,
                Name = "Sales",
                Code = "SAL",
            });

            await seed.SaveChangesAsync();
        }

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            Add(seed, _adaId, "Ada", _engineeringId, _lagosId, managerId: null);
            await seed.SaveChangesAsync();
        }

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            Add(seed, _benId, "Ben", _engineeringId, _lagosId, _adaId);
            Add(seed, _caraId, "Cara", _salesId, _londonId, managerId: null);
            Add(seed, _deleId, "Dele", _salesId, _lagosId, managerId: null);

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        return provider;
    }

    private void Add(
        MoteeDbContext seed,
        Guid id,
        string first,
        Guid departmentId,
        Guid branchId,
        Guid? managerId)
    {
        seed.Employees.Add(new Employee
        {
            Id = id,
            TenantId = _tenantId,
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            DepartmentId = departmentId,
            BranchId = branchId,
            ManagerId = managerId,
            Status = EmployeeStatus.Active,
        });
    }

    private static async Task<Guid[]> ReachedAsync(ServiceProvider provider, Viewer viewer)
    {
        using IServiceScope scope = provider.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<EmployeeReach>()
            .IdsOf(viewer)
            .OrderBy(id => id)
            .ToArrayAsync();
    }

    private static Guid[] Sorted(params Guid[] ids) => [.. ids.OrderBy(id => id)];

    [SkippableFact]
    public async Task EverythingReachesEverybody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            Sorted(_adaId, _benId, _caraId, _deleId),
            await ReachedAsync(provider, Viewer.Everything));
    }

    [SkippableFact]
    public async Task NothingReachesNobody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Empty(await ReachedAsync(provider, Viewer.Nobody));
    }

    [SkippableFact]
    public async Task SelfReachesOnlyTheHolder()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            [_adaId],
            await ReachedAsync(provider, Viewer.Of(DataScope.Nothing with
            {
                Kind = DataScopeKind.Self,
            }, _adaId)));
    }

    // A manager needs their own row in the list they work from, so this is reports plus
    // themselves rather than reports alone.
    [SkippableFact]
    public async Task DirectReportsReachesTheReportsAndTheManager()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            Sorted(_adaId, _benId),
            await ReachedAsync(provider, Viewer.Of(DataScope.Nothing with
            {
                Kind = DataScopeKind.DirectReports,
            }, _adaId)));
    }

    // The departments the level names, not the one the viewer sits in. Cara is asking
    // and Cara is in Sales, so a rule that read the holder's own department would answer
    // Sales — and the whole point of a named scope is that it does not.
    [SkippableFact]
    public async Task DepartmentReachesTheNamedDepartmentsRatherThanTheHoldersOwn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            Sorted(_adaId, _benId),
            await ReachedAsync(
                provider, Viewer.Of(DataScope.Departments(_engineeringId), _caraId)));
    }

    [SkippableFact]
    public async Task SeveralNamedDepartmentsAreReachedTogether()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            Sorted(_adaId, _benId, _caraId, _deleId),
            await ReachedAsync(
                provider,
                Viewer.Of(DataScope.Departments(_engineeringId, _salesId), _adaId)));
    }

    // Branch cuts across departments, which is the axis's reason to exist: Lagos holds
    // Engineering and Sales both, and London holds neither entirely.
    [SkippableFact]
    public async Task BranchCutsAcrossDepartments()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            Sorted(_adaId, _benId, _deleId),
            await ReachedAsync(provider, Viewer.Of(DataScope.Branches(_lagosId), _caraId)));
    }

    // Through the department, so adding a department to a unit widens the scope without
    // anybody touching the employees.
    [SkippableFact]
    public async Task BusinessUnitReachesEveryoneUnderItsDepartments()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            Sorted(_adaId, _benId),
            await ReachedAsync(
                provider, Viewer.Of(DataScope.BusinessUnits(_platformUnitId), _caraId)));
    }

    // An empty allowlist is not an open one. A level naming no departments reaches
    // nobody, and the alternative reading — "no restriction" — is how an unfinished
    // access level becomes a company-wide grant.
    [SkippableFact]
    public async Task ANamedScopeWithNoNamesReachesNobody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Empty(await ReachedAsync(provider, Viewer.Of(DataScope.Departments(), _adaId)));
    }

    // A holder-relative scope with no holder reaches nobody rather than everybody. This
    // is the case that arises when a background job or a token without an employee claim
    // reaches a scoped read.
    [SkippableFact]
    public async Task AHolderRelativeScopeWithNoHolderReachesNobody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Empty(await ReachedAsync(provider, Viewer.Of(DataScope.Nothing with
        {
            Kind = DataScopeKind.Self,
        }, employeeId: null)));

        Assert.Empty(await ReachedAsync(provider, Viewer.Of(DataScope.Nothing with
        {
            Kind = DataScopeKind.DirectReports,
        }, employeeId: null)));
    }

    // Self-relative kinds are resolved into named ones by the permission handler before
    // any endpoint sees them. If one arrives here anyway, it must reach nothing rather
    // than fall through to something permissive.
    [SkippableFact]
    public async Task AnUnresolvedSelfRelativeScopeReachesNobody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Empty(await ReachedAsync(provider, Viewer.Of(DataScope.TheirDepartment, _adaId)));
        Assert.Empty(await ReachedAsync(provider, Viewer.Of(DataScope.TheirBranch, _adaId)));
    }
}
