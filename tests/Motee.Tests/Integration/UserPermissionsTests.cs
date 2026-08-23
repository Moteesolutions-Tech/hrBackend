using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Authorization now reads what a user holds from the database rather than a role
// claim. These cover the path the permission handler takes on every request.
[Collection(PostgresCollection.Name)]
public class UserPermissionsTests(PostgresFixture fixture)
{
    private const string Employees = "organization.employees";

    private async Task<(Guid TenantId, Guid UserId, ServiceProvider Provider)> ArrangeAsync(
        bool isOwner = false)
    {
        await fixture.ResetAsync();

        Guid tenantId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Acme",
                Slug = $"acme-{tenantId:N}",
                CountryCode = CountryCode.Nigeria,
            });

            seed.Users.Add(new ApplicationUser
            {
                Id = userId,
                TenantId = tenantId,
                FirstName = "Ada",
                LastName = "Okafor",
                Email = "ada@acme.com",
                UserName = "ada@acme.com",
                NormalizedEmail = "ADA@ACME.COM",
                IsOwner = isOwner,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = tenantId;

        return (tenantId, userId, provider);
    }

    private static async Task<Guid> GiveLevelAsync(
        PostgresFixture fixture,
        Guid tenantId,
        Guid userId,
        string name,
        DataScope scope,
        AccessLevelStatus status,
        params PermissionAction[] actions)
    {
        Guid levelId = Guid.NewGuid();

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        context.AccessLevels.Add(new AccessLevel
        {
            Id = levelId,
            TenantId = tenantId,
            Name = name,
            Kind = AccessLevelKind.Custom,
            Status = status,
            Scope = scope,
            Permissions =
            [
                new ModulePermission { Module = Employees, Access = true, Actions = actions },
            ],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        context.UserAccessLevels.Add(new UserAccessLevel
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            AccessLevelId = levelId,
            AssignedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync();

        return levelId;
    }

    private static IUserPermissions Permissions(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IUserPermissions>();

    [SkippableFact]
    public async Task AHeldLevelGrantsWhatItPermits()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await GiveLevelAsync(fixture, tenantId, userId, "HR Admin", DataScope.Everything,
            AccessLevelStatus.Active, PermissionAction.View, PermissionAction.Delete);

        ResolvedPermissions held = await Permissions(owned).ForAsync(userId);

        Assert.Equal(
            DataScopeKind.All,
            AccessDecision.Resolve(held.Permissions, Employees, PermissionAction.Delete).Kind);

        Assert.Equal(["HR Admin"], held.LevelNames);
    }

    // Withdrawing a level has to bite at once, or deactivating one is a label rather
    // than a control.
    [SkippableFact]
    public async Task AnInactiveLevelGrantsNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await GiveLevelAsync(fixture, tenantId, userId, "HR Admin", DataScope.Everything,
            AccessLevelStatus.Inactive, PermissionAction.View, PermissionAction.Delete);

        ResolvedPermissions held = await Permissions(owned).ForAsync(userId);

        Assert.Empty(held.LevelNames);
        Assert.Equal(
            DataScopeKind.None,
            AccessDecision.Resolve(held.Permissions, Employees, PermissionAction.Delete).Kind);
    }

    [SkippableFact]
    public async Task ADraftLevelGrantsNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await GiveLevelAsync(fixture, tenantId, userId, "Half Built", DataScope.Everything,
            AccessLevelStatus.Draft, PermissionAction.View);

        Assert.Empty((await Permissions(owned).ForAsync(userId)).LevelNames);
    }

    // Two levels combine: what they permit is unioned, how far they reach is not.
    [SkippableFact]
    public async Task TwoLevelsUnionActionsAndKeepTheNarrowerReach()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await GiveLevelAsync(fixture, tenantId, userId, "Line Manager",
            new DataScope { Kind = DataScopeKind.DirectReports },
            AccessLevelStatus.Active, PermissionAction.View);

        await GiveLevelAsync(fixture, tenantId, userId, "Recruiter", DataScope.Everything,
            AccessLevelStatus.Active, PermissionAction.Export);

        ResolvedPermissions held = await Permissions(owned).ForAsync(userId);

        Assert.Equal(["Line Manager", "Recruiter"], held.LevelNames);

        // Both actions, but only as far as the narrower level reaches.
        Assert.Equal(
            DataScopeKind.DirectReports,
            AccessDecision.Resolve(held.Permissions, Employees, PermissionAction.Export).Kind);
    }

    // Nothing assigned is not a lockout: the self-service floor still reaches their
    // own record, which is what keeps a tenant that deleted the wrong level usable.
    [SkippableFact]
    public async Task SomeoneWithNoLevelStillReachesTheirOwnRecord()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        ResolvedPermissions held = await Permissions(owned).ForAsync(userId);

        Assert.Empty(held.LevelNames);
        Assert.Equal(
            DataScopeKind.Self,
            AccessDecision.Resolve(held.Permissions, Employees, PermissionAction.View).Kind);
    }

    [SkippableFact]
    public async Task TheOwnerIsRecognisedWithoutHoldingAnything()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid userId, ServiceProvider provider) = await ArrangeAsync(isOwner: true);
        await using ServiceProvider owned = provider;

        ResolvedPermissions held = await Permissions(owned).ForAsync(userId);

        Assert.True(held.IsOwner);
        Assert.Empty(held.LevelNames);
    }

    // The resolver ignores tenant filters, because it runs before a tenant is
    // established. The user id has to be the whole constraint.
    [SkippableFact]
    public async Task AnotherTenantsLevelIsNotPickedUp()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await GiveLevelAsync(fixture, tenantId, userId, "Mine", DataScope.Everything,
            AccessLevelStatus.Active, PermissionAction.View);

        Guid other = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = other,
                Name = "Globex",
                Slug = $"globex-{other:N}",
                CountryCode = CountryCode.UnitedKingdom,
            });

            await seed.SaveChangesAsync();
        }

        // A level in another company, assigned to nobody here.
        await using (MoteeDbContext seed = fixture.CreateContext(other))
        {
            seed.AccessLevels.Add(new AccessLevel
            {
                Id = Guid.NewGuid(),
                TenantId = other,
                Name = "Theirs",
                Kind = AccessLevelKind.Custom,
                Status = AccessLevelStatus.Active,
                Scope = DataScope.Everything,
                Permissions = [],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        Assert.Equal(["Mine"], (await Permissions(owned).ForAsync(userId)).LevelNames);
    }
}
