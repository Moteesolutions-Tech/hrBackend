using Microsoft.EntityFrameworkCore;
using Motee.Domain.Common;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// These assert database constraints, not C# logic. The in-memory provider has no
// unique indexes, so nothing here is meaningful without a real Postgres.
[Collection(PostgresCollection.Name)]
public class UserEmailUniquenessTests(PostgresFixture fixture)
{
    private static Tenant NewTenant(string slug) => new()
    {
        Id = Guid.NewGuid(),
        Name = slug,
        Slug = slug,
        CountryCode = CountryCode.Nigeria,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static ApplicationUser NewUser(Guid? tenantId, string email) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        IsPlatformStaff = tenantId is null,
        FirstName = "Ada",
        LastName = "Okafor",
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        SecurityStamp = Guid.NewGuid().ToString(),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    // One address belongs to one company. Someone working for two organisations
    // needs two addresses.
    [SkippableFact]
    public async Task TheSameEmailCannotExistInTwoTenants()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        await using MoteeDbContext context = fixture.CreateContext();

        Tenant acme = NewTenant("acme");
        Tenant globex = NewTenant("globex");
        context.Tenants.AddRange(acme, globex);
        await context.SaveChangesAsync();

        context.Users.Add(NewUser(acme.Id, "ada@shared.com"));
        await context.SaveChangesAsync();

        context.Users.Add(NewUser(globex.Id, "ada@shared.com"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task TheSameEmailIsRejectedTwiceWithinOneTenant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        await using MoteeDbContext context = fixture.CreateContext();

        Tenant acme = NewTenant("acme");
        context.Tenants.Add(acme);
        await context.SaveChangesAsync();

        context.Users.Add(NewUser(acme.Id, "ada@acme.com"));
        await context.SaveChangesAsync();

        context.Users.Add(NewUser(acme.Id, "ada@acme.com"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task TwoPlatformStaffCannotShareAnEmail()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        await using MoteeDbContext context = fixture.CreateContext();

        context.Users.Add(NewUser(null, "ops@motee.app"));
        await context.SaveChangesAsync();

        context.Users.Add(NewUser(null, "ops@motee.app"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    // Platform staff are not a separate namespace — the address is taken outright.
    [SkippableFact]
    public async Task PlatformStaffCannotShareAnEmailWithATenantUser()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        await using MoteeDbContext context = fixture.CreateContext();

        Tenant acme = NewTenant("acme");
        context.Tenants.Add(acme);
        await context.SaveChangesAsync();

        context.Users.Add(NewUser(null, "ada@shared.com"));
        await context.SaveChangesAsync();

        context.Users.Add(NewUser(acme.Id, "ada@shared.com"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task TenantSlugsAreUnique()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        await using MoteeDbContext context = fixture.CreateContext();

        context.Tenants.Add(NewTenant("acme"));
        await context.SaveChangesAsync();

        context.Tenants.Add(NewTenant("acme"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}
