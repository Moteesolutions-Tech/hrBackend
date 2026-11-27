using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Assets;
using Motee.Application.Auth;
using Motee.Application.Dashboard;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Performance;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The Action Centre: everything outstanding, grouped the way the screen groups
// it, derived on every read.
//
// Nothing here has a table and that is the point. An alerts table would need
// something to write it and something to clear it, and the thing that clears
// "missing emergency contact" is somebody filling the field in on a different
// screen — which would never come back to delete the row. So an item goes away
// because the reason for it did.
[Collection(PostgresCollection.Name)]
public class HrActionsDashboardTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private readonly MovableClock _clock = new(
        new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Owner",
            LastName = "Okafor",
            Email = "owner@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static Task<HrActionsDashboardDto> BoardAsync(ServiceProvider provider) =>
        Resolve<IDashboardService>(provider).HrActionsAsync();

    private static async Task<HrActionCategoryDto> CategoryAsync(
        ServiceProvider provider,
        string key) =>
        (await BoardAsync(provider)).Categories.First(category => category.Key == key);

    // Complete by default, so a test that wants an incomplete record says so.
    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        bool emergencyContact = true,
        EmployeeStatus status = EmployeeStatus.Active) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Adeyemi",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = status,
            StartDate = new DateOnly(2021, 1, 10),
            EmergencyContactName = emergencyContact ? "Next Of Kin" : null,
            EmergencyContactPhone = emergencyContact ? "08098765432" : null,
        })).Employee!.Id;

    private async Task ReviewAsync(Guid employeeId, DateOnly dueDate, ReviewStatus status)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.PerformanceReviews.Add(new PerformanceReview
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            Period = "H2 2026",
            DueDate = dueDate,
            Status = status,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        });

        await seed.SaveChangesAsync();
    }

    // ── the shape of the board ──────────────────────────────────────────────

    [SkippableFact]
    public async Task EveryCategoryIsReportedEvenAtZero()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        HrActionsDashboardDto board = await BoardAsync(provider);

        // The screen draws all eleven. An absent category and an empty one look
        // identical to a user and mean opposite things, so none is dropped.
        Assert.Equal(
            [
                "right_to_work", "compliance", "payroll", "recruitment", "relations",
                "performance", "leave", "learning", "assets", "management", "executive",
            ],
            board.Categories.Select(category => category.Key).ToArray());

        Assert.Equal(Today, board.AsOf);
    }

    // There is no payroll in this product — no pay run, no payslip, no bank
    // details. Every row the demo bundle shows under this heading is one
    // nothing could compute, so the category is present and empty rather than
    // filled with invented figures.
    [SkippableFact]
    public async Task PayrollIsEmptyBecauseThereIsNoPayroll()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada");

        HrActionCategoryDto payroll = await CategoryAsync(provider, "payroll");

        Assert.Equal(0, payroll.Count);
        Assert.Empty(payroll.Actions);
    }

    [SkippableFact]
    public async Task AQuietCompanyHasNothingOutstanding()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada");

        HrActionsDashboardDto board = await BoardAsync(provider);

        Assert.Equal(0, board.Total);
        Assert.Equal(0, board.Critical);
        Assert.Equal(0, board.Warning);
        Assert.Equal(0, board.Info);
    }

    // ── employee data ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AMissingEmergencyContactIsListedAsInformation()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada", emergencyContact: false);
        await HireAsync(provider, "Ben");

        HrActionCategoryDto compliance = await CategoryAsync(provider, "compliance");

        HrActionDto action = Assert.Single(compliance.Actions);

        Assert.Equal("Missing emergency contact", action.Title);
        Assert.Equal("Ada Adeyemi", action.Detail);
        Assert.Equal(ada, action.EmployeeId);

        // Nothing breaks today. Marking tidying as urgent is how a list of
        // twenty critical items gets ignored.
        Assert.Equal(HrActionSeverity.Info, action.Severity);

        // No deadline, rather than an invented one: an unfilled field is not
        // late, and a countdown on it would be fiction.
        Assert.Null(action.DueInDays);
    }

    [SkippableFact]
    public async Task SomebodyWhoHasLeftIsNotChasedForMissingDetails()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(
            provider, "Ada", emergencyContact: false, status: EmployeeStatus.Inactive);

        Assert.Empty((await CategoryAsync(provider, "compliance")).Actions);
    }

    // ── dated items and their severity ──────────────────────────────────────

    [SkippableFact]
    public async Task AnOverdueReviewIsCriticalAndCountsTheDaysPast()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada");

        await ReviewAsync(ada, Today.AddDays(-5), ReviewStatus.InProgress);

        HrActionDto action = Assert.Single(
            (await CategoryAsync(provider, "performance")).Actions);

        Assert.Equal("Performance review overdue", action.Title);
        Assert.Equal("Ada Adeyemi", action.Detail);
        Assert.Equal(HrActionSeverity.Critical, action.Severity);

        // Negative, because it is late. A screen showing "5 days" for something
        // five days overdue reads as time remaining.
        Assert.Equal(-5, action.DueInDays);
    }

    [SkippableFact]
    public async Task ACompletedReviewPastItsDateIsNotOverdue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada");

        await ReviewAsync(ada, Today.AddDays(-5), ReviewStatus.Completed);

        Assert.Empty((await CategoryAsync(provider, "performance")).Actions);
    }

    [SkippableFact]
    public async Task AReviewStillInTheFutureIsNotListed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada");

        await ReviewAsync(ada, Today.AddDays(20), ReviewStatus.NotStarted);

        Assert.Empty((await CategoryAsync(provider, "performance")).Actions);
    }

    // ── assets ──────────────────────────────────────────────────────────────

    // The one on this list that walks out of the building.
    [SkippableFact]
    public async Task KitStillIssuedToALeaverIsCritical()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada");

        Guid assetId = (await Resolve<IAssetService>(provider).CreateAsync(new AssetRequest
        {
            Tag = "AST-0142",
            Name = "MacBook Pro 14",
            Category = "Laptop",
        })).Asset!.Id;

        Assert.True((await Resolve<IAssetService>(provider)
            .AssignAsync(assetId, new AssignAssetRequest { EmployeeId = ada })).Succeeded);

        // Nothing outstanding while they still work here.
        Assert.Empty((await CategoryAsync(provider, "assets")).Actions);

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            Employee employee = await seed.Employees.FirstAsync(row => row.Id == ada);

            employee.Status = EmployeeStatus.Inactive;

            await seed.SaveChangesAsync();
        }

        HrActionDto action = Assert.Single((await CategoryAsync(provider, "assets")).Actions);

        Assert.Equal("Asset still issued to a leaver", action.Title);
        Assert.Contains("AST-0142", action.Detail!, StringComparison.Ordinal);
        Assert.Contains("Ada Adeyemi", action.Detail!, StringComparison.Ordinal);
        Assert.Equal(HrActionSeverity.Critical, action.Severity);
    }

    // Somebody serving notice has not gone. Chasing their laptop back while
    // they are still using it to work is the wrong end of the stick, and it is
    // the same in-post question every other figure now agrees on.
    [SkippableFact]
    public async Task KitWithSomebodyServingNoticeIsNotChased()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada");

        Guid assetId = (await Resolve<IAssetService>(provider).CreateAsync(new AssetRequest
        {
            Tag = "AST-0143",
            Name = "Dell XPS",
            Category = "Laptop",
        })).Asset!.Id;

        Assert.True((await Resolve<IAssetService>(provider)
            .AssignAsync(assetId, new AssignAssetRequest { EmployeeId = ada })).Succeeded);

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            Employee employee = await seed.Employees.FirstAsync(row => row.Id == ada);

            employee.Status = EmployeeStatus.Offboarding;

            await seed.SaveChangesAsync();
        }

        Assert.Empty((await CategoryAsync(provider, "assets")).Actions);
    }

    // ── the tiles above the list ────────────────────────────────────────────

    [SkippableFact]
    public async Task TheSeverityTotalsMatchTheItems()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada", emergencyContact: false);

        await ReviewAsync(ada, Today.AddDays(-2), ReviewStatus.InProgress);

        HrActionsDashboardDto board = await BoardAsync(provider);

        // Counted on the server, because the three tiles and the list have to
        // agree and a client tallying a capped list would undercount.
        Assert.Equal(2, board.Total);
        Assert.Equal(1, board.Critical);
        Assert.Equal(0, board.Warning);
        Assert.Equal(1, board.Info);
    }

    // The count is the truth; the list is a sample. A company with forty
    // incomplete records needs to be told forty, not sent forty rows.
    [SkippableFact]
    public async Task TheCategoryCountIsNotCappedEvenThoughTheListIs()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        for (int index = 1; index <= 12; index++)
        {
            await HireAsync(provider, $"Person{index:D2}", emergencyContact: false);
        }

        HrActionCategoryDto compliance = await CategoryAsync(provider, "compliance");

        Assert.Equal(12, compliance.Count);
        Assert.Equal(10, compliance.Actions.Count);
    }
}
