using System.Net;
using Motee.Domain.Common;
using Motee.Domain.Contracts;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Domain.Organisation;
using Motee.Domain.Settings;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Integration;

namespace Motee.Tests.Api;

// Who may read an organisation-wide list.
//
// Every module here is on the self-service floor, and that floor grants View at Self
// scope to any signed-in employee with no access level at all. The permission handler
// succeeds on any scope above None, so the attribute alone lets every member of staff
// past — it says they may read contracts, not whose.
//
// These endpoints then hand their query straight to ListAsync, which takes no scope.
// The result is that an ordinary employee calling GET /contracts reads the whole
// company's pay, and the same shape repeats across leave, attendance, documents,
// training and the rest. Each module's own MineAsync route is what self-service is for.
//
// At the API layer deliberately, like GuarantorReachTests: the mistake lives in the
// controller, so a service test would pass while the hole stayed open.
[Collection(PostgresCollection.Name)]
public class OrganisationWideListReachTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _bolaId;
    private Guid _adaId;
    private Guid _bolaUserId;

    // Bola holds no access level whatsoever. The only thing letting them near any of
    // these endpoints is the self-service floor, which is the case under test.
    private async Task<ApiFactory> ArrangeAsync()
    {
        await fixture.ResetAsync();

        _tenantId = Guid.NewGuid();
        _bolaUserId = Guid.NewGuid();
        _bolaId = Guid.NewGuid();
        _adaId = Guid.NewGuid();

        Guid departmentId = Guid.NewGuid();
        Guid leaveTypeId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = _tenantId,
                Name = "Acme",
                Slug = $"acme-{_tenantId:N}",
                CountryCode = CountryCode.Nigeria,

                // Every feature on, deliberately. The permission handler asks whether
                // the module is enabled before it asks anything about permissions, and
                // a bare tenant gets only the default set — so without this, half these
                // endpoints answer 403 because the feature is off and the test would
                // pass while the hole stayed open.
                Settings = new TenantSettings
                {
                    EnabledModules = [.. FeatureCatalogue.All.Select(feature => feature.Id)],
                },
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

        // A second save, because the rows below point at the employees above by foreign
        // key and EF orders inserts within one SaveChanges by its own graph walk, not by
        // the order they were added.
        await using (MoteeDbContext scoped = fixture.CreateContext(_tenantId))
        {
            // Ada's pay, which is the thing that must not travel.
            scoped.Contracts.Add(new Contract
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                EmployeeId = _adaId,
                Title = "Ada's employment contract",
                ContractType = ContractType.Employment,
                Status = ContractStatus.Active,
                StartDate = new DateOnly(2021, 1, 10),
                Salary = 7_654_321m,
                Currency = "NGN",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            scoped.LeaveTypes.Add(new LeaveType
            {
                Id = leaveTypeId,
                TenantId = _tenantId,
                Name = "Annual",
                Code = "ANN",
            });

            await scoped.SaveChangesAsync();
        }

        await using (MoteeDbContext scoped = fixture.CreateContext(_tenantId))
        {
            // Ada's leave, with a reason of the kind people actually write.
            scoped.LeaveRequests.Add(new LeaveRequest
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                EmployeeId = _adaId,
                LeaveTypeId = leaveTypeId,
                StartDate = new DateOnly(2026, 3, 2),
                EndDate = new DateOnly(2026, 3, 6),
                TotalDays = 5,
                Status = LeaveRequestStatus.Approved,
                Reason = "Chemotherapy appointments",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await scoped.SaveChangesAsync();
        }

        return new ApiFactory(fixture.ConnectionString!)
        {
            ActingAs = _bolaUserId,
            ActingTenant = _tenantId,
            ActingEmployee = _bolaId,
        };
    }

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
    }

    // The disclosure, stated as plainly as it can be: a colleague's salary, in the
    // response body of a request any employee can make.
    [SkippableFact]
    public async Task AnOrdinaryEmployeeCannotReadAColleaguesPay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient().GetAsync("/api/v1/contracts");

        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("7654321", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Ada's employment contract", body, StringComparison.Ordinal);
    }

    // A leave reason is often medical. The same request shape, the same hole.
    [SkippableFact]
    public async Task AnOrdinaryEmployeeCannotReadAColleaguesLeaveReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient().GetAsync("/api/v1/leave/requests");

        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Chemotherapy", body, StringComparison.OrdinalIgnoreCase);
    }

    // The breadth of it. Each of these is the organisation-wide list for a module the
    // self-service floor grants, and each module has its own /mine route for the
    // employee's own records — so refusing here takes nothing away from self-service.
    [SkippableTheory]
    [InlineData("/api/v1/contracts")]
    [InlineData("/api/v1/leave/requests")]
    [InlineData("/api/v1/attendance")]
    [InlineData("/api/v1/documents")]
    [InlineData("/api/v1/expenses/claims")]
    public async Task AnOrdinaryEmployeeIsRefusedTheOrganisationWideList(string path)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient().GetAsync(path);

        // Forbidden rather than not-found: unlike a single record by id, the existence
        // of the company's leave register is not the secret — being allowed to read it
        // is. And a 404 here would send a client looking for a typo in its own URL.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // The rest of the floor, swept in one go.
    //
    // Every one of these is an organisation-wide list on a module SelfServicePolicy
    // grants at Self scope, so each is the same shape as the five above and has to be
    // checked rather than assumed. Kept as its own case so a new one failing names
    // itself, and so the list is a standing inventory of what the sweep covered.
    [SkippableTheory]
    [InlineData("/api/v1/performance/reviews")]
    [InlineData("/api/v1/performance/goals")]
    [InlineData("/api/v1/performance/feedback")]
    [InlineData("/api/v1/certifications")]
    [InlineData("/api/v1/leave/balances")]
    [InlineData("/api/v1/attendance/timesheets")]
    [InlineData("/api/v1/attendance/corrections")]
    [InlineData("/api/v1/approvals")]
    public async Task NoOtherSelfServiceModuleExposesItsWholeRegister(string path)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // The deliberate exception, pinned so nobody closes it by sweeping the module.
    //
    // The course catalogue is not personal data. It is the list of training a company
    // offers, and an employee has to be able to read it to enrol — their own enrolments
    // are learning/mine. Gating it on more than Self would be self-service refusing the
    // thing self-service exists for.
    [SkippableFact]
    public async Task TheCourseCatalogueStaysReadableByStaff()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync("/api/v1/learning/courses");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
