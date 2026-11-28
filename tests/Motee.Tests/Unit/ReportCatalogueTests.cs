using Motee.Application.Reports;
using Motee.Domain.Authorization;

namespace Motee.Tests.Unit;

// What can be reported on, and what a caller may ask for.
//
// This is the piece that replaces the requested query builder. POST /reports/run was
// specified as a dataset plus arbitrary columns, filters, groupBy and sort — an
// API-level SQL builder over a schema that is the wrong shape for one, because a good
// part of it is jsonb behind value converters that cannot be filtered in SQL at all.
//
// So the catalogue declares what exists and the rest is refused. These tests are about
// the refusals, because that is where a declared surface earns its keep.
//
// Written before the service.
public class ReportCatalogueTests
{
    [Fact]
    public void EveryDatasetIsNamedOnceAndCarriesColumns()
    {
        Assert.NotEmpty(ReportCatalogue.All);

        Assert.Equal(
            ReportCatalogue.All.Count,
            ReportCatalogue.All
                .Select(dataset => dataset.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());

        Assert.All(ReportCatalogue.All, dataset =>
        {
            Assert.False(string.IsNullOrWhiteSpace(dataset.Key));
            Assert.False(string.IsNullOrWhiteSpace(dataset.Label));
            Assert.False(string.IsNullOrWhiteSpace(dataset.Module));
            Assert.NotEmpty(dataset.Columns);
        });
    }

    [Fact]
    public void EveryColumnIsNamedOnceWithinItsDataset() =>
        Assert.All(ReportCatalogue.All, dataset =>
            Assert.Equal(
                dataset.Columns.Count,
                dataset.Columns
                    .Select(column => column.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()));

    // Every dataset has at least one way to narrow it. A report of eighty thousand rows
    // with no filter is a download, not a report.
    [Fact]
    public void EveryDatasetCanBeNarrowed() =>
        Assert.All(ReportCatalogue.All, dataset =>
            Assert.Contains(dataset.Columns, column => column.Filterable));

    [Fact]
    public void ADatasetIsFoundByKeyIgnoringCase()
    {
        Assert.NotNull(ReportCatalogue.Find("employees"));
        Assert.NotNull(ReportCatalogue.Find("Employees"));
        Assert.NotNull(ReportCatalogue.Find("  leave  "));
    }

    [Fact]
    public void AnUnknownDatasetIsNotFound()
    {
        Assert.Null(ReportCatalogue.Find("payroll"));
        Assert.Null(ReportCatalogue.Find(""));
        Assert.Null(ReportCatalogue.Find(null));
    }

    // ── choosing columns ────────────────────────────────────────────────────

    [Fact]
    public void AskingForNoColumnsGivesThemAll()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        Assert.Equal(
            employees.Columns.Count,
            ReportCatalogue.Resolve(employees.Columns, null).Count);

        Assert.Equal(
            employees.Columns.Count,
            ReportCatalogue.Resolve(employees.Columns, []).Count);
    }

    [Fact]
    public void ColumnsComeBackInTheOrderAsked()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        IReadOnlyList<ReportColumn> chosen =
            ReportCatalogue.Resolve(employees.Columns, ["department", "fullName"]);

        Assert.Equal(["department", "fullName"], chosen.Select(column => column.Key).ToArray());
    }

    // Dropped rather than refused, matching EmployeeExportColumns: a saved report naming
    // a column that has since gone should still run, minus that column, rather than
    // becoming unopenable.
    [Fact]
    public void AnUnknownColumnIsDropped()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        IReadOnlyList<ReportColumn> chosen =
            ReportCatalogue.Resolve(employees.Columns, ["fullName", "bonus", "email"]);

