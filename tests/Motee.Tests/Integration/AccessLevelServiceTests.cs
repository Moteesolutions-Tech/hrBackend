using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Authorization;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class AccessLevelServiceTests(PostgresFixture fixture)
{
    private const string Employees = "organization.employees";

    private async Task<(Guid UserId, ServiceProvider Provider)> ArrangeAsync()
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
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = tenantId;

        return (userId, provider);
    }

    private static IAccessLevelService Levels(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IAccessLevelService>();

    private static AccessLevelRequest Request(
        string name = "Payroll Clerk",
        params PermissionAction[] actions) => new()
    {
        Name = name,
        Scope = DataScope.Everything,
        Permissions =
        [
            new ModulePermission
            {
                Module = Employees,
                Access = true,
                Actions = actions.Length > 0 ? actions : [PermissionAction.View],
            },
        ],
    };

    // A level live the moment it is created is how someone is handed payroll access
    // by accident, five clicks in.
    [SkippableFact]
    public async Task ANewLevelStartsAsDraft()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        AccessLevelResult created = await Levels(owned).CreateAsync(Request());

        Assert.True(created.Succeeded, created.Outcome.ToString());
        Assert.Equal(AccessLevelStatus.Draft, created.AccessLevel!.Status);
        Assert.Equal(AccessLevelKind.Custom, created.AccessLevel.Kind);
    }

    // Stored expanded, so the row explains its own grants. An audit reading "approve"
    // without "view" has no way to know the evaluator supplies it.
    [SkippableFact]
    public async Task DependenciesAreStoredNotInferred()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        AccessLevelResult created = await Levels(owned)
            .CreateAsync(Request(actions: PermissionAction.Administer));

        IReadOnlyCollection<PermissionAction> actions =
            created.AccessLevel!.Permissions.Single().Actions;

        Assert.Contains(PermissionAction.View, actions);
        Assert.Contains(PermissionAction.Edit, actions);
    }

    [SkippableFact]
    public async Task TwoLevelsCannotShareAName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Levels(owned).CreateAsync(Request("HR Manager"));

        Assert.Equal(
            AccessLevelOutcome.DuplicateName,
            (await Levels(owned).CreateAsync(Request("hr manager"))).Outcome);
    }

    // A module the catalogue does not contain can never be evaluated, so storing it
    // would be a permission that silently does nothing for ever.
    [SkippableFact]
    public async Task AnUnknownModuleIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        AccessLevelRequest bad = Request() with
        {
            Permissions =
            [
                new ModulePermission
                {
                    Module = "not.a.module",
                    Access = true,
                    Actions = [PermissionAction.View],
                },
            ],
        };

        Assert.Equal(AccessLevelOutcome.UnknownModule, (await Levels(owned).CreateAsync(bad)).Outcome);
    }

    // Draft is unfinished. Handing it to someone would grant nothing while looking
    // like it had worked.
    [SkippableFact]
    public async Task ADraftLevelCannotBeAssigned()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid levelId = (await Levels(owned).CreateAsync(Request())).AccessLevel!.Id;

        Assert.Equal(
            AccessLevelOutcome.NotAssignable,
            (await Levels(owned).AssignAsync(userId, levelId)).Outcome);
    }

    [SkippableFact]
    public async Task AnActivatedLevelCanBeAssignedAndTakesEffectAtOnce()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid levelId = (await Levels(owned)
            .CreateAsync(Request("Payroll Clerk", PermissionAction.View, PermissionAction.Delete)))
            .AccessLevel!.Id;

        await Levels(owned).ChangeStatusAsync(levelId, AccessLevelStatus.Active);
        await Levels(owned).AssignAsync(userId, levelId);

        IUserPermissions permissions = owned.CreateScope().ServiceProvider
            .GetRequiredService<IUserPermissions>();

        Assert.Equal(
            DataScopeKind.All,
            AccessDecision.Resolve(
                (await permissions.ForAsync(userId)).Permissions,
                Employees,
                PermissionAction.Delete).Kind);
    }

    // Deactivating has to bite immediately, or it is a label rather than a control.
    // The cache is evicted for every holder rather than left to lapse.
    [SkippableFact]
    public async Task DeactivatingRevokesWithoutWaitingForTheCache()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid levelId = (await Levels(owned)
            .CreateAsync(Request("Payroll Clerk", PermissionAction.View, PermissionAction.Delete)))
            .AccessLevel!.Id;

        await Levels(owned).ChangeStatusAsync(levelId, AccessLevelStatus.Active);
        await Levels(owned).AssignAsync(userId, levelId);

        IUserPermissions permissions = owned.CreateScope().ServiceProvider
            .GetRequiredService<IUserPermissions>();

        // Resolved once, so it is now cached.
        Assert.NotEmpty((await permissions.ForAsync(userId)).LevelNames);

        await Levels(owned).ChangeStatusAsync(levelId, AccessLevelStatus.Inactive);

        Assert.Empty((await permissions.ForAsync(userId)).LevelNames);
    }

    // Deleting a level out from under the people holding it is not something anyone
    // means to do. Deactivating says the same thing without the data loss.
    [SkippableFact]
    public async Task ALevelInUseCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid levelId = (await Levels(owned).CreateAsync(Request())).AccessLevel!.Id;

        await Levels(owned).ChangeStatusAsync(levelId, AccessLevelStatus.Active);
        await Levels(owned).AssignAsync(userId, levelId);

        Assert.Equal(
            AccessLevelOutcome.InUse,
            (await Levels(owned).DeleteAsync(levelId)).Outcome);

        // Withdrawn, and now it goes.
        await Levels(owned).WithdrawAsync(userId, levelId);

        Assert.True((await Levels(owned).DeleteAsync(levelId)).Succeeded);
    }

    // Duplicating is what makes seven shipped levels enough: a niche role is a
    // two-minute edit of the closest match rather than a blank 42-module grid.
    [SkippableFact]
    public async Task ALevelCanStartAsACopyOfAnother()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        AccessLevelResult source = await Levels(owned)
            .CreateAsync(Request("Read-only") with { Description = "Sees everything." });

        AccessLevelResult copy = await Levels(owned).CreateAsync(
            Request("Health & Safety Officer") with { CopyFromId = source.AccessLevel!.Id });

        Assert.True(copy.Succeeded, copy.Outcome.ToString());
        Assert.Equal("Sees everything.", copy.AccessLevel!.Description);
    }

    [SkippableFact]
    public async Task AssignedCountReflectsWhoHoldsIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid userId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid levelId = (await Levels(owned).CreateAsync(Request())).AccessLevel!.Id;

        await Levels(owned).ChangeStatusAsync(levelId, AccessLevelStatus.Active);
        await Levels(owned).AssignAsync(userId, levelId);

        Assert.Equal(1, (await Levels(owned).GetAsync(levelId))!.AssignedCount);
    }
}
