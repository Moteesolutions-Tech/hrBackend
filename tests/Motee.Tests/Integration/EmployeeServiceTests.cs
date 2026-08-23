using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Authorization;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class EmployeeServiceTests(PostgresFixture fixture)
{
    private async Task<(Guid TenantId, Guid DepartmentId, ServiceProvider Provider)> ArrangeAsync()
    {
        await fixture.ResetAsync();

        Guid tenantId = Guid.NewGuid();

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

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = tenantId;

        DepartmentResult department = await provider.CreateScope().ServiceProvider
            .GetRequiredService<IDepartmentService>()
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" });

        return (tenantId, department.Department!.Id, provider);
    }

    private static IEmployeeService Service(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IEmployeeService>();

    private static EmployeeRequest Request(
        Guid departmentId,
        string email = "ada@acme.com",
        string firstName = "Ada",
        Guid? managerId = null,
        string? employeeNumber = null,
        DateOnly? startDate = null,
        WorkMode? workMode = null,
        EmploymentType employmentType = EmploymentType.FullTime) => new()
    {
        StartDate = startDate,
        WorkMode = workMode,
        FirstName = firstName,
        LastName = "Okafor",
        Email = email,
        Phone = "08012345678",
        JobTitle = "Engineer",
        DepartmentId = departmentId,
        EmploymentType = employmentType,
        ManagerId = managerId,
        EmployeeNumber = employeeNumber,
        EmergencyContactName = "Bola Okafor",
        EmergencyContactRelationship = "Sister",
        EmergencyContactPhone = "08087654321",
    };

    private static EmployeeQuery AllScope() => new() { Scope = DataScope.Everything };

    [SkippableFact]
    public async Task CreatesAnEmployee()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeResult result = await Service(owned).CreateAsync(Request(departmentId));

        Assert.True(result.Succeeded, result.Outcome.ToString());
        Assert.Equal("Ada Okafor", result.Employee!.FullName);
        Assert.Equal("Engineering", result.Employee.Department);
        Assert.Equal(EmploymentType.FullTime, result.Employee.EmploymentType);
        Assert.Equal("Bola Okafor", result.Employee.EmergencyContactName);
        Assert.Equal(EmployeeStatus.Pending, result.Employee.Status);
    }

    [SkippableFact]
    public async Task RefusesADuplicateEmail()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request(departmentId));

        Assert.Equal(
            EmployeeOutcome.DuplicateEmail,
            (await Service(owned).CreateAsync(Request(departmentId, email: "ADA@acme.com"))).Outcome);
    }

    [SkippableFact]
    public async Task RefusesADuplicateEmployeeNumber()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request(departmentId, employeeNumber: "EMP-001"));

        Assert.Equal(
            EmployeeOutcome.DuplicateEmployeeNumber,
            (await Service(owned).CreateAsync(
                Request(departmentId, email: "bola@acme.com", employeeNumber: "EMP-001"))).Outcome);
    }

    // The tenant filter makes this a cross-tenant check as well: another company's
    // department is not visible, so it reads as unknown rather than forbidden.
    [SkippableFact]
    public async Task RefusesAnUnknownDepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Assert.Equal(
            EmployeeOutcome.UnknownDepartment,
            (await Service(owned).CreateAsync(Request(Guid.NewGuid()))).Outcome);
    }

    [SkippableFact]
    public async Task RefusesAnUnknownManager()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Assert.Equal(
            EmployeeOutcome.UnknownManager,
            (await Service(owned).CreateAsync(Request(departmentId, managerId: Guid.NewGuid()))).Outcome);
    }

    [SkippableFact]
    public async Task NobodyCanReportToThemselves()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Service(owned).CreateAsync(Request(departmentId))).Employee!.Id;

        Assert.Equal(
            EmployeeOutcome.ManagerCycle,
            (await Service(owned).UpdateAsync(id, Request(departmentId, managerId: id))).Outcome);
    }

    // A closed loop would make every hierarchy walk — approval routing, org chart,
    // Team scope — run forever.
    [SkippableFact]
    public async Task AManagerCannotBeMovedUnderTheirOwnReport()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid bossId = (await Service(owned).CreateAsync(Request(departmentId))).Employee!.Id;

        Guid reportId = (await Service(owned).CreateAsync(
            Request(departmentId, email: "bola@acme.com", firstName: "Bola", managerId: bossId)))
            .Employee!.Id;

        Guid grandchildId = (await Service(owned).CreateAsync(
            Request(departmentId, email: "chidi@acme.com", firstName: "Chidi", managerId: reportId)))
            .Employee!.Id;

        Assert.Equal(
            EmployeeOutcome.ManagerCycle,
            (await Service(owned).UpdateAsync(bossId, Request(departmentId, managerId: grandchildId))).Outcome);
    }

    [SkippableFact]
    public async Task CountsDirectReports()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid bossId = (await Service(owned).CreateAsync(Request(departmentId))).Employee!.Id;

        await Service(owned).CreateAsync(
            Request(departmentId, email: "bola@acme.com", firstName: "Bola", managerId: bossId));
        await Service(owned).CreateAsync(
            Request(departmentId, email: "chidi@acme.com", firstName: "Chidi", managerId: bossId));

        EmployeeDto boss = (await Service(owned).GetAsync(bossId))!;

        Assert.Equal(2, boss.DirectReportCount);
    }

    [SkippableFact]
    public async Task ResolvesTheManagersName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid bossId = (await Service(owned).CreateAsync(Request(departmentId))).Employee!.Id;

        EmployeeResult report = await Service(owned).CreateAsync(
            Request(departmentId, email: "bola@acme.com", firstName: "Bola", managerId: bossId));

        Assert.Equal("Ada Okafor", report.Employee!.ManagerName);
    }

    [SkippableFact]
    public async Task SearchesAcrossNameAndEmail()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request(departmentId));
        await Service(owned).CreateAsync(
            Request(departmentId, email: "bola@globex.com", firstName: "Bola"));

        Assert.Single((await Service(owned).ListAsync(AllScope() with { Search = "ada" })).Items);
        Assert.Single((await Service(owned).ListAsync(AllScope() with { Search = "globex" })).Items);
        Assert.Equal(2, ((await Service(owned).ListAsync(AllScope() with { Search = "o" })).Items).Count);
    }

    [SkippableFact]
    public async Task FiltersByStartDateRange()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(
            Request(departmentId, startDate: new DateOnly(2024, 1, 15)));
        await Service(owned).CreateAsync(
            Request(departmentId, email: "bola@acme.com", firstName: "Bola",
                startDate: new DateOnly(2025, 6, 1)));

        EmployeeQuery inRange = AllScope() with
        {
            StartedFrom = new DateOnly(2025, 1, 1),
            StartedTo = new DateOnly(2025, 12, 31),
        };

        Assert.Equal("Bola Okafor", (await Service(owned).ListAsync(inRange)).Items.Single().Name);
    }

    // Either end alone: "everyone who joined since April" has no upper bound.
    [SkippableFact]
    public async Task AnOpenEndedRangeFiltersFromOneSideOnly()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(
            Request(departmentId, startDate: new DateOnly(2024, 1, 15)));
        await Service(owned).CreateAsync(
            Request(departmentId, email: "bola@acme.com", firstName: "Bola",
                startDate: new DateOnly(2025, 6, 1)));

        Assert.Single((await Service(owned)
            .ListAsync(AllScope() with { StartedFrom = new DateOnly(2025, 1, 1) })).Items);
        Assert.Single((await Service(owned)
            .ListAsync(AllScope() with { StartedTo = new DateOnly(2024, 12, 31) })).Items);
    }

    // Both ends are inclusive, so a range that names someone's exact start date
    // includes them.
    [SkippableFact]
    public async Task TheRangeIncludesItsOwnBoundaries()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        DateOnly started = new(2025, 6, 1);
        await Service(owned).CreateAsync(Request(departmentId, startDate: started));

        Assert.Single((await Service(owned)
            .ListAsync(AllScope() with { StartedFrom = started, StartedTo = started })).Items);
    }

    // Otherwise a range would quietly include everyone whose paperwork is incomplete.
    [SkippableFact]
    public async Task AnEmployeeWithNoStartDateIsOutsideEveryRange()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request(departmentId, startDate: null));

        Assert.Empty((await Service(owned)
            .ListAsync(AllScope() with { StartedFrom = new DateOnly(2000, 1, 1) })).Items);
        Assert.Empty((await Service(owned)
            .ListAsync(AllScope() with { StartedTo = new DateOnly(2099, 1, 1) })).Items);
        Assert.Single((await Service(owned).ListAsync(AllScope())).Items);
    }

    [SkippableFact]
    public async Task FiltersByEmploymentType()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(
            Request(departmentId, employmentType: EmploymentType.FullTime));
        await Service(owned).CreateAsync(
            Request(departmentId, email: "bola@acme.com", firstName: "Bola",
                employmentType: EmploymentType.Contract));

        Assert.Equal("Bola Okafor", (await Service(owned).ListAsync(
            AllScope() with { EmploymentType = EmploymentType.Contract })).Items.Single().Name);
    }

    [SkippableFact]
    public async Task FiltersByWorkMode()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request(departmentId, workMode: WorkMode.Remote));
        await Service(owned).CreateAsync(
            Request(departmentId, email: "bola@acme.com", firstName: "Bola",
                workMode: WorkMode.Onsite));

        Assert.Equal("Ada Okafor", (await Service(owned).ListAsync(
            AllScope() with { WorkMode = WorkMode.Remote })).Items.Single().Name);
    }

    // Nobody recorded a work mode for them, so they are not an answer to "who is
    // remote" — the same rule as the start-date range.
    [SkippableFact]
    public async Task AnEmployeeWithNoWorkModeMatchesNoWorkModeFilter()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request(departmentId, workMode: null));

        Assert.Empty((await Service(owned)
            .ListAsync(AllScope() with { WorkMode = WorkMode.Onsite })).Items);
        Assert.Single((await Service(owned).ListAsync(AllScope())).Items);
    }

    // The search box offers department, so searching one has to find its people even
    // though the name lives on another table.
    [SkippableFact]
    public async Task SearchAlsoMatchesTheDepartmentName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid finance = (await owned.CreateScope().ServiceProvider
            .GetRequiredService<IDepartmentService>()
            .CreateAsync(new DepartmentRequest { Name = "Finance", Code = "FIN" }))
            .Department!.Id;

        await Service(owned).CreateAsync(Request(departmentId));
        await Service(owned).CreateAsync(
            Request(finance, email: "bola@acme.com", firstName: "Bola"));

        Assert.Equal("Bola Okafor",
            (await Service(owned).ListAsync(AllScope() with { Search = "financ" }))
                .Items.Single().Name);
    }

    // Filters combine rather than replace one another.
    [SkippableFact]
    public async Task FiltersNarrowTogether()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request(departmentId,
            workMode: WorkMode.Remote, employmentType: EmploymentType.Contract));
        await Service(owned).CreateAsync(Request(departmentId, email: "bola@acme.com",
            firstName: "Bola", workMode: WorkMode.Remote, employmentType: EmploymentType.FullTime));

        Assert.Equal("Ada Okafor", (await Service(owned).ListAsync(AllScope() with
        {
            WorkMode = WorkMode.Remote,
            EmploymentType = EmploymentType.Contract,
        })).Items.Single().Name);
    }

    private static async Task<Guid> Hire(
        ServiceProvider provider,
        Guid departmentId,
        string email,
        EmployeeStatus status)
    {
        Guid id = (await Service(provider).CreateAsync(Request(departmentId, email: email)))
            .Employee!.Id;

        if (status != EmployeeStatus.Pending)
        {
            await Service(provider).ChangeStatusAsync(id, status);
        }

        return id;
    }

    [SkippableFact]
    public async Task StatsCountEveryStatus()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Hire(owned, departmentId, "one@acme.com", EmployeeStatus.Active);
        await Hire(owned, departmentId, "two@acme.com", EmployeeStatus.Active);
        await Hire(owned, departmentId, "three@acme.com", EmployeeStatus.Probation);
        await Hire(owned, departmentId, "four@acme.com", EmployeeStatus.Pending);

        EmployeeStatsDto stats = await Service(owned).StatsAsync(AllScope());

        Assert.Equal(2, Count(stats, EmployeeStatus.Active));
        Assert.Equal(1, Count(stats, EmployeeStatus.Probation));
        Assert.Equal(1, Count(stats, EmployeeStatus.Pending));
        Assert.Equal(4, stats.Headcount);
    }

    // The tab strip renders from this list, so a status nobody is in still needs a
    // row — otherwise the tab disappears rather than showing zero.
    [SkippableFact]
    public async Task EveryStatusAppearsEvenAtZero()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, _, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        EmployeeStatsDto stats = await Service(owned).StatsAsync(AllScope());

        Assert.Equal(Enum.GetValues<EmployeeStatus>().Length, stats.ByStatus.Count);
        Assert.All(stats.ByStatus, entry => Assert.Equal(0, entry.Count));
        Assert.Equal(0, stats.Headcount);
    }

    // Soft-deleted rows keep their tab but are not people who work here.
    [SkippableFact]
    public async Task DeletedRowsAreCountedButExcludedFromHeadcount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Hire(owned, departmentId, "kept@acme.com", EmployeeStatus.Active);
        Guid removed = await Hire(owned, departmentId, "gone@acme.com", EmployeeStatus.Active);

        await Service(owned).ChangeStatusAsync(removed, EmployeeStatus.Deleted);

        EmployeeStatsDto stats = await Service(owned).StatsAsync(AllScope());

        Assert.Equal(1, Count(stats, EmployeeStatus.Deleted));
        Assert.Equal(1, stats.Headcount);
    }

    // A card that counted the whole company while the table under it showed one team
    // would be a leak, not a rounding difference.
    [SkippableFact]
    public async Task StatsAreNarrowedByScopeLikeTheList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid bossId = await Hire(owned, departmentId, "boss@acme.com", EmployeeStatus.Active);

        await Service(owned).CreateAsync(
            Request(departmentId, email: "report@acme.com", firstName: "Bola", managerId: bossId));
        await Hire(owned, departmentId, "stranger@acme.com", EmployeeStatus.Active);

        EmployeeStatsDto team = await Service(owned).StatsAsync(new EmployeeQuery
        {
            Scope = new DataScope { Kind = DataScopeKind.DirectReports },
            ViewerEmployeeId = bossId,
        });

        // The manager plus one direct report; the third person is not theirs to count.
        Assert.Equal(2, team.Headcount);
    }

    // The cards follow the toolbar, or clicking through to a filtered table shows a
    // different number than the card that was clicked.
    [SkippableFact]
    public async Task StatsHonourTheSameFiltersAsTheList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid otherDepartment = (await owned.CreateScope().ServiceProvider
            .GetRequiredService<IDepartmentService>()
            .CreateAsync(new DepartmentRequest { Name = "Finance", Code = "FIN" }))
            .Department!.Id;

        await Hire(owned, departmentId, "eng@acme.com", EmployeeStatus.Active);
        await Hire(owned, otherDepartment, "fin@acme.com", EmployeeStatus.Active);

        EmployeeStatsDto stats = await Service(owned)
            .StatsAsync(AllScope() with { DepartmentId = otherDepartment });

        Assert.Equal(1, stats.Headcount);
        Assert.Equal(1, Count(stats, EmployeeStatus.Active));
    }

    private static int Count(EmployeeStatsDto stats, EmployeeStatus status) =>
        stats.ByStatus.Single(entry => entry.Status == status).Count;

    // A Line Manager who can edit employees must not thereby reach the whole company.
    [SkippableFact]
    public async Task TeamScopeSeesOnlyDirectReportsAndSelf()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid bossId = (await Service(owned).CreateAsync(Request(departmentId))).Employee!.Id;

        await Service(owned).CreateAsync(
            Request(departmentId, email: "bola@acme.com", firstName: "Bola", managerId: bossId));
        await Service(owned).CreateAsync(
            Request(departmentId, email: "chidi@acme.com", firstName: "Chidi"));

        IReadOnlyList<EmployeeListItemDto> visible = (await Service(owned).ListAsync(new EmployeeQuery
        {
            Scope = new DataScope { Kind = DataScopeKind.DirectReports },
            ViewerEmployeeId = bossId,
        })).Items;

        Assert.Equal(["Ada Okafor", "Bola Okafor"], visible.Select(employee => employee.Name).Order());
    }

    [SkippableFact]
    public async Task SelfScopeSeesOnlyTheirOwnRecord()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Service(owned).CreateAsync(Request(departmentId))).Employee!.Id;
        await Service(owned).CreateAsync(
            Request(departmentId, email: "bola@acme.com", firstName: "Bola"));

        IReadOnlyList<EmployeeListItemDto> visible = (await Service(owned).ListAsync(new EmployeeQuery
        {
            Scope = new DataScope { Kind = DataScopeKind.Self },
            ViewerEmployeeId = id,
        })).Items;

        Assert.Single(visible);
        Assert.Equal("Ada Okafor", visible[0].Name);
    }

    // PermissionScope.None reaching the service is a bug upstream; it must not read
    // as "everything".
    [SkippableFact]
    public async Task NoScopeSeesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request(departmentId));

        Assert.Empty((await Service(owned).ListAsync(new EmployeeQuery { Scope = DataScope.Nothing })).Items);
    }

    [SkippableFact]
    public async Task UpdatesAnEmployee()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Service(owned).CreateAsync(Request(departmentId))).Employee!.Id;

        EmployeeResult updated = await Service(owned).UpdateAsync(id, Request(departmentId) with
        {
            JobTitle = "Staff Engineer",
            Status = EmployeeStatus.Active,
            Grade = "L5",
        });

        Assert.True(updated.Succeeded);
        Assert.Equal("Staff Engineer", updated.Employee!.JobTitle);
        Assert.Equal(EmployeeStatus.Active, updated.Employee.Status);
        Assert.Equal("L5", updated.Employee.Grade);
    }

    [SkippableFact]
    public async Task SavingAnEmployeeUnchangedIsAllowed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Service(owned).CreateAsync(Request(departmentId))).Employee!.Id;

        Assert.True((await Service(owned).UpdateAsync(id, Request(departmentId))).Succeeded);
    }

    [SkippableFact]
    public async Task ReportsNotFoundForAnUnknownId()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Assert.Null(await Service(owned).GetAsync(Guid.NewGuid()));
        Assert.Equal(
            EmployeeOutcome.NotFound,
            (await Service(owned).UpdateAsync(Guid.NewGuid(), Request(departmentId))).Outcome);
    }

    [SkippableFact]
    public async Task DepartmentEmployeeCountReflectsCreatedEmployees()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, Guid departmentId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        await Service(owned).CreateAsync(Request(departmentId));

        DepartmentDto department = (await provider.CreateScope().ServiceProvider
            .GetRequiredService<IDepartmentService>()
            .GetAsync(departmentId))!;

        Assert.Equal(1, department.EmployeeCount);
    }
}