        Assert.Equal(["fullName", "email"], chosen.Select(column => column.Key).ToArray());
    }

    // Asking only for columns that no longer exist would otherwise produce a table with
    // no columns at all, which reads as "no data" rather than "your saved view is stale".
    [Fact]
    public void AskingOnlyForUnknownColumnsFallsBackToAll()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        Assert.Equal(
            employees.Columns.Count,
            ReportCatalogue.Resolve(employees.Columns, ["bonus", "shoeSize"]).Count);
    }

    // ── filters ─────────────────────────────────────────────────────────────

    [Fact]
    public void AFilterOnAFilterableColumnPasses()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        Assert.Null(ReportCatalogue.UnfilterableColumn(
            employees.Columns,
            new Dictionary<string, string?> { ["department"] = "Engineering" }));
    }

    // Refused rather than ignored, and this is the one that matters. A report that
    // quietly drops "department = Engineering" answers a different question from the
    // one asked, and looks right while doing it.
    [Fact]
    public void AFilterOnAColumnThatCannotBeNarrowedIsNamed()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        Assert.Equal(
            "email",
            ReportCatalogue.UnfilterableColumn(
                employees.Columns,
                new Dictionary<string, string?> { ["email"] = "ada@" }));
    }

    [Fact]
    public void AFilterOnAColumnTheDatasetDoesNotHaveIsNamed()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        Assert.Equal(
            "bonus",
            ReportCatalogue.UnfilterableColumn(
                employees.Columns,
                new Dictionary<string, string?> { ["bonus"] = "50000" }));
    }

    [Fact]
    public void NoFiltersIsFine()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        Assert.Null(ReportCatalogue.UnfilterableColumn(employees.Columns, null));
        Assert.Null(ReportCatalogue.UnfilterableColumn(
            employees.Columns, new Dictionary<string, string?>()));
    }

    // ── who may read what ───────────────────────────────────────────────────

    // A report is the same data in a different shape, so it is the same permission.
    // Somebody who cannot open the leave screen has no business exporting it.
    [Fact]
    public void ADatasetNeedsItsOwnModulePermission()
    {
        ReportDataset leave = ReportCatalogue.Find("leave")!;

        Assert.False(ReportCatalogue.CanRead(
            AccessLevelPermissions.Nothing, isOwner: false, leave));

        Assert.True(ReportCatalogue.CanRead(
            Holding(leave.Module), isOwner: false, leave));
    }

    [Fact]
    public void HoldingOneModuleDoesNotOpenAnother()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;
        ReportDataset assets = ReportCatalogue.Find("assets")!;

        Assert.True(ReportCatalogue.CanRead(
            Holding(employees.Module), isOwner: false, employees));

        Assert.False(ReportCatalogue.CanRead(
            Holding(employees.Module), isOwner: false, assets));
    }

    // The case that makes the rule necessary rather than arbitrary.
    //
    // SelfServicePolicy grants every signed-in employee View at Self scope on several
    // of these modules, so a test of "any scope at all" would list every dataset to
    // everybody. Nothing leaks — Self narrows to the one person asking — but a reports
    // catalogue offering "Employees — 1 record" is noise, and a report of yourself is
    // what your own screens are for.
    [Fact]
    public void SelfScopeAloneIsNotEnoughToOfferAReport()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        // Nothing granted at all, which is what an ordinary employee holds. The floor
        // still resolves to Self on this module.
        DataScope floor = ReportCatalogue.ScopeFor(
            AccessLevelPermissions.Nothing, isOwner: false, employees);

        Assert.Equal(DataScopeKind.Self, floor.Kind);

        Assert.False(ReportCatalogue.CanRead(
            AccessLevelPermissions.Nothing, isOwner: false, employees));
    }

    // The owner holds everything, as everywhere else.
    [Fact]
    public void TheOwnerReadsEveryDataset() =>
        Assert.All(ReportCatalogue.All, dataset =>
            Assert.True(ReportCatalogue.CanRead(
                AccessLevelPermissions.Nothing, isOwner: true, dataset)));

    // The scope comes from the dataset's own module, not from the reports endpoint —
    // and it has to, or a team-scoped manager would get the whole company in CSV form
    // while their own screen correctly narrows the same data.
    [Fact]
    public void TheScopeIsTheOneTheDatasetsOwnModuleGrants()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        AccessLevelPermissions team = new()
        {
            Modules =
            [
                new ModulePermission
                {
                    Module = employees.Module,
                    Access = true,
                    Actions = [PermissionAction.View],
                },
            ],
            Scope = new DataScope { Kind = DataScopeKind.DirectReports },
        };

        Assert.Equal(
            DataScopeKind.DirectReports,
            ReportCatalogue.ScopeFor(team, isOwner: false, employees).Kind);

        // A dataset their permission does not cover is not offered, so the report
        // cannot be run wider than the screen. Self rather than None, because the
        // self-service floor reaches assets too — and Self is below the bar.
        Assert.False(ReportCatalogue.CanRead(
            team, isOwner: false, ReportCatalogue.Find("assets")!));
    }

    [Fact]
    public void TheOwnerSeesEverything() =>
        Assert.Equal(
            DataScopeKind.All,
            ReportCatalogue.ScopeFor(
                AccessLevelPermissions.Nothing,
                isOwner: true,
                ReportCatalogue.Find("employees")!).Kind);

    // ── what the reports screen reads ───────────────────────────────────────

    // The frontend's report registry is the spec, and five of its reports share one row
    // shape: src/lib/reports/registry/people.ts declares EmpRow and selectEmployeeRows,
    // read by Employees, Gender Split, Age Demographics, Department Breakdown and
    // Hiring Trend. Their analytics are computed in the browser from these fields, so a
    // field missing here is not a narrower table — it is a chart that cannot be drawn.
    //
    // Pinned as a list rather than left to drift, because the failure is silent: the
    // report renders, with an empty graph.
    [Fact]
    public void TheEmployeesDatasetCarriesEveryFieldTheReportsScreenReads()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        string[] needed =
        [
            "employeeNumber", "fullName", "department", "jobTitle", "employmentType",
            "status", "gender", "grade", "age", "startDate", "tenureYears", "manager",
            "directReports", "salary", "region",
        ];

        Assert.All(needed, key => Assert.Contains(
            key,
            employees.Columns.Select(column => column.Key),
            StringComparer.Ordinal));
    }

    // Every dataset's keys are pinned to the row shape the reports screen reads, for
    // the same reason the employee list above is: the screen computes its charts in the
    // browser from these keys, so a rename here does not fail — it renders an empty
    // graph, which nobody notices until somebody trusts it.
    //
    // Only the fields the screen actually reads are listed. A dataset may offer more.
    [Theory]
    [InlineData("leave", "employee type status startDate endDate days")]
    [InlineData("attendance", "date employee department status clockIn clockOut hours")]
    [InlineData("assets", "assetTag name category condition status assignedTo value")]
    [InlineData("performance", "employee cycle self manager calibrated completedAt")]
    [InlineData("learning", "employee course category status progress enrolledAt")]
    public void ADatasetCarriesTheKeysTheReportsScreenReads(string key, string needed)
    {
        ReportDataset dataset = ReportCatalogue.Find(key)
            ?? throw new InvalidOperationException($"No dataset '{key}'.");

        Assert.All(needed.Split(' '), column => Assert.Contains(
            column,
            dataset.Columns.Select(declared => declared.Key),
            StringComparer.Ordinal));
    }

    // The screen groups reports under the group this names, so a dataset in the wrong
    // one lands on the wrong tab. Leave was Operations and belongs with the workforce.
    [Theory]
    [InlineData("employees", "People")]
    [InlineData("leave", "People")]
    [InlineData("attendance", "People")]
    [InlineData("assets", "Operations")]
    [InlineData("performance", "Talent")]
    [InlineData("learning", "Talent")]
    public void ADatasetIsInTheGroupTheScreenExpects(string key, string group) =>
        Assert.Equal(group, ReportCatalogue.Find(key)!.Group);

    // ── columns with their own permission ───────────────────────────────────

    // Pay is the case that makes a per-column gate necessary. The frontend declares an
    // "Annual Salary" column on a report gated by organization.employees, so as
    // specified, anybody who can open the staff list reads everyone's pay.
    //
    // Salary is recorded on the contract, so the permission for reading it is
    // operations.contracts. The column is dropped and the rows are kept: a manager
    // without contract access should still get their team's roster.
    [Fact]
    public void PayIsNotOfferedToSomebodyWhoCanOnlySeeTheStaffList()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        IReadOnlyList<ReportColumn> visible = ReportCatalogue.Visible(
            employees, Holding(employees.Module), isOwner: false);

        Assert.DoesNotContain(
            "salary", visible.Select(column => column.Key), StringComparer.Ordinal);

        // The rest of the roster survives — this drops a column, not the report.
        Assert.Contains("fullName", visible.Select(column => column.Key), StringComparer.Ordinal);
    }

    [Fact]
    public void PayIsOfferedToSomebodyWhoCanReadContracts()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        IReadOnlyList<ReportColumn> visible = ReportCatalogue.Visible(
            employees,
            Holding(employees.Module, "operations.contracts"),
            isOwner: false);

        Assert.Contains("salary", visible.Select(column => column.Key), StringComparer.Ordinal);
    }

    // The self-service floor reaches operations.contracts at Self scope, so "holds the
    // module at all" would hand everybody the pay column. Same bar as CanRead: seeing
    // your own contract is not permission to see the team's pay.
    [Fact]
    public void PayIsNotOfferedOnContractsHeldOnlyAtSelfScope()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        AccessLevelPermissions self = new()
        {
            Modules =
            [
                new ModulePermission
                {
                    Module = employees.Module,
                    Access = true,
                    Actions = [PermissionAction.View],
                },
                new ModulePermission
                {
                    Module = "operations.contracts",
                    Access = true,
                    Actions = [PermissionAction.View],
                },
            ],
            Scope = new DataScope { Kind = DataScopeKind.Self },
        };

        Assert.DoesNotContain(
            "salary",
            ReportCatalogue.Visible(employees, self, isOwner: false)
                .Select(column => column.Key),
            StringComparer.Ordinal);
    }

    [Fact]
    public void TheOwnerIsOfferedEveryColumn() =>
        Assert.All(ReportCatalogue.All, dataset => Assert.Equal(
            dataset.Columns.Count,
            ReportCatalogue.Visible(dataset, AccessLevelPermissions.Nothing, isOwner: true).Count));

    // Naming the column explicitly must not get round the gate. Resolve runs over what
    // Visible returned, so a caller who cannot see pay cannot select it either.
    [Fact]
    public void AskingForAGatedColumnByNameDoesNotProduceIt()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        IReadOnlyList<ReportColumn> chosen = ReportCatalogue.Resolve(
            ReportCatalogue.Visible(employees, Holding(employees.Module), isOwner: false),
            ["fullName", "salary"]);

        Assert.Equal(["fullName"], chosen.Select(column => column.Key).ToArray());
    }

    // And neither can it be filtered on. A filter over a hidden column leaks it by
    // halves: narrow on salary > 50000, count the rows, repeat.
    [Fact]
    public void AGatedColumnCannotBeFilteredOnEither()
    {
        ReportDataset employees = ReportCatalogue.Find("employees")!;

        Assert.Equal(
            "salary",
            ReportCatalogue.UnfilterableColumn(
                ReportCatalogue.Visible(employees, Holding(employees.Module), isOwner: false),
                new Dictionary<string, string?> { ["salary"] = "50000" }));
    }

    private static AccessLevelPermissions Holding(params string[] modules) => new()
    {
        Modules =
        [
            .. modules.Select(module => new ModulePermission
            {
                Module = module,
                Access = true,
                Actions = [PermissionAction.View],
            }),
        ],
        Scope = new DataScope { Kind = DataScopeKind.All },
    };
}
