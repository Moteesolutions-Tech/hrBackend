using System.Net;
using Motee.Domain.Attendance;
using Motee.Domain.Authorization;
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

// What a department-scoped HR user sees in each register.
//
// The other half of the same bug. Gating these endpoints above Self stopped every
// employee reading them, but the registers themselves took no scope at all — so an HR
// user whose access level reaches only Engineering still got Sales' pay, absence and
// attendance, which is the data their own employee list correctly hides from them.
//
// Hana holds one level, scoped to Engineering. Ada is in Engineering and Zara is in
// Sales, and each has a contract, a leave request and an attendance record, so every
// register below has one row Hana may see and one she may not. Asserting on both
// directions matters: a narrowing that returned nothing would pass a test that only
// checked Zara was absent.
[Collection(PostgresCollection.Name)]
public class ScopedRegisterReachTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _engineeringId;
    private Guid _salesId;
    private Guid _adaId;
    private Guid _zaraId;
    private Guid _hanaUserId;

    private async Task<ApiFactory> ArrangeAsync()
    {
        await fixture.ResetAsync();

        _tenantId = Guid.NewGuid();
        _engineeringId = Guid.NewGuid();
        _salesId = Guid.NewGuid();
        _adaId = Guid.NewGuid();
        _zaraId = Guid.NewGuid();
        _hanaUserId = Guid.NewGuid();

        Guid hanaId = Guid.NewGuid();
        Guid levelId = Guid.NewGuid();
        Guid leaveTypeId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = _tenantId,
                Name = "Acme",
                Slug = $"acme-{_tenantId:N}",
                CountryCode = CountryCode.Nigeria,

                // Every feature on, so a refusal can only come from the permission
                // layer rather than from a module being switched off.
                Settings = new TenantSettings
                {
                    EnabledModules = [.. FeatureCatalogue.All.Select(feature => feature.Id)],
                },
            });

            await seed.SaveChangesAsync();
        }

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Departments.Add(new Department
            {
                Id = _engineeringId,
                TenantId = _tenantId,
                Name = "Engineering",
                Code = "ENG",
            });

            seed.Departments.Add(new Department
            {
                Id = _salesId,
                TenantId = _tenantId,
                Name = "Sales",
                Code = "SAL",
            });

            seed.LeaveTypes.Add(new LeaveType
            {
                Id = leaveTypeId,
                TenantId = _tenantId,
                Name = "Annual",
                Code = "ANN",
            });

            await seed.SaveChangesAsync();
        }

        // A policy, because the balances register is built from active policies joined
        // to active leave types rather than from stored balance rows. Without one the
        // register is empty for everybody, and a test asserting that Zara is absent
        // from it would pass while proving nothing.
        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.LeavePolicies.Add(new LeavePolicy
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                LeaveTypeId = leaveTypeId,
                Name = "Annual leave",
                DaysPerYear = 20,
                IsActive = true,
            });

            await seed.SaveChangesAsync();
        }

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            Add(seed, _adaId, "Ada", _engineeringId);
            Add(seed, _zaraId, "Zara", _salesId);

            // Hana is HR, posted to Engineering, and the level below is what decides
            // her reach rather than where she sits.
            Add(seed, hanaId, "Hana", _engineeringId);

            seed.Users.Add(new ApplicationUser
            {
                Id = _hanaUserId,
                TenantId = _tenantId,
                FirstName = "Hana",
                LastName = "Okafor",
                Email = "hana@acme.com",
                UserName = "hana@acme.com",
                NormalizedEmail = "HANA@ACME.COM",
                EmployeeId = hanaId,
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            seed.AccessLevels.Add(new AccessLevel
            {
                Id = levelId,
                TenantId = _tenantId,
                Name = "HR — Engineering",
                Kind = AccessLevelKind.Custom,
                Status = AccessLevelStatus.Active,
                Scope = DataScope.Departments(_engineeringId),
                Permissions =
                [
                    .. new[]
                    {
                        "operations.contracts",
                        "time-payroll.leave",
                        "time-payroll.attendance",
                        "time-payroll.expenses",
                        "talent.performance",
                        "talent.training",
                    }
                    .Select(module => new ModulePermission
                    {
                        Module = module,
                        Access = true,
                        Actions = [PermissionAction.View],
                    }),
                ],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            seed.UserAccessLevels.Add(new UserAccessLevel
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                UserId = _hanaUserId,
                AccessLevelId = levelId,
                AssignedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        // A second save: the rows below point at the employees above by foreign key, and
        // EF orders inserts within one SaveChanges by its own graph walk rather than by
        // the order they were added.
        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            Pay(seed, _adaId, "Ada's contract", 1_111_111m);
            Pay(seed, _zaraId, "Zara's contract", 9_999_999m);

            Absence(seed, _adaId, leaveTypeId, "Ada is away");
            Absence(seed, _zaraId, leaveTypeId, "Zara is away");

            Attended(seed, _adaId);
            Attended(seed, _zaraId);

            await seed.SaveChangesAsync();
        }

        return new ApiFactory(fixture.ConnectionString!)
        {
            ActingAs = _hanaUserId,
            ActingTenant = _tenantId,
            ActingEmployee = hanaId,
        };
    }

    private void Add(MoteeDbContext seed, Guid employeeId, string first, Guid departmentId) =>
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

    private void Pay(MoteeDbContext seed, Guid employeeId, string title, decimal salary) =>
        seed.Contracts.Add(new Contract
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            Title = title,
            ContractType = ContractType.Employment,
            Status = ContractStatus.Active,
            StartDate = new DateOnly(2021, 1, 10),
            Salary = salary,
            Currency = "NGN",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    private void Absence(MoteeDbContext seed, Guid employeeId, Guid leaveTypeId, string reason) =>
        seed.LeaveRequests.Add(new LeaveRequest
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            LeaveTypeId = leaveTypeId,
            StartDate = new DateOnly(2026, 3, 2),
            EndDate = new DateOnly(2026, 3, 6),
            TotalDays = 5,
            Status = LeaveRequestStatus.Approved,
            Reason = reason,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    private void Attended(MoteeDbContext seed, Guid employeeId) =>
        seed.AttendanceRecords.Add(new AttendanceRecord
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            Date = DateOnly.FromDateTime(DateTime.UtcNow),

            // Status is derived from the clock rather than stored, so a clock-in is
            // what makes this a day somebody attended.
            ClockInAt = DateTimeOffset.UtcNow.AddHours(-3),
        });

    // Her own department's pay, and not the other department's. The two salaries are
    // deliberately unmistakable in a response body.
    [SkippableFact]
    public async Task ADepartmentScopedHrUserSeesOnlyTheirDepartmentsPay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient().GetAsync("/api/v1/contracts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("1111111", body, StringComparison.Ordinal);
        Assert.DoesNotContain("9999999", body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ADepartmentScopedHrUserSeesOnlyTheirDepartmentsLeave()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient().GetAsync("/api/v1/leave/requests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Ada is away", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Zara is away", body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ADepartmentScopedHrUserSeesOnlyTheirDepartmentsAttendance()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient().GetAsync("/api/v1/attendance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Ada", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Zara", body, StringComparison.Ordinal);
    }

    // The registers Hana may open at all, each answering rather than refusing: the
    // minimum-scope gate must admit a real grant, or the fix for the first half of the
    // bug would have simply closed HR out of its own screens.
    [SkippableTheory]
    [InlineData("/api/v1/contracts")]
    [InlineData("/api/v1/leave/requests")]
    [InlineData("/api/v1/leave/balances")]
    [InlineData("/api/v1/attendance")]
    [InlineData("/api/v1/attendance/timesheets")]
    [InlineData("/api/v1/attendance/corrections")]
    [InlineData("/api/v1/expenses/claims")]
    [InlineData("/api/v1/performance/reviews")]
    [InlineData("/api/v1/performance/goals")]
    [InlineData("/api/v1/performance/feedback")]
    [InlineData("/api/v1/certifications")]
    public async Task ARealGrantStillOpensTheRegister(string path)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // And the balances register, which is built from the employee list rather than from
    // rows pointing at it, narrows the same way.
    [SkippableFact]
    public async Task TheBalancesRegisterIsNarrowedToTheDepartmentToo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        using ApiFactory api = await ArrangeAsync();

        HttpResponseMessage response = await api.CreateClient()
            .GetAsync("/api/v1/leave/balances");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Ada", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Zara", body, StringComparison.Ordinal);
    }
}
