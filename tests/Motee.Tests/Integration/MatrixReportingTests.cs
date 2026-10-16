using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Approvals;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Tenancy;
using Motee.Domain.Approvals;
using Motee.Domain.Authorization;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The second reporting line, and the setting that permits it.
//
// The wizard describes a matrix precisely — "a Functional Manager (their department
// manager) and a Project Manager" — and draws a diagram of it, while the backend had one
// manager column. These pin the structure setting as a rule rather than a label: it is
// what decides whether the second line may exist at all.
[Collection(PostgresCollection.Name)]
public class MatrixReportingTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _departmentId;

    private async Task<ServiceProvider> ArrangeAsync(StructureType structure)
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            Tenant tenant = await seed.Tenants.FirstAsync(candidate => candidate.Id == _tenantId);
            tenant.Settings = tenant.Settings with { StructureType = structure };
            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        return provider;
    }

    // The resolver refuses anybody who cannot act, so an approver needs a login before
    // resolution means anything.
    private async Task GiveAccountAsync(Guid employeeId, string handle)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = handle,
            LastName = "Adeyemi",
            Email = $"{handle}-login@acme.com",
            NormalizedEmail = $"{handle.ToUpperInvariant()}-LOGIN@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private Task<EmployeeResult> HireAsync(
        ServiceProvider provider,
        string first,
        string email,
        Guid? manager = null,
        Guid? dotted = null) =>
        Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Adeyemi",
            Email = email,
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            ManagerId = manager,
            DottedLineManagerId = dotted,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
        });

    // The thing the wizard's diagram promises: two managers, one person.
    [SkippableFact]
    public async Task AMatrixCompanyCanRecordAProjectManager()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(StructureType.Matrix);

        Guid functional = (await HireAsync(provider, "Cara", "cara@acme.com")).Employee!.Id;
        Guid project = (await HireAsync(provider, "Emeka", "emeka@acme.com")).Employee!.Id;

        EmployeeResult hired = await HireAsync(
            provider, "Ben", "ben@acme.com", manager: functional, dotted: project);

        Assert.True(hired.Succeeded, hired.Outcome.ToString());

        EmployeeDto employee = (await Resolve<IEmployeeService>(provider)
            .GetAsync(hired.Employee!.Id))!;

        Assert.Equal(functional, employee.ManagerId);
        Assert.Equal(project, employee.DottedLineManagerId);
        Assert.Equal("Emeka Adeyemi", employee.DottedLineManagerName);
    }

    // Refused, not ignored. Dropping it silently leaves an HR admin believing they
    // recorded a project manager, with nothing to show they did not.
    [SkippableFact]
    public async Task AHierarchicalCompanyCannotRecordOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(StructureType.Hierarchical);

        Guid project = (await HireAsync(provider, "Emeka", "emeka@acme.com")).Employee!.Id;

        EmployeeResult refused = await HireAsync(
            provider, "Ben", "ben@acme.com", dotted: project);

        Assert.Equal(EmployeeOutcome.SecondaryReportingNotAllowed, refused.Outcome);
    }

    [SkippableFact]
    public async Task AFlatCompanyCannotEither()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(StructureType.Flat);

        Guid project = (await HireAsync(provider, "Emeka", "emeka@acme.com")).Employee!.Id;

        Assert.Equal(
            EmployeeOutcome.SecondaryReportingNotAllowed,
            (await HireAsync(provider, "Ben", "ben@acme.com", dotted: project)).Outcome);
    }

    [SkippableFact]
    public async Task SomebodyCannotBeTheirOwnProjectManager()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(StructureType.Matrix);

        Guid ben = (await HireAsync(provider, "Ben", "ben@acme.com")).Employee!.Id;

        EmployeeResult refused = await Resolve<IEmployeeService>(provider).UpdateAsync(
            ben,
            new EmployeeRequest
            {
                FirstName = "Ben",
                LastName = "Adeyemi",
                Email = "ben@acme.com",
                Phone = "08012345678",
                JobTitle = "Engineer",
                DepartmentId = _departmentId,
                DottedLineManagerId = ben,
                EmploymentType = EmploymentType.FullTime,
                Status = EmployeeStatus.Active,
            });

        Assert.Equal(EmployeeOutcome.UnknownDottedLineManager, refused.Outcome);
    }

    [SkippableFact]
    public async Task AProjectManagerWhoIsNotAnEmployeeIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(StructureType.Matrix);

        Assert.Equal(
            EmployeeOutcome.UnknownDottedLineManager,
            (await HireAsync(provider, "Ben", "ben@acme.com", dotted: Guid.NewGuid())).Outcome);
    }

    // The dotted line is a working relationship, not an HR one. Approvals keep following
    // the solid line, because a project manager approving somebody's leave is the
    // functional manager's call — and widening that as a side effect of a structure
    // setting is exactly the kind of change that surprises people later.
    [SkippableFact]
    public async Task ApprovalsStillFollowTheSolidLine()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(StructureType.Matrix);

        Guid functional = (await HireAsync(provider, "Cara", "cara@acme.com")).Employee!.Id;
        Guid project = (await HireAsync(provider, "Emeka", "emeka@acme.com")).Employee!.Id;
        Guid ben = (await HireAsync(
            provider, "Ben", "ben@acme.com", manager: functional, dotted: project)).Employee!.Id;

        // Both need an account, or the resolver reports nobody for either and the test
        // would pass without distinguishing the two lines at all.
        await GiveAccountAsync(functional, "cara");
        await GiveAccountAsync(project, "emeka");

        ResolvedApprover resolved = await Resolve<IApproverResolution>(provider)
            .ResolveAsync(ApproverResolver.LineManager, ben);

        Assert.Equal(functional, resolved.EmployeeId);
    }

    // And neither does it grant sight of anybody's record. DirectReports keeps meaning
    // ManagerId: a company switching to matrix must not silently widen who can read what.
    [SkippableFact]
    public async Task ADottedLineGrantsNoRecordAccess()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(StructureType.Matrix);

        Guid project = (await HireAsync(provider, "Emeka", "emeka@acme.com")).Employee!.Id;
        await HireAsync(provider, "Ben", "ben@acme.com", dotted: project);

        PagedResult<EmployeeListItemDto> reach = await Resolve<IEmployeeService>(provider)
            .ListAsync(new EmployeeQuery
            {
                Scope = new DataScope { Kind = DataScopeKind.DirectReports },
                ViewerEmployeeId = project,
            });

        // Themselves only — Ben is on their project, not in their team.
        Assert.Single(reach.Items);
        Assert.DoesNotContain(
            reach.Items, row => row.Name.StartsWith("Ben", StringComparison.Ordinal));
    }



}
