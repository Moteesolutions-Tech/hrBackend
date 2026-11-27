using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Exports;
using Motee.Application.Organisation;
using Motee.Application.Reports;
using Motee.Domain.Authorization;
using Motee.Domain.Employees;
using Motee.Domain.Exports;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Exporting a report as a file.
//
// Most of what is pinned here is one fact: the file a background worker writes holds
// exactly the rows the person who asked for it was allowed to see. By the time it runs
// there is no request and no signed-in user to resolve a permission against, so the
// scope is resolved while the caller is still there and travels on the job row. If that
// round trip broke, a department-scoped manager's emailed spreadsheet would quietly
// contain the whole company, and every screen would still look right.
//
// The rest is that the queue, the retention, the signed link and the listing are the
// ones that already existed — a report export is a second kind of job, not a second
// machine.
[Collection(PostgresCollection.Name)]
public class ReportExportTests(PostgresFixture fixture)
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

    private static IExportService Exports(ServiceProvider provider) =>
        Resolve<IExportService>(provider);

    private async Task<Guid> HireAsync(ServiceProvider provider, string first, Guid departmentId) =>
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
            StartDate = new DateOnly(2021, 1, 10),
        })).Employee!.Id;

    private static Task<ReportExportResult> ExportAsync(
        ServiceProvider provider,
        string dataset,
        IReadOnlyDictionary<string, string?>? filters = null,
        IReadOnlyList<string>? columns = null) =>
        Reports(provider).ExportAsync(new ReportExportRequest
        {
            Dataset = dataset,
            Filters = filters,
            Columns = columns,
        });

    // The queue runs inline in tests, so asking for the export finishes it. The service
    // hands out signed links rather than bytes, so the file is read from storage.
    private static async Task<string> ReadCsvAsync(ServiceProvider provider, Guid exportId)
    {
        Guid tenantId = provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId!.Value;
        PostgresFixture.InMemoryFileStorage storage =
            provider.GetRequiredService<PostgresFixture.InMemoryFileStorage>();

        string key = storage.Keys.Single(candidate =>
            candidate.StartsWith($"exports/{tenantId:N}/{exportId:N}/", StringComparison.Ordinal));

        await using Stream content = (await storage.OpenReadAsync(key))!;
        using StreamReader reader = new(content, Encoding.UTF8);

        return await reader.ReadToEndAsync();
    }

    private static string[] LinesOf(string csv) =>
        csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    // A user holding one custom level, so a scope narrower than the owner's can be
    // tested end to end rather than asserted on in isolation.
    private async Task ActAsync(
        ServiceProvider provider,
        Guid employeeId,
        string handle,
        DataScope scope,
        string module)
    {
        Guid userId = Guid.NewGuid();
        Guid levelId = Guid.NewGuid();

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

            seed.AccessLevels.Add(new AccessLevel
            {
                Id = levelId,
                TenantId = _tenantId,
                Name = $"{handle} level",
                Kind = AccessLevelKind.Custom,
                Status = AccessLevelStatus.Active,
                Scope = scope,
                Permissions =
                [
                    new ModulePermission
                    {
                        Module = module,
                        Access = true,
                        Actions = [PermissionAction.View],
                    },
                ],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            seed.UserAccessLevels.Add(new UserAccessLevel
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                UserId = userId,
                AccessLevelId = levelId,
                AssignedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();
    }

    // ── the job ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AReportExportRunsAndBecomesDownloadable()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);
        await HireAsync(provider, "Ben", _salesId);

        ReportExportResult export = await ExportAsync(provider, "employees");

        Assert.True(export.Succeeded, export.Outcome.ToString());

        ExportJobDto finished = (await Exports(provider).GetAsync(export.Job!.Id))!;

        Assert.Equal(ExportStatus.Completed, finished.Status);
        Assert.Equal(2, finished.RowCount);
        Assert.NotNull(finished.ExpiresAt);
    }

    // Named after the dataset rather than "export.csv": somebody downloading three
    // reports in a morning has to be able to tell them apart in a downloads folder.
    [SkippableFact]
    public async Task TheFileIsNamedAfterTheDataset()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ReportExportResult export = await ExportAsync(provider, "employees");
        ExportJobDto finished = (await Exports(provider).GetAsync(export.Job!.Id))!;

        Assert.StartsWith("employees-", finished.FileName!, StringComparison.Ordinal);
        Assert.EndsWith(".csv", finished.FileName!, StringComparison.Ordinal);
    }

    // Reusing the existing machinery is most of the point, and this is what it buys:
    // a refresh no longer loses the download.
    [SkippableFact]
    public async Task AReportExportIsListedAmongTheCallersOwnExports()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ReportExportResult export = await ExportAsync(provider, "employees");

        ExportJobDto listed = Assert.Single(await Exports(provider).MineAsync());

        Assert.Equal(export.Job!.Id, listed.Id);

        // Prefixed, so a dataset called "employees" cannot be confused with the
        // employee export, which has a different filters shape entirely.
        Assert.Equal("report:employees", listed.Kind);
    }

    // The two kinds share a queue and a runner, so the branch that tells them apart is
    // worth pinning: both complete, and neither is built as the other.
    [SkippableFact]
    public async Task AnEmployeeExportAndAReportExportBuildDifferentFiles()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ExportJobDto employeeJob = await Exports(provider).RequestEmployeeExportAsync(
            new EmployeeExportFilters { Scope = DataScope.Everything });

        ReportExportResult report = await ExportAsync(provider, "employees", columns: ["name"]);

        Assert.Equal(
            ExportStatus.Completed,
            (await Exports(provider).GetAsync(employeeJob.Id))!.Status);

        Assert.Equal(
            ExportStatus.Completed,
            (await Exports(provider).GetAsync(report.Job!.Id))!.Status);

        string employeeCsv = await ReadCsvAsync(provider, employeeJob.Id);
        string reportCsv = await ReadCsvAsync(provider, report.Job!.Id);

        // One column, because the report asked for one. The employee export has its
        // own full set of columns, which is how the two files are told apart.
        Assert.Equal("\"Name\"", LinesOf(reportCsv)[0]);
        Assert.NotEqual(LinesOf(employeeCsv)[0], LinesOf(reportCsv)[0]);
    }

    // ── the file ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheFileHasAHeaderAndOneLinePerRow()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);
        await HireAsync(provider, "Ben", _salesId);
        await HireAsync(provider, "Cara", _salesId);

        ReportExportResult export = await ExportAsync(provider, "employees");
        string csv = await ReadCsvAsync(provider, export.Job!.Id);

        Assert.Equal(4, LinesOf(csv).Length);
        Assert.Contains("ada@acme.com", csv, StringComparison.Ordinal);
        Assert.Contains("ben@acme.com", csv, StringComparison.Ordinal);
    }

    // The header carries the catalogue's own labels, so the file reads the way the
    // screen did rather than exposing column keys.
    [SkippableFact]
    public async Task TheHeaderCarriesTheColumnLabelsInTheOrderAskedFor()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);

        ReportExportResult export = await ExportAsync(
            provider, "employees", columns: ["department", "name"]);

        string[] lines = LinesOf(await ReadCsvAsync(provider, export.Job!.Id));

        Assert.Equal("\"Department\",\"Name\"", lines[0]);
        Assert.Equal("\"Engineering\",\"Ada Adeyemi\"", lines[1]);
    }

    [SkippableFact]
    public async Task AFilterNarrowsTheFile()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada", _engineeringId);
        await HireAsync(provider, "Ben", _salesId);

        ReportExportResult export = await ExportAsync(
            provider,
            "employees",
            filters: new Dictionary<string, string?> { ["department"] = "Engineering" },
            columns: ["name"]);

        string csv = await ReadCsvAsync(provider, export.Job!.Id);

        Assert.Equal(2, LinesOf(csv).Length);
        Assert.Contains("Ada", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("Ben", csv, StringComparison.Ordinal);
    }

    // A quoting bug here shifts every later column by one, in a file somebody opens in
    // Excel and believes.
    [SkippableFact]
    public async Task AValueContainingACommaDoesNotShiftTheColumns()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Research, Design", Code = "RND" }))
            .Department!.Id;

        await HireAsync(provider, "Ada", departmentId);

        ReportExportResult export = await ExportAsync(
            provider, "employees", columns: ["department", "name"]);

        string[] lines = LinesOf(await ReadCsvAsync(provider, export.Job!.Id));

        Assert.Equal("\"Research, Design\",\"Ada Adeyemi\"", lines[1]);
    }

    // ── the scope round trip ────────────────────────────────────────────────

    // The reason ReportExportFilters carries a scope at all. The worker has no request
    // behind it, so without this the file would hold every employee in the company.
    [SkippableFact]
    public async Task AnExportHoldsOnlyTheRowsTheRequestersScopeReaches()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);
        await HireAsync(provider, "Ben", _salesId);

        await ActAsync(
            provider, adaId, "ada", DataScope.Departments(_engineeringId), "organization.employees");

        ReportExportResult export = await ExportAsync(provider, "employees", columns: ["name"]);

        Assert.True(export.Succeeded, export.Outcome.ToString());

        string csv = await ReadCsvAsync(provider, export.Job!.Id);

        Assert.Contains("Ada", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("Ben", csv, StringComparison.Ordinal);
    }

    // Asserted on the row itself as well as on the file, because this is the field that
    // would be silently absent if somebody simplified the filters record later. An
    // export with no stored scope is not an empty export; it is an unscoped one.
    [SkippableFact]
    public async Task TheResolvedScopeIsStoredOnTheJobForTheWorkerToReplay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        await ActAsync(
            provider, adaId, "ada", DataScope.Departments(_engineeringId), "organization.employees");

        ReportExportResult export = await ExportAsync(provider, "employees");

        ExportJob job;

        await using (MoteeDbContext read = fixture.CreateContext(_tenantId))
        {
            job = await read.ExportJobs.FindAsync(export.Job!.Id)
                ?? throw new InvalidOperationException("The export job was not written.");
        }

        ReportExportFilters stored = JsonSerializer.Deserialize<ReportExportFilters>(
            job.Filters, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal("employees", stored.Dataset);
        Assert.Equal(DataScopeKind.Department, stored.Scope.Kind);
        Assert.Equal([_engineeringId], stored.Scope.DepartmentIds);

        // Travels too, because OwnDepartment and OwnBranch mean nothing without
        // knowing whose department.
        Assert.Equal(adaId, stored.ViewerEmployeeId);
    }

    // ── refusals ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnUnknownDatasetQueuesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ReportExportResult export = await ExportAsync(provider, "payroll-ledger");

        Assert.Equal(ReportOutcome.UnknownDataset, export.Outcome);
        Assert.Null(export.Job);
        Assert.Empty(await Exports(provider).MineAsync());
    }

    // Refused rather than ignored, and for the same reason a run is: a file that
    // quietly dropped the filter answers a different question and looks right doing it
    // — worse on a spreadsheet nobody can see the query behind.
    [SkippableFact]
    public async Task AFilterOnAnUnfilterableColumnIsRefusedAndNamed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ReportExportResult export = await ExportAsync(
            provider,
            "employees",
            filters: new Dictionary<string, string?> { ["phone"] = "0801" });

        Assert.Equal(ReportOutcome.NotFilterable, export.Outcome);
        Assert.Equal("phone", export.Column);
        Assert.Empty(await Exports(provider).MineAsync());
    }

    // The self-service floor again. Every signed-in employee holds View at Self scope
    // on this module, so without the broader-than-Self rule anybody could export a
    // one-row report — and the export path must refuse exactly what the run path does.
    [SkippableFact]
    public async Task AnOrdinaryEmployeeCannotExportAnything()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid adaId = await HireAsync(provider, "Ada", _engineeringId);

        await ActAsync(provider, adaId, "ada", DataScope.Everything, "talent.performance");

        ReportExportResult export = await ExportAsync(provider, "employees");

        Assert.Equal(ReportOutcome.UnknownDataset, export.Outcome);
        Assert.Null(export.Job);
    }
}
