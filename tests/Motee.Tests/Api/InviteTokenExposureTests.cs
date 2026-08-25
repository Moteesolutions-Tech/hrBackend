using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Integration;

namespace Motee.Tests.Api;

// The raw join token is what lets someone set another person's password. Only its hash
// is stored, so the plaintext is meant to exist in one place - the email - and nowhere
// a response body can carry it: devtools, proxy logs, frontend error reporting.
//
// It used to be returned whenever the environment was not Production, which included
// the deployed Staging host. These pin that it is absent unless the process is a
// developer's own machine.
[Collection(PostgresCollection.Name)]
public class InviteTokenExposureTests(PostgresFixture fixture)
{
    private async Task<(Guid EmployeeId, ApiFactory Api)> ArrangeAsync()
    {
        await fixture.ResetAsync();

        Guid tenantId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Guid employeeId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Acme",
                Slug = $"acme-{tenantId:N}",
                CountryCode = CountryCode.Nigeria,
            });

            // Owner, so the permission check is not what the test is measuring.
            seed.Users.Add(new ApplicationUser
            {
                Id = userId,
                TenantId = tenantId,
                FirstName = "Ada",
                LastName = "Okafor",
                Email = "ada@acme.com",
                UserName = "ada@acme.com",
                NormalizedEmail = "ADA@ACME.COM",
                IsOwner = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        await using (MoteeDbContext context = fixture.CreateContext(tenantId))
        {
            context.Employees.Add(new Employee
            {
                Id = employeeId,
                TenantId = tenantId,
                FirstName = "Ben",
                LastName = "Adeyemi",
                Email = "ben@acme.com",
                Status = EmployeeStatus.Onboarded,
                OnboardingMethod = OnboardingMethod.Manual,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await context.SaveChangesAsync();
        }

        return (employeeId, new ApiFactory(fixture.ConnectionString!)
        {
            ActingAs = userId,
            ActingTenant = tenantId,
        });
    }

    // The test host runs as Development with App:Debug unset, which is the same state a
    // deployed process is in: debug mode off. The token must not come back.
    [SkippableFact]
    public async Task TheJoinTokenIsWithheldWhenDebugModeIsOff()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid employeeId, ApiFactory api) = await ArrangeAsync();
        using ApiFactory owned = api;

        HttpResponseMessage response = await owned.CreateClient()
            .PostAsync($"/api/v1/employees/{employeeId}/invitation", content: null);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{(int)response.StatusCode}: {string.Join(" | ", owned.Failures)}");

        JsonElement data = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data");

        Assert.Equal(
            JsonValueKind.Null,
            data.GetProperty("joinToken").ValueKind);
    }

    // The invitation still has to be issued - the token is delivered by email rather
    // than withheld altogether, so the flow works, it just stops short-circuiting the
    // mailbox.
    [SkippableFact]
    public async Task TheInvitationIsStillIssued()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid employeeId, ApiFactory api) = await ArrangeAsync();
        using ApiFactory owned = api;

        HttpResponseMessage response = await owned.CreateClient()
            .PostAsync($"/api/v1/employees/{employeeId}/invitation", content: null);

        JsonElement data = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data");

        Assert.NotEqual(JsonValueKind.Null, data.GetProperty("expiresAt").ValueKind);

        await using MoteeDbContext context = fixture.CreateContext();
        Assert.NotEmpty(context.EmployeeInvitations.IgnoreQueryFilters().ToList());
    }
}
