using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Tenancy;
using Motee.Domain.Authorization;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The admin is the one person whose account exists before any employee does, so
// theirs is the only link that cannot be made when the account is created.
[Collection(PostgresCollection.Name)]
public class OnboardingGapsTests(PostgresFixture fixture)
{
    private const string AdminEmail = "admin@acme.com";

    private async Task<(Guid TenantId, Guid UserId, ServiceProvider Provider)> RegisterAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = AdminEmail,
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = registration.TenantId;

        return (registration.TenantId, registration.UserId, provider);
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static async Task<Guid> DepartmentAsync(ServiceProvider provider) =>
        (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
        .Department!.Id;

    private static EmployeeRequest Employee(Guid departmentId, string email, string firstName = "Ada") => new()
    {
        FirstName = firstName,
        LastName = "Okafor",
        Email = email,
        Phone = "08012345678",
        JobTitle = "Head of People",
        DepartmentId = departmentId,
        EmploymentType = EmploymentType.FullTime,
    };

    // Nothing is invented for a new tenant. Adding an employee before a department
    // exists fails with a message that says so, which is the intended behaviour.
    [SkippableFact]
    public async Task ANewTenantStartsWithNoDepartments()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, _, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        await Resolve<ITenantSetupService>(owned).CompleteAsync(tenantId);

        Assert.Empty(await Resolve<IDepartmentService>(owned).ListAsync());
    }

    [SkippableFact]
    public async Task AddingAnEmployeeBeforeAnyDepartmentIsRefusedClearly()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        EmployeeResult result = await Resolve<IEmployeeService>(owned)
            .CreateAsync(Employee(Guid.NewGuid(), "bola@acme.com"));

        Assert.Equal(EmployeeOutcome.UnknownDepartment, result.Outcome);
    }

    // Without this the admin's Team and Self scope stay empty for ever, because the
    // employee_id claim is never populated.
    [SkippableFact]
    public async Task CreatingTheirOwnRecordLinksTheAdminsAccount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid userId, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        Guid departmentId = await DepartmentAsync(owned);

        EmployeeResult created = await Resolve<IEmployeeService>(owned)
            .CreateAsync(Employee(departmentId, AdminEmail));

        Assert.True(created.Succeeded, created.Outcome.ToString());

        await using MoteeDbContext context = fixture.CreateContext(tenantId);
        ApplicationUser user = await context.Users.SingleAsync(candidate => candidate.Id == userId);

        Assert.Equal(created.Employee!.Id, user.EmployeeId);
    }

    [SkippableFact]
    public async Task TheLinkedAdminCanSeeThemselvesUnderSelfScope()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        Guid departmentId = await DepartmentAsync(owned);

        Guid employeeId = (await Resolve<IEmployeeService>(owned)
            .CreateAsync(Employee(departmentId, AdminEmail))).Employee!.Id;

        PagedResult<EmployeeListItemDto> page = await Resolve<IEmployeeService>(owned)
            .ListAsync(new EmployeeQuery
            {
                Scope = new DataScope { Kind = DataScopeKind.Self },
                ViewerEmployeeId = employeeId,
            });

        Assert.Single(page.Items);
        Assert.Equal(1, page.TotalItems);
    }

    // An address that is not the admin's must not attach to their account.
    [SkippableFact]
    public async Task AnUnrelatedEmployeeDoesNotLinkToTheAdmin()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid userId, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        Guid departmentId = await DepartmentAsync(owned);

        await Resolve<IEmployeeService>(owned)
            .CreateAsync(Employee(departmentId, "bola@acme.com", "Bola"));

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        Assert.Null((await context.Users.SingleAsync(candidate => candidate.Id == userId)).EmployeeId);
    }

    // Linking happens once; a later record with the same address is a duplicate and
    // never steals an account that is already attached.
    [SkippableFact]
    public async Task AnAlreadyLinkedAccountIsNotRelinked()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid userId, ServiceProvider provider) = await RegisterAsync();
        await using ServiceProvider owned = provider;

        Guid departmentId = await DepartmentAsync(owned);

        Guid firstId = (await Resolve<IEmployeeService>(owned)
            .CreateAsync(Employee(departmentId, AdminEmail))).Employee!.Id;

        EmployeeResult second = await Resolve<IEmployeeService>(owned)
            .CreateAsync(Employee(departmentId, AdminEmail, "Someone"));

        Assert.Equal(EmployeeOutcome.DuplicateEmail, second.Outcome);

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        Assert.Equal(
            firstId,
            (await context.Users.SingleAsync(candidate => candidate.Id == userId)).EmployeeId);
    }
}
