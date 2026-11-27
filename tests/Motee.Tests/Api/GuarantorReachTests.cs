using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Onboarding;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Integration;

namespace Motee.Tests.Api;

// Whose guarantors somebody may read.
//
// At the API layer deliberately, because the mistake this guards against lives in the
// controller and not the service. GET /employees/{id}/guarantors carries
// [RequiresPermission("organization.employees", View)] — and SelfServicePolicy grants
// that to every signed-in employee at Self scope, so the attribute alone lets anybody
// past. It says they may read an employee, not which one.
//
// Without the reachability narrowing, any member of staff could read any colleague's
// guarantors by changing the id in the URL. A guarantor is an external person — usually
// a relative — and the record holds their name, relationship, address and phone number.
//
// The same narrowing is what makes the endpoint serve self-service: Self scope reaches
// the caller's own record, so My Profile needs no separate /employees/me route.
[Collection(PostgresCollection.Name)]
public class GuarantorReachTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _bolaId;
    private Guid _adaId;
    private Guid _bolaUserId;

    // Bola holds no access level at all, so the only thing letting them near this
    // endpoint is the self-service floor — which is the case under test.
    private async Task<ApiFactory> ArrangeAsync()
    {
        await fixture.ResetAsync();

        _tenantId = Guid.NewGuid();
        _bolaUserId = Guid.NewGuid();
        _bolaId = Guid.NewGuid();
        _adaId = Guid.NewGuid();

        Guid departmentId = Guid.NewGuid();

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

        await using (MoteeDbContext scoped = fixture.CreateContext(_tenantId))
        {
            scoped.Departments.Add(new Department
            {
                Id = departmentId,
                TenantId = _tenantId,
                Name = "Engineering",
                Code = "ENG",
            });

            Add(scoped, _bolaId, departmentId, "Bola");
            Add(scoped, _adaId, departmentId, "Ada");

            scoped.Users.Add(new ApplicationUser
            {
                Id = _bolaUserId,
                TenantId = _tenantId,
                FirstName = "Bola",
                LastName = "Okafor",
                Email = "bola@acme.com",
                UserName = "bola@acme.com",
                NormalizedEmail = "BOLA@ACME.COM",
                EmployeeId = _bolaId,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await scoped.SaveChangesAsync();
        }

        return new ApiFactory(fixture.ConnectionString!)
        {
            ActingAs = _bolaUserId,
            ActingTenant = _tenantId,

            // What a real token carries. Self scope reaches nobody without it, so
            // leaving it off would make every case below a 404 and the leak test pass
            // for the wrong reason.
            ActingEmployee = _bolaId,
        };
    }

    // An employee, their onboarding record, and one guarantor on it.
    private void Add(MoteeDbContext seed, Guid employeeId, Guid departmentId, string first)
    {
        seed.Employees.Add(new Employee
        {
            Id = employeeId,
            TenantId = _tenantId,
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            DepartmentId = departmentId,
            Status = EmployeeStatus.Active,
        });

        Guid recordId = Guid.NewGuid();

        seed.OnboardingRecords.Add(new OnboardingRecord
        {
            Id = recordId,
            TenantId = _tenantId,
            EmployeeId = employeeId,
            Stage = OnboardingStage.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        seed.Guarantors.Add(new Guarantor
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            OnboardingRecordId = recordId,
            Position = 1,
            Name = $"{first}'s uncle",
            Relationship = "Uncle",
            Occupation = "Teacher",
            Address = "12 Allen Avenue, Ikeja",
            Phone = "08098765432",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        JsonElement envelope = await response.Content.ReadFromJsonAsync<JsonElement>();

        return envelope.GetProperty("data");
    }

    [SkippableFact]
    public async Task AnEmployeeCanReadTheirOwnGuarantors()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync($"/api/v1/employees/{_bolaId:D}/guarantors");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement data = await DataAsync(response);

        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("Bola's uncle", data[0].GetProperty("name").GetString());
    }

    // The leak. Bola holds nothing but the self-service floor, and that floor is
    // enough to satisfy the permission attribute — so only the reachability check
    // stands between them and Ada's next of kin.
    [SkippableFact]
    public async Task AnEmployeeCannotReadAColleaguesGuarantors()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync($"/api/v1/employees/{_adaId:D}/guarantors");

        // Not found rather than forbidden, matching the profile route: telling
        // somebody an id exists but is none of their business is itself a fact
        // about the directory.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("uncle", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("08098765432", body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AnEmployeeWhoDoesNotExistIsNotFound()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync($"/api/v1/employees/{Guid.NewGuid():D}/guarantors");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
