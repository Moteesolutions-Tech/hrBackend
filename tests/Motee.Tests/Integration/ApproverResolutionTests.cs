using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Approvals;
using Motee.Domain.Approvals;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Turning "the line manager" into a person. Every way this can find nobody is ordinary
// data — an employee with no manager, a department with no head, an approver who has
// since left — and each has to say so rather than leaving an approval waiting on a
// queue nobody is watching.
[Collection(PostgresCollection.Name)]
public class ApproverResolutionTests(PostgresFixture fixture)
{
    private Guid _tenantId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        _tenantId = Guid.NewGuid();

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

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        return provider;
    }

    private async Task<Guid> AddEmployeeAsync(
        string first,
        Guid? managerId = null,
        Guid? departmentId = null,
        EmployeeStatus status = EmployeeStatus.Active,
        bool withAccount = true)
    {
        Guid employeeId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Employees.Add(new Employee
        {
            Id = employeeId,
            TenantId = _tenantId,
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            ManagerId = managerId,
            DepartmentId = departmentId,
            Status = status,
            OnboardingMethod = OnboardingMethod.Manual,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        if (withAccount)
        {
            seed.Users.Add(new ApplicationUser
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                EmployeeId = employeeId,
                FirstName = first,
                LastName = "Okafor",
                Email = $"{first.ToLowerInvariant()}@acme.com",
                NormalizedEmail = $"{first.ToUpperInvariant()}@ACME.COM",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await seed.SaveChangesAsync();

        return employeeId;
    }

    private async Task<Guid> AddDepartmentAsync(Guid? headId = null)
    {
        Guid departmentId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Departments.Add(new Department
        {
            Id = departmentId,
            TenantId = _tenantId,
            Name = "Engineering",
            Code = "ENG",
            HeadEmployeeId = headId,
            Status = DepartmentStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        return departmentId;
    }

    private static IApproverResolution Resolver(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IApproverResolution>();

    [SkippableFact]
    public async Task TheLineManagerIsFound()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid managerId = await AddEmployeeAsync("Ada");
        Guid employeeId = await AddEmployeeAsync("Ben", managerId: managerId);

        ResolvedApprover resolved = await Resolver(provider)
            .ResolveAsync(ApproverResolver.LineManager, employeeId);

        Assert.True(resolved.Found);
        Assert.Equal(managerId, resolved.EmployeeId);
        Assert.Equal("Ada Okafor", resolved.Name);
        Assert.NotNull(resolved.UserId);
    }

    [SkippableFact]
    public async Task TheDepartmentHeadIsFound()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid headId = await AddEmployeeAsync("Ada");
        Guid departmentId = await AddDepartmentAsync(headId);
        Guid employeeId = await AddEmployeeAsync("Ben", departmentId: departmentId);

        ResolvedApprover resolved = await Resolver(provider)
            .ResolveAsync(ApproverResolver.DepartmentHead, employeeId);

        Assert.True(resolved.Found);
        Assert.Equal(headId, resolved.EmployeeId);
    }

    // Ordinary data, not an error. The chain decides what to do about it based on
    // whether the step was required — but it has to be told, in words.
    [SkippableFact]
    public async Task AnEmployeeWithNoManagerResolvesToNobodyAndSaysWhy()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await AddEmployeeAsync("Ben");

        ResolvedApprover resolved = await Resolver(provider)
            .ResolveAsync(ApproverResolver.LineManager, employeeId);

        Assert.False(resolved.Found);
        Assert.Contains("no line manager", resolved.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ADepartmentWithNoHeadResolvesToNobody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid departmentId = await AddDepartmentAsync();
        Guid employeeId = await AddEmployeeAsync("Ben", departmentId: departmentId);

        ResolvedApprover resolved = await Resolver(provider)
            .ResolveAsync(ApproverResolver.DepartmentHead, employeeId);

        Assert.False(resolved.Found);
        Assert.Contains("no head", resolved.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // Approving your own request is not an approval. This happens on real data whenever
    // somebody is recorded as their own manager, and it is silent unless caught.
    [SkippableFact]
    public async Task SomebodyCannotApproveTheirOwnRequest()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await AddEmployeeAsync("Ben");

        await using (MoteeDbContext context = fixture.CreateContext(_tenantId))
        {
            Employee employee = context.Employees.First(candidate => candidate.Id == employeeId);
            employee.ManagerId = employeeId;
            await context.SaveChangesAsync();
        }

        ResolvedApprover resolved = await Resolver(provider)
            .ResolveAsync(ApproverResolver.LineManager, employeeId);

        Assert.False(resolved.Found);
        Assert.Contains("own manager", resolved.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // Pointing a live approval at somebody who has gone is how a queue sits untouched
    // for weeks before anyone asks why.
    [SkippableFact]
    public async Task ALeaverCannotBeAnApprover()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid managerId = await AddEmployeeAsync("Ada", status: EmployeeStatus.Inactive);
        Guid employeeId = await AddEmployeeAsync("Ben", managerId: managerId);

        ResolvedApprover resolved = await Resolver(provider)
            .ResolveAsync(ApproverResolver.LineManager, employeeId);

        Assert.False(resolved.Found);
        Assert.Contains("has left", resolved.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // Being in the org chart is not the same as being able to act. Somebody with no
    // login cannot approve anything, and that is a different problem from finding
    // nobody at all.
    [SkippableFact]
    public async Task AnApproverWithNoAccountCannotApprove()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid managerId = await AddEmployeeAsync("Ada", withAccount: false);
        Guid employeeId = await AddEmployeeAsync("Ben", managerId: managerId);

        ResolvedApprover resolved = await Resolver(provider)
            .ResolveAsync(ApproverResolver.LineManager, employeeId);

        Assert.False(resolved.Found);
        Assert.Contains("no account", resolved.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // Both phase 1 rules are positional. With no subject there is no anchor, and
    // guessing one would put the approval in front of somebody arbitrary.
    [SkippableFact]
    public async Task WithNoSubjectNothingCanBeResolved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ResolvedApprover resolved = await Resolver(provider)
            .ResolveAsync(ApproverResolver.LineManager, subjectEmployeeId: null);

        Assert.False(resolved.Found);
    }
}
