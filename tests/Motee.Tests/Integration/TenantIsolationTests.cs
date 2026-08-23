using Microsoft.EntityFrameworkCore;
using Motee.Domain.Common;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The safety net under every tenant-scoped module. A query that forgets its tenant
// predicate must return nothing rather than another company's rows.
[Collection(PostgresCollection.Name)]
public class TenantIsolationTests(PostgresFixture fixture)
{
    private async Task<(Guid AcmeId, Guid GlobexId)> SeedTwoTenantsAsync()
    {
        await fixture.ResetAsync();

        Guid acmeId = Guid.NewGuid();
        Guid globexId = Guid.NewGuid();

        // No tenant on this context, so nothing is stamped implicitly.
        await using MoteeDbContext seed = fixture.CreateContext();

        seed.Tenants.AddRange(
            new Tenant { Id = acmeId, Name = "Acme", Slug = $"acme-{acmeId:N}", CountryCode = CountryCode.Nigeria },
            new Tenant { Id = globexId, Name = "Globex", Slug = $"globex-{globexId:N}", CountryCode = CountryCode.UnitedKingdom });

        seed.Departments.AddRange(
            new Department { Id = Guid.NewGuid(), TenantId = acmeId, Name = "Engineering", Code = "ENG" },
            new Department { Id = Guid.NewGuid(), TenantId = acmeId, Name = "Finance", Code = "FIN" },
            new Department { Id = Guid.NewGuid(), TenantId = globexId, Name = "Legal", Code = "LEG" });

        await seed.SaveChangesAsync();

        return (acmeId, globexId);
    }

    [SkippableFact]
    public async Task EachTenantSeesOnlyItsOwnRows()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid acmeId, Guid globexId) = await SeedTwoTenantsAsync();

        await using MoteeDbContext acme = fixture.CreateContext(acmeId);
        await using MoteeDbContext globex = fixture.CreateContext(globexId);

        Assert.Equal(["ENG", "FIN"], await acme.Departments.Select(d => d.Code).OrderBy(c => c).ToListAsync());
        Assert.Equal(["LEG"], await globex.Departments.Select(d => d.Code).ToListAsync());
    }

    // The dangerous case: an id leaked from elsewhere, fetched without a predicate.
    [SkippableFact]
    public async Task AnotherTenantsRowIsNotReachableEvenByItsId()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid acmeId, Guid globexId) = await SeedTwoTenantsAsync();

        Guid legalId;
        await using (MoteeDbContext globex = fixture.CreateContext(globexId))
        {
            legalId = await globex.Departments.Select(d => d.Id).FirstAsync();
        }

        await using MoteeDbContext acme = fixture.CreateContext(acmeId);

        Assert.Null(await acme.Departments.FirstOrDefaultAsync(d => d.Id == legalId));
        Assert.Null(await acme.Departments.FindAsync(legalId));
    }

    // Background jobs and registration run with no tenant resolved. Failing closed
    // means they see nothing rather than everything.
    [SkippableFact]
    public async Task NoTenantResolvedSeesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await SeedTwoTenantsAsync();

        await using MoteeDbContext none = fixture.CreateContext();

        Assert.Empty(await none.Departments.ToListAsync());
    }

    [SkippableFact]
    public async Task PlatformWideReadsMustOptOutExplicitly()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await SeedTwoTenantsAsync();

        await using MoteeDbContext none = fixture.CreateContext();

        Assert.Equal(3, await none.Departments.IgnoreQueryFilters().CountAsync());
    }

    [SkippableFact]
    public async Task CountsAndAggregatesAreFilteredToo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid acmeId, _) = await SeedTwoTenantsAsync();

        await using MoteeDbContext acme = fixture.CreateContext(acmeId);

        Assert.Equal(2, await acme.Departments.CountAsync());
        Assert.False(await acme.Departments.AnyAsync(d => d.Code == "LEG"));
    }

    // An insert with no TenantId set is stamped from the current tenant, so it can
    // never become a row the filter refuses to return.
    [SkippableFact]
    public async Task InsertsAreStampedWithTheCurrentTenant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid acmeId, _) = await SeedTwoTenantsAsync();

        await using (MoteeDbContext acme = fixture.CreateContext(acmeId))
        {
            acme.Departments.Add(new Department { Id = Guid.NewGuid(), Name = "People", Code = "PPL" });
            await acme.SaveChangesAsync();
        }

        await using MoteeDbContext reread = fixture.CreateContext(acmeId);

        Assert.Equal(acmeId, (await reread.Departments.SingleAsync(d => d.Code == "PPL")).TenantId);
    }
}
