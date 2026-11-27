using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Leave;
using Motee.Application.Organisation;
using Motee.Application.Reports;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Reporting over the declared datasets.
//
// Two things are being pinned. That a report carries the same narrowing as the screen
// it reports on — a report is the same data in a different shape, and the scope has to
// come from the dataset's own module rather than from the reports endpoint. And that a
// filter the dataset cannot honour is refused rather than ignored, because a report
// that quietly drops "department = Engineering" answers a different question and looks
// right doing it.
[Collection(PostgresCollection.Name)]
public class ReportServiceTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _ownerUserId;
    private Guid _engineeringId;
    private Guid _salesId;

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
        _ownerUserId = registration.UserId;

        ServiceProvider provider = fixture.BuildProvider();

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            _ownerUserId.ToString();

        _engineeringId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _salesId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IReportService Reports(ServiceProvider provider) =>
        Resolve<IReportService>(provider);

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid departmentId,
        Guid? managerId = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Adeyemi",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            ManagerId = managerId,
            StartDate = new DateOnly(2021, 1, 10),
        })).Employee!.Id;

    private static Task<ReportRunResult> RunAsync(
        ServiceProvider provider,
        string dataset,
        IReadOnlyDictionary<string, string?>? filters = null,
        IReadOnlyList<string>? columns = null) =>
        Reports(provider).RunAsync(new ReportRunRequest
        {
            Dataset = dataset,
            Filters = filters,
            Columns = columns,
            PageSize = 200,
        });

    // ── the catalogue ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheOwnerIsOfferedEveryDatasetWithItsRealCount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);
        await HireAsync(provider, "Ben", _salesId);

        IReadOnlyList<ReportDatasetDto> offered = await Reports(provider).DatasetsAsync();

        Assert.Equal(ReportCatalogue.All.Count, offered.Count);

        ReportDatasetDto employees = offered.First(dataset => dataset.Key == "employees");

        // The count comes through the same provider the report runs through, so it is
        // the number of rows the caller would actually get — not the size of the table.
        Assert.Equal(2, employees.RecordCount);
        Assert.Equal("Employees / Headcount", employees.Label);
        Assert.NotEmpty(employees.Columns);
    }

    // The case that makes the Self rule necessary. SelfServicePolicy grants every
    // signed-in employee View at Self scope on these modules, so "any scope at all"
    // would list every dataset to everybody — and a report of yourself is what your
    // own screens are for.
    [SkippableFact]
    public async Task AnOrdinaryEmployeeIsOfferedNoDatasets()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        await ActAsync(provider, adaId, "ada");

        Assert.Empty(await Reports(provider).DatasetsAsync());

        // And running one anyway comes back as though it did not exist, rather than
        // confirming that something they cannot read is there.
        Assert.Equal(
            ReportOutcome.UnknownDataset,
            (await RunAsync(provider, "employees")).Outcome);
    }

    // ── rows ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AReportReturnsTheColumnsAskedForInOrder()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ReportRunResult run = await RunAsync(
            provider, "employees", columns: ["department", "name"]);

        Assert.True(run.Succeeded, run.Outcome.ToString());

        ReportTableDto table = run.Table!;

        Assert.Equal(["department", "name"], table.Columns.Select(c => c.Key).ToArray());

        IReadOnlyDictionary<string, string?> row = Assert.Single(table.Rows.Items);

        // Only what was asked for. Building every column and letting the client pick
        // would put a phone number in a payload somebody deliberately left out.
        Assert.Equal(["department", "name"], row.Keys.Order().ToArray().Order().ToArray());
        Assert.Equal("Engineering", row["department"]);
        Assert.Equal("Ada Adeyemi", row["name"]);
    }

    [SkippableFact]
    public async Task TheDayTheFiguresWereReadIsOnTheAnswer()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ReportRunResult run = await RunAsync(provider, "employees");

        Assert.True(run.Succeeded, run.Outcome.ToString());
        Assert.Equal(
            DateOnly.FromDateTime(DateTime.UtcNow), run.Table!.AsOf);
    }

    // ── filters ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AFilterNarrowsByName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);
        await HireAsync(provider, "Ben", _salesId);

        ReportRunResult run = await RunAsync(
            provider,
            "employees",
            new Dictionary<string, string?> { ["department"] = "Engineering" });

        Assert.True(run.Succeeded, run.Outcome.ToString());

        IReadOnlyDictionary<string, string?> row = Assert.Single(run.Table!.Rows.Items);

        Assert.Equal("Ada Adeyemi", row["name"]);
    }

    // Case-insensitive, because a filter carries what somebody typed.
    [SkippableFact]
    public async Task ADepartmentFilterIgnoresCase()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ReportRunResult run = await RunAsync(
            provider,
            "employees",
            new Dictionary<string, string?> { ["department"] = "engineering" });

        Assert.True(run.Succeeded, run.Outcome.ToString());
        Assert.Single(run.Table!.Rows.Items);
    }

    // No rows rather than every row. A filter naming a department that does not exist
    // has an answer, and it is not "here is the whole company".
    [SkippableFact]
    public async Task AFilterForSomethingThatDoesNotExistReturnsNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ReportRunResult run = await RunAsync(
            provider,
            "employees",
            new Dictionary<string, string?> { ["department"] = "Marketing" });

        Assert.True(run.Succeeded, run.Outcome.ToString());
        Assert.Empty(run.Table!.Rows.Items);
        Assert.Equal(0, run.Table.Rows.TotalItems);
    }

    [SkippableFact]
    public async Task AStatusThatIsNotAStatusReturnsNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ReportRunResult run = await RunAsync(
            provider,
            "employees",
            new Dictionary<string, string?> { ["status"] = "Purple" });

        // Not the whole table, which would be the worst of the available answers.
        Assert.True(run.Succeeded, run.Outcome.ToString());
        Assert.Empty(run.Table!.Rows.Items);
    }

    // The refusal that matters. Ignoring it would answer a different question.
    [SkippableFact]
    public async Task AFilterOnAColumnThatCannotBeNarrowedIsRefusedByName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ReportRunResult refused = await RunAsync(
            provider,
            "employees",
            new Dictionary<string, string?> { ["email"] = "ada@" });

        Assert.Equal(ReportOutcome.NotFilterable, refused.Outcome);
        Assert.Equal("email", refused.Column);
    }

    [SkippableFact]
    public async Task AnUnknownDatasetIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ReportOutcome.UnknownDataset,
            (await RunAsync(provider, "payroll")).Outcome);
    }

    // ── the leave dataset ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheLeaveReportReadsTheLeaveRegister()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        Guid annualTypeId;

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            annualTypeId = await seed.LeaveTypes
                .Where(type => type.Code == "annual")
                .Select(type => type.Id)
                .FirstAsync();
        }

        DateOnly start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);

        Assert.True((await Resolve<ILeaveRequestService>(provider).SubmitAsync(
            new LeaveRequestSubmission
            {
                EmployeeId = adaId,
                LeaveTypeId = annualTypeId,
                StartDate = start,
                EndDate = start.AddDays(2),
                Reason = "Holiday",
            })).Succeeded);

        ReportRunResult run = await RunAsync(provider, "leave");

        Assert.True(run.Succeeded, run.Outcome.ToString());

        IReadOnlyDictionary<string, string?> row = Assert.Single(run.Table!.Rows.Items);

        Assert.Equal("Ada Adeyemi", row["employeeName"]);
        Assert.Equal("Annual Leave", row["leaveType"]);
        Assert.Equal("Engineering", row["department"]);

        // ISO on the wire, so the value means one day whatever the reader's locale.
        Assert.Equal(start.ToString("yyyy-MM-dd"), row["startDate"]);
        Assert.Equal("false", row["isHalfDay"]);
    }

    // ── signing in as somebody ──────────────────────────────────────────────

    private async Task ActAsync(ServiceProvider provider, Guid employeeId, string handle)
    {
        Guid userId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Users.Add(new ApplicationUser
            {
                Id = userId,
                TenantId = _tenantId,
                EmployeeId = employeeId,
                FirstName = handle,
                LastName = "Adeyemi",
                Email = $"{handle}@acme.com",
                NormalizedEmail = $"{handle.ToUpperInvariant()}@ACME.COM",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();
    }
}
