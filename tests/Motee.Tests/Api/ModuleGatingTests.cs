using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Settings;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Integration;

namespace Motee.Tests.Api;

// Switching a module off has to close its endpoints, not merely hide a nav
// item.
//
// This is the whole point of the module switches, and the reason they are
// tested through the real pipeline rather than against the service: the gate is
// in the permission handler, and a service test would pass whether or not the
// handler consulted it. The onboarding wizard has written these choices since
// it shipped and nothing had ever read them — a company could switch Training
// off and every training endpoint stayed open.
//
// Written before the gate.
[Collection(PostgresCollection.Name)]
public class ModuleGatingTests(PostgresFixture fixture)
{
    private const string TrainingEndpoint = "/api/v1/learning/courses";
    private const string EmployeesEndpoint = "/api/v1/employees";

    [SkippableFact]
    public async Task AnEnabledModuleAnswers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        using ApiFactory api = await ArrangeAsync(enabled: ["learning"]);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(api, TrainingEndpoint));
    }

    [SkippableFact]
    public async Task ADisabledModuleStopsAnswering()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        using ApiFactory api = await ArrangeAsync(enabled: ["attendance"]);

        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(api, TrainingEndpoint));
    }

    // The owner bypasses every permission check. A module switched off is not a
    // permission they are missing — it is a part of the product this company
    // does not have — so the bypass must not reach past it. Otherwise the one
    // person most likely to be testing the switch is the one person it does not
    // apply to.
    [SkippableFact]
    public async Task TheOwnerDoesNotBypassADisabledModule()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        using ApiFactory api = await ArrangeAsync(enabled: ["attendance"], isOwner: true);

        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(api, TrainingEndpoint));
    }

    [SkippableFact]
    public async Task AModuleNoFeatureGovernsKeepsAnswering()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        using ApiFactory api = await ArrangeAsync(enabled: ["attendance"]);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(api, EmployeesEndpoint));
    }

    // The way back from switching everything off. Gating this would let a
    // company lose the screen that switches things on again.
    [SkippableFact]
    public async Task SettingsKeepsAnsweringWithEverythingSwitchedOff()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        using ApiFactory api = await ArrangeAsync(enabled: ["employee-management"]);

        Assert.Equal(
            HttpStatusCode.OK, await StatusAsync(api, "/api/v1/settings/modules"));
    }

    // The sidebar is built from what /auth/me returns. If the matrix came back
    // unfiltered the screen would render a Training nav item whose every
    // request fails, which reads as a permissions bug and is not one.
    [SkippableFact]
    public async Task ADisabledModuleIsNotInThePermissionMatrix()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        using ApiFactory api = await ArrangeAsync(enabled: ["attendance"]);

        JsonElement me = await MeAsync(api);

        string[] modules =
        [
            .. me.GetProperty("permissions")
                .EnumerateArray()
                .Select(module => module.GetProperty("module").GetString()!),
        ];

        Assert.DoesNotContain("talent.training", modules);
        Assert.Contains("time-payroll.attendance", modules);
    }

    [SkippableFact]
    public async Task TheEnabledModulesComeBackWithTheMatrix()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        using ApiFactory api = await ArrangeAsync(enabled: ["attendance"]);

        JsonElement me = await MeAsync(api);

        string[] enabled =
        [
            .. me.GetProperty("enabledModules").EnumerateArray().Select(id => id.GetString()!),
        ];

        Assert.Contains("attendance", enabled);
        Assert.DoesNotContain("learning", enabled);

        // Essential whatever was chosen: there is no product without it.
        Assert.Contains("employee-management", enabled);
    }

    [SkippableFact]
    public async Task ADisabledModuleIsNotInTheSidebar()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        using ApiFactory api = await ArrangeAsync(enabled: ["attendance"]);

        string[] sidebar =
        [
            .. (await MeAsync(api)).GetProperty("sidebarModules")
                .EnumerateArray()
                .Select(id => id.GetString()!),
        ];

        Assert.DoesNotContain("learning", sidebar);
    }

    // A tenant mid-registration has written nothing yet. Reaching no module at
    // all would present as every screen refusing.
    [SkippableFact]
    public async Task ACompanyThatHasChosenNothingGetsTheDefaults()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        using ApiFactory api = await ArrangeAsync(enabled: []);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(api, "/api/v1/leave/types"));
    }

    private static async Task<HttpStatusCode> StatusAsync(ApiFactory api, string route) =>
        (await api.CreateClient().GetAsync(route)).StatusCode;

    private static async Task<JsonElement> MeAsync(ApiFactory api)
    {
        HttpResponseMessage response = await api.CreateClient().GetAsync("/api/v1/auth/me");

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{(int)response.StatusCode}: {string.Join(" | ", api.Failures)}");

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
    }

    private async Task<ApiFactory> ArrangeAsync(
        IReadOnlyList<string> enabled,
        bool isOwner = false)
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
                Settings = new TenantSettings { EnabledModules = enabled },
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

        // Everything this test reaches for, so a refusal is the module gate
        // rather than a missing grant.
        string[] granted =
        [
            "talent.training",
            "time-payroll.attendance",
            "time-payroll.leave",
            "organization.employees",
            "admin.settings",
        ];

        await using (MoteeDbContext context = fixture.CreateContext(tenantId))
        {
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
                    .. granted.Select(module => new ModulePermission
                    {
                        Module = module,
                        Access = true,
                        Actions = [PermissionAction.View, PermissionAction.Edit],
                    }),
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

        return new ApiFactory(fixture.ConnectionString!)
        {
            ActingAs = userId,
            ActingTenant = tenantId,
        };
    }
}
