using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Authorization;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The list behind the Users screen, and the source the access-level holder endpoints
// had no way to offer: assigning a level took a user id that nothing could produce.
[Collection(PostgresCollection.Name)]
public class TenantUserServiceTests(PostgresFixture fixture)
{
    private Guid _tenantId;

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

        return provider;
    }

    private async Task AddUserAsync(
        Guid id,
        string first,
        string email,
        bool isOwner = false,
        bool emailConfirmed = true,
        DateTimeOffset? lockoutEnd = null,
        bool platformStaff = false)
    {
        await using MoteeDbContext seed = fixture.CreateContext();

        seed.Users.Add(new ApplicationUser
        {
            Id = id,
            TenantId = platformStaff ? null : _tenantId,
            IsPlatformStaff = platformStaff,
            IsOwner = isOwner,
            FirstName = first,
            LastName = "Okafor",
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = emailConfirmed,
            LockoutEnd = lockoutEnd,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    private static ITenantUserService Service(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<ITenantUserService>();

    [SkippableFact]
    public async Task ListsTheTenantsUsers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await AddUserAsync(Guid.NewGuid(), "Ada", "ada@acme.com", isOwner: true);
        await AddUserAsync(Guid.NewGuid(), "Ben", "ben@acme.com");

        IReadOnlyList<TenantUserDto> users = await Service(provider).ListAsync();

        Assert.Equal(2, users.Count);
        Assert.Equal(["Ada Okafor", "Ben Okafor"], users.Select(user => user.Name));
        Assert.Equal("AO", users[0].Initials);
    }

    // The owner bypasses every permission check, so an interface that showed only their
    // levels would report someone with no access who in fact has all of it.
    [SkippableFact]
    public async Task TheOwnerIsFlagged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await AddUserAsync(Guid.NewGuid(), "Ada", "ada@acme.com", isOwner: true);
        await AddUserAsync(Guid.NewGuid(), "Ben", "ben@acme.com");

        IReadOnlyList<TenantUserDto> users = await Service(provider).ListAsync();

        Assert.True(users.Single(user => user.Name == "Ada Okafor").IsOwner);
        Assert.False(users.Single(user => user.Name == "Ben Okafor").IsOwner);
    }

    // Platform staff operate across every tenant and carry no tenant id. They are not
    // this company's people and must never appear on its screens.
    [SkippableFact]
    public async Task PlatformStaffAreExcluded()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await AddUserAsync(Guid.NewGuid(), "Ada", "ada@acme.com");
        await AddUserAsync(Guid.NewGuid(), "Staff", "staff@motee.com", platformStaff: true);

        IReadOnlyList<TenantUserDto> users = await Service(provider).ListAsync();

        Assert.Equal("Ada Okafor", Assert.Single(users).Name);
    }

    [SkippableFact]
    public async Task AnotherCompanysUsersAreNotVisible()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await AddUserAsync(Guid.NewGuid(), "Ada", "ada@acme.com");

        Guid otherTenant = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = otherTenant,
                Name = "Globex",
                Slug = $"globex-{otherTenant:N}",
                CountryCode = CountryCode.Nigeria,
            });

            seed.Users.Add(new ApplicationUser
            {
                Id = Guid.NewGuid(),
                TenantId = otherTenant,
                FirstName = "Cara",
                LastName = "Ndu",
                Email = "cara@globex.com",
                NormalizedEmail = "CARA@GLOBEX.COM",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        IReadOnlyList<TenantUserDto> users = await Service(provider).ListAsync();

        Assert.Equal("Ada Okafor", Assert.Single(users).Name);
    }

    // Registered but never verified. They cannot sign in and they are not locked out
    // either — the fix differs, so the states must not be conflated.
    [SkippableFact]
    public async Task AnUnverifiedAccountIsPending()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await AddUserAsync(Guid.NewGuid(), "Ada", "ada@acme.com", emailConfirmed: false);

        Assert.Equal(UserAccountState.Pending, Assert.Single(await Service(provider).ListAsync()).State);
    }

    [SkippableFact]
    public async Task AnAccountInLockoutIsLockedAndSaysUntilWhen()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateTimeOffset until = DateTimeOffset.UtcNow.AddMinutes(15);
        await AddUserAsync(Guid.NewGuid(), "Ada", "ada@acme.com", lockoutEnd: until);

        TenantUserDto user = Assert.Single(await Service(provider).ListAsync());

        Assert.Equal(UserAccountState.Locked, user.State);
        Assert.NotNull(user.LockedUntil);
    }

    // Identity leaves LockoutEnd set after it expires, so a past date must not read as
    // locked — otherwise an account stays visibly locked forever after one bad evening.
    [SkippableFact]
    public async Task AnExpiredLockoutIsActiveAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await AddUserAsync(
            Guid.NewGuid(), "Ada", "ada@acme.com",
            lockoutEnd: DateTimeOffset.UtcNow.AddMinutes(-5));

        TenantUserDto user = Assert.Single(await Service(provider).ListAsync());

        Assert.Equal(UserAccountState.Active, user.State);
        Assert.Null(user.LockedUntil);
    }

    [SkippableFact]
    public async Task EveryHeldLevelIsListed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid userId = Guid.NewGuid();
        await AddUserAsync(userId, "Ada", "ada@acme.com");

        Guid active = await AddLevelAsync("HR Admin", AccessLevelStatus.Active, userId);
        Guid retired = await AddLevelAsync("Recruiter", AccessLevelStatus.Inactive, userId);

        TenantUserDto user = Assert.Single(await Service(provider).ListAsync());

        Assert.Equal(2, user.AccessLevels.Count);

        // A deactivated level grants nothing but is still assigned. An admin asking why
        // someone lost access needs to see that here rather than on another screen.
        Assert.True(user.AccessLevels.Single(level => level.Id == active).IsActive);
        Assert.False(user.AccessLevels.Single(level => level.Id == retired).IsActive);
    }

    // Users are not covered by the global tenant filter, so assigning has to check the
    // person belongs to this company itself. Without that clause an admin who knows a
    // user id from another company can hand them one of their own levels — changing what
    // somebody in a company they do not administer is allowed to do.
    [SkippableFact]
    public async Task ALevelCannotBeAssignedToAnotherCompanysUser()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid levelId = Guid.NewGuid();

        await using (MoteeDbContext context = fixture.CreateContext(_tenantId))
        {
            context.AccessLevels.Add(new AccessLevel
            {
                Id = levelId,
                TenantId = _tenantId,
                Name = "HR Admin",
                Kind = AccessLevelKind.Custom,
                Status = AccessLevelStatus.Active,
                Scope = DataScope.Everything,
                Permissions = [],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await context.SaveChangesAsync();
        }

        Guid otherTenant = Guid.NewGuid();
        Guid outsiderId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = otherTenant,
                Name = "Globex",
                Slug = $"globex-{otherTenant:N}",
                CountryCode = CountryCode.Nigeria,
            });

            seed.Users.Add(new ApplicationUser
            {
                Id = outsiderId,
                TenantId = otherTenant,
                FirstName = "Cara",
                LastName = "Ndu",
                Email = "cara@globex.com",
                NormalizedEmail = "CARA@GLOBEX.COM",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        IAccessLevelService levels = provider.CreateScope().ServiceProvider
            .GetRequiredService<IAccessLevelService>();

        AccessLevelResult result = await levels.AssignAsync(outsiderId, levelId);

        Assert.Equal(AccessLevelOutcome.UserNotFound, result.Outcome);

        await using MoteeDbContext check = fixture.CreateContext();
        Assert.Empty(check.UserAccessLevels.IgnoreQueryFilters().ToList());
    }

    private async Task<Guid> AddLevelAsync(string name, AccessLevelStatus status, Guid userId)
    {
        Guid levelId = Guid.NewGuid();

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        context.AccessLevels.Add(new AccessLevel
        {
            Id = levelId,
            TenantId = _tenantId,
            Name = name,
            Kind = AccessLevelKind.Custom,
            Status = status,
            Scope = DataScope.Everything,
            Permissions = [],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        context.UserAccessLevels.Add(new UserAccessLevel
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            UserId = userId,
            AccessLevelId = levelId,
            AssignedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();

        return levelId;
    }
}
