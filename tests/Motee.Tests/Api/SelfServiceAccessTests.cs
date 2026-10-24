using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Integration;

namespace Motee.Tests.Api;

// Who the self-service endpoints are for.
//
// Everyone who works here, whatever they do: the HR admin, a joiner on their first
// morning, somebody holding no access level at all. That is why these endpoints carry
// no permission attribute — requiring the leave module to see your own remaining days
// would mean granting every member of staff the screen listing the whole company's.
//
// The owner is the single account they cannot serve, because they are not an employee
// and so are not the subject of leave, onboarding or anyone's approvals. They used to
// get a 404 reading "No employee record found", which describes an absence and reads
// as a fault. Now they get a code the client can act on.
//
// At the API layer deliberately: the service tests below this all passed while the
// medical gate was refusing the account owner.
[Collection(PostgresCollection.Name)]
public class SelfServiceAccessTests(PostgresFixture fixture)
{
    private const string NotAnEmployee = "1006";

    // Every self-service address, so a new one added without the filter shows up here
    // rather than in production as an owner's broken screen.
    public static TheoryData<string> SelfServiceEndpoints =>
    [
        "/api/v1/leave/requests/mine",
        "/api/v1/leave/balances/mine",
        "/api/v1/onboarding/mine",
        "/api/v1/approval-delegations/mine",
    ];

    private async Task<ApiFactory> ArrangeAsync(bool asOwner)
    {
        await fixture.ResetAsync();

        Guid tenantId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Guid employeeId = Guid.NewGuid();
        Guid departmentId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Acme",
                Slug = $"acme-{tenantId:N}",
                CountryCode = CountryCode.Nigeria,
            });

            await seed.SaveChangesAsync();
        }

        await using (MoteeDbContext scoped = fixture.CreateContext(tenantId))
        {
            if (!asOwner)
            {
                scoped.Departments.Add(new Department
                {
                    Id = departmentId,
                    TenantId = tenantId,
                    Name = "Engineering",
                    Code = "ENG",
                });

                scoped.Employees.Add(new Employee
                {
                    Id = employeeId,
                    TenantId = tenantId,
                    FirstName = "Bola",
                    LastName = "Okafor",
                    Email = "bola@acme.com",
                    DepartmentId = departmentId,
                    Status = EmployeeStatus.Active,
                });
            }

            scoped.Users.Add(new ApplicationUser
            {
                Id = userId,
                TenantId = tenantId,
                FirstName = asOwner ? "Ada" : "Bola",
                LastName = "Okafor",
                Email = asOwner ? "ada@acme.com" : "bola@acme.com",
                UserName = asOwner ? "ada@acme.com" : "bola@acme.com",
                NormalizedEmail = asOwner ? "ADA@ACME.COM" : "BOLA@ACME.COM",
                IsOwner = asOwner,

                // The whole distinction, in one column.
                EmployeeId = asOwner ? null : employeeId,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await scoped.SaveChangesAsync();
        }

        return new ApiFactory(fixture.ConnectionString!)
        {
            ActingAs = userId,
            ActingTenant = tenantId,
        };
    }

    [SkippableTheory]
    [MemberData(nameof(SelfServiceEndpoints))]
    public async Task TheOwnerIsTurnedAwayFromSelfService(string url)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync(asOwner: true);

        HttpResponseMessage response = await api.CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        JsonElement envelope = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The code, not the status. A bare 403 is indistinguishable from a permission
        // the owner is missing — which would send somebody looking for one to grant,
        // and there is none, because they already hold everything.
        Assert.Equal(NotAnEmployee, envelope.GetProperty("responseCode").GetString());
        Assert.False(envelope.GetProperty("success").GetBoolean());

        // Says what the account is rather than what is missing from it.
        Assert.DoesNotContain(
            "not found",
            envelope.GetProperty("message").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    // These had no authorization attribute and the application sets no fallback policy,
    // so they answered anonymous callers — the delegation list with an empty 200.
    // Nothing leaked, because nothing resolves without a user, but that was a property
    // of the queries rather than a decision anybody made.
    //
    // 401 before the employee check, so a stranger is not told what an owner account is
    // either.
    [SkippableTheory]
    [MemberData(nameof(SelfServiceEndpoints))]
    public async Task AnAnonymousCallerIsTurnedAwayFirst(string url)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await fixture.ResetAsync();

        using ApiFactory api = new(fixture.ConnectionString!);

        HttpResponseMessage response = await api.CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(
            "manages the company",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    // The other half, and the one worth guarding: the filter must not close these to
    // the people they exist for. An employee holding no access level at all still
    // reaches every one of them.
    [SkippableTheory]
    [MemberData(nameof(SelfServiceEndpoints))]
    public async Task AnEmployeeWithNoAccessLevelStillReachesThem(string url)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync(asOwner: false);

        HttpResponseMessage response = await api.CreateClient().GetAsync(url);

        JsonElement envelope = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Not necessarily 200 — /onboarding/mine is a genuine 404 for somebody with no
        // onboarding record. Anything else means they were turned away, and asserting
        // merely "not 403" would let a 401 pass as though they had got through.
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"{url} returned {(int)response.StatusCode}: {envelope}");

        Assert.NotEqual(NotAnEmployee, envelope.GetProperty("responseCode").GetString());
    }
}
