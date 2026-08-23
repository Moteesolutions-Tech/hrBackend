using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Integration;

namespace Motee.Tests.Api;

// GET /auth/me is what the frontend builds its whole interface from: which nav items
// appear, which buttons render, whether the setup wizard opens.
//
// It returned an empty permission matrix to every user for a while, because it still
// derived permissions from the role claim after those moved into access levels. Every
// service test passed throughout. These are the tests that would have caught it.
[Collection(PostgresCollection.Name)]
public class CurrentUserEndpointTests(PostgresFixture fixture)
{
    private async Task<(Guid UserId, ApiFactory Api)> ArrangeAsync(
        bool isOwner = false,
        bool withLevel = true)
    {
        await fixture.ResetAsync();

        Guid tenantId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Guid levelId = Guid.NewGuid();

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

        if (withLevel)
        {
            await using MoteeDbContext context = fixture.CreateContext(tenantId);

            context.AccessLevels.Add(new AccessLevel
            {
                Id = levelId,
                TenantId = tenantId,
                Name = "HR Admin",
                Kind = AccessLevelKind.Custom,
                Status = AccessLevelStatus.Active,
                Scope = DataScope.Everything,
                Permissions =
                [
                    new ModulePermission
                    {
                        Module = "organization.employees",
                        Access = true,
                        Actions = [PermissionAction.View, PermissionAction.Delete],
                    },
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
        }

        return (userId, new ApiFactory(fixture.ConnectionString!) { ActingAs = userId });
    }

    private static async Task<JsonElement> MeAsync(ApiFactory api)
    {
        HttpResponseMessage response = await api.CreateClient().GetAsync("/api/v1/auth/me");

        // The body on failure, or a 500 here is a stack trace nobody can see.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{(int)response.StatusCode}: {string.Join(" | ", api.Failures)}");

        JsonElement envelope = await response.Content.ReadFromJsonAsync<JsonElement>();

        return envelope.GetProperty("data");
    }

    // The exact failure that shipped: an empty matrix, which the frontend renders as
    // an interface with every action hidden.
    [SkippableFact]
    public async Task PermissionsComeBackForSomeoneHoldingALevel()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ApiFactory api) = await ArrangeAsync();
        using ApiFactory owned = api;

        JsonElement me = await MeAsync(owned);

        Assert.NotEmpty(me.GetProperty("permissions").EnumerateArray());
        Assert.Equal(["HR Admin"], me.GetProperty("accessLevels")
            .EnumerateArray().Select(level => level.GetString()));
    }

    // The names behind the merge, so an admin asking why someone can delete records
    // gets an answer rather than a combined matrix.
    [SkippableFact]
    public async Task TheGrantedActionsAreTheOnesTheLevelPermits()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ApiFactory api) = await ArrangeAsync();
        using ApiFactory owned = api;

        JsonElement employees = (await MeAsync(owned))
            .GetProperty("permissions")
            .EnumerateArray()
            .Single(module => module.GetProperty("module").GetString() == "organization.employees");

        string[] actions =
            [.. employees.GetProperty("actions").EnumerateArray().Select(a => a.GetString()!)];

        Assert.Contains("view", actions);
        Assert.Contains("delete", actions);
    }

    // Scope moved to the level, so it arrives as an object carrying the ids a named
    // kind covers.
    [SkippableFact]
    public async Task TheScopeArrivesAsAnObject()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ApiFactory api) = await ArrangeAsync();
        using ApiFactory owned = api;

        JsonElement scope = (await MeAsync(owned)).GetProperty("scope");

        Assert.Equal("all", scope.GetProperty("kind").GetString());
        Assert.True(scope.TryGetProperty("departmentIds", out _));
    }

    // The owner bypasses every check, so the UI must not hide things from them on the
    // strength of an empty matrix.
    [SkippableFact]
    public async Task TheOwnerIsFlaggedEvenHoldingNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ApiFactory api) = await ArrangeAsync(isOwner: true, withLevel: false);
        using ApiFactory owned = api;

        JsonElement me = await MeAsync(owned);

        Assert.True(me.GetProperty("isOwner").GetBoolean());
        Assert.Empty(me.GetProperty("accessLevels").EnumerateArray());
    }

    // Holding nothing is not a lockout — the self-service floor still applies — but it
    // is an empty matrix, and the flag above is what tells the two apart.
    [SkippableFact]
    public async Task SomeoneHoldingNothingIsNotTheOwner()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ApiFactory api) = await ArrangeAsync(withLevel: false);
        using ApiFactory owned = api;

        JsonElement me = await MeAsync(owned);

        Assert.False(me.GetProperty("isOwner").GetBoolean());
        Assert.Empty(me.GetProperty("accessLevels").EnumerateArray());
    }

    [SkippableFact]
    public async Task WithoutAuthenticationItIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ApiFactory api) = await ArrangeAsync();
        using ApiFactory owned = api;

        owned.ActingAs = null;

        HttpResponseMessage response = await owned.CreateClient().GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
