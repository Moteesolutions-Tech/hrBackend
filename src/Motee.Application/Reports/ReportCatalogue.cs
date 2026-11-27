using Motee.Domain.Authorization;

namespace Motee.Application.Reports;

// What can be reported on, and which columns each dataset offers.
//
// This is deliberately not a query builder. The request was POST /reports/run taking a
// dataset plus arbitrary columns, filters, groupBy and sort — which is an API-level SQL
// builder, and this schema is the wrong shape for one. A good part of it is jsonb behind
// value converters: a blackout's departments, a holiday's exemptions, an access level's
// permissions, an intake request's field map. None of those can be filtered in SQL, so a
// generic engine over them either throws at runtime or quietly loads whole tables to
// filter in memory — and the second is worse, because it works until the table is big.
//
// A declared catalogue gives the same screen. Each dataset names its columns and which
// of them may be filtered; anything else is refused. The pattern is already here —
// EmployeeExportColumns does exactly this for one dataset, and ProfileFields and
// ApprovalIntake do it for their own surfaces — so this generalises something the
// codebase already relies on rather than introducing a new idea.
//
// Rows come from the module's own list service rather than fresh queries, so a report
// and the screen it reports on cannot disagree. That is the same reason
// /dashboard/mine delegates for its clock.
public enum ReportColumnKind
{
    Text,
    Number,

    // ISO on the wire. A date is a date whatever the reader's locale, and formatting
    // belongs to whoever draws the grid.
    Date,

    Boolean,
}

public sealed record ReportColumn
{
    // What the caller selects by. Stable across header rewording, like an export
    // column's key.
    public required string Key { get; init; }

    public required string Header { get; init; }

    public required ReportColumnKind Kind { get; init; }

    // Whether this column can be narrowed on. Only the ones a real query can narrow:
    // offering a filter the service has to apply in memory would be a filter that
    // works on a hundred rows and times out on a hundred thousand.
    public bool Filterable { get; init; }
}

public sealed record ReportDataset
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    // How the reports screen groups them: People, Operations, Talent.
    public required string Group { get; init; }

    // The module permission the dataset's own screen is behind. A report is the same
    // data in a different shape, so it is the same permission — somebody who cannot
    // open the leave screen has no business exporting it.
    public required string Module { get; init; }

    public required IReadOnlyList<ReportColumn> Columns { get; init; }
}

public static class ReportCatalogue
{
    public const string People = "People";

    public const string Operations = "Operations";

    public const string Talent = "Talent";

    // Three datasets to begin with, each reading its module's own list service. The
    // registry is the point: adding the remaining fourteen is one entry and one
    // projection each, with nothing else to change — and a dataset declared here with
    // no rows behind it would be a form that always comes back empty, so they arrive
    // together.
    public static readonly IReadOnlyList<ReportDataset> All =
    [
        new ReportDataset
        {
            Key = "employees",
            Label = "Employees / Headcount",
            Group = People,
            Module = "organization.employees",
            Columns =
            [
                Text("employeeNumber", "Employee ID"),
                Text("name", "Name", filterable: true),
                Text("email", "Email"),
                Text("phone", "Phone"),
                Text("jobTitle", "Job Title", filterable: true),
                Text("department", "Department", filterable: true),
                Text("employmentType", "Employment Type", filterable: true),
                Text("workMode", "Work Mode", filterable: true),
                Text("workLocation", "Work Location"),
                Text("manager", "Manager"),
                Number("directReports", "Direct Reports"),
                Date("startDate", "Start Date", filterable: true),
                Text("status", "Status", filterable: true),
            ],
        },

        new ReportDataset
        {
            Key = "leave",
            Label = "Leave",
            Group = Operations,
            Module = "time-payroll.leave",
            Columns =
            [
                Text("employeeName", "Employee", filterable: true),
                Text("department", "Department", filterable: true),
                Text("leaveType", "Leave Type", filterable: true),
                Date("startDate", "From", filterable: true),
                Date("endDate", "To"),
                Number("totalDays", "Days"),
                Boolean("isHalfDay", "Half Day"),
                Text("status", "Status", filterable: true),
                Text("reason", "Reason"),
                Text("relief", "Covered By"),
            ],
        },

        new ReportDataset
        {
            Key = "assets",
            Label = "Assets",
            Group = Operations,
            Module = "operations.assets",
            Columns =
            [
                Text("tag", "Tag", filterable: true),
                Text("name", "Asset"),
                Text("category", "Category", filterable: true),
                Text("serialNumber", "Serial Number"),
                Text("assignedTo", "Assigned To", filterable: true),
                Date("assignedDate", "Assigned On"),
                Text("status", "Status", filterable: true),
            ],
        },
    ];

    public static ReportDataset? Find(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(dataset =>
                string.Equals(dataset.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    // The columns a caller asked for, or all of them.
    //
    // Unknown keys are dropped rather than refused, matching EmployeeExportColumns: a
    // saved report naming a column that has since gone should still run, minus that
    // column, rather than becoming unopenable.
    public static IReadOnlyList<ReportColumn> Resolve(
        ReportDataset dataset,
        IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count == 0)
        {
            return dataset.Columns;
        }

        List<ReportColumn> selected =
        [
            .. keys
                .Select(key => dataset.Columns.FirstOrDefault(column =>
                    string.Equals(column.Key, key, StringComparison.OrdinalIgnoreCase)))
                .OfType<ReportColumn>(),
        ];

        // Asking only for columns that no longer exist would otherwise produce a table
        // with no columns at all.
        return selected.Count == 0 ? dataset.Columns : selected;
    }

    // A filter naming a column that is not filterable, or not in the dataset at all, is
    // refused rather than ignored. Dropping it silently would answer a different
    // question from the one asked — and a report that quietly ignores "department =
    // Engineering" is worse than one that refuses it.
    public static string? UnfilterableColumn(
        ReportDataset dataset,
        IReadOnlyDictionary<string, string?>? filters)
    {
        if (filters is null)
        {
            return null;
        }

        foreach (string key in filters.Keys)
        {
            ReportColumn? column = dataset.Columns.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));

            if (column is null || !column.Filterable)
            {
                return key;
            }
        }

        return null;
    }

    // How much of a dataset the caller may see, resolved per dataset against its own
    // module rather than against the reports endpoint.
    //
    // That distinction is the whole of report safety and it is easy to get wrong. A
    // controller's GrantedScope() reads the scope the permission handler put in
    // HttpContext.Items for the module named on *that endpoint* — so a reports
    // controller gets the scope for the reports module, which says nothing about how
    // much of the employee list the caller may read. Passing that, or worse passing
    // DataScope.Everything, would hand a team-scoped manager the whole company in CSV
    // form, which is the same data their own screen correctly narrows.
    //
    // So the scope comes from here, per dataset, and travels into the module's own
    // typed query — which already knows how to narrow by it.
    public static DataScope ScopeFor(
        AccessLevelPermissions? held,
        bool isOwner,
        ReportDataset dataset) =>
        isOwner
            ? new DataScope { Kind = DataScopeKind.All }
            : AccessDecision.Resolve(held, dataset.Module, PermissionAction.View);

    // Whether a dataset is worth offering them. Datasets they cannot read are left out
    // of the catalogue rather than listed and refused on use.
    //
    // The bar is a scope *broader than Self*, which is not the obvious choice and is
    // the right one. SelfServicePolicy grants every signed-in employee View at Self
    // scope on several of these modules — employees, leave, attendance, assets — so a
    // test of "any scope at all" lists every one of them to everybody.
    //
    // Nothing leaks if it does: Self narrows to the one person asking, which is the
    // same data their own screens already show them. But a reports catalogue offering
    // "Employees — 1 record" is noise, and a report of yourself is not what the screen
    // is for. Somebody who can only see themselves has self-service, not reporting.
    public static bool CanRead(AccessLevelPermissions? held, bool isOwner, ReportDataset dataset) =>
        ScopeFor(held, isOwner, dataset).Kind > DataScopeKind.Self;

    private static ReportColumn Text(string key, string header, bool filterable = false) =>
        new() { Key = key, Header = header, Kind = ReportColumnKind.Text, Filterable = filterable };

    private static ReportColumn Number(string key, string header, bool filterable = false) =>
        new()
        {
            Key = key,
            Header = header,
            Kind = ReportColumnKind.Number,
            Filterable = filterable,
        };

    private static ReportColumn Date(string key, string header, bool filterable = false) =>
        new() { Key = key, Header = header, Kind = ReportColumnKind.Date, Filterable = filterable };

    private static ReportColumn Boolean(string key, string header, bool filterable = false) =>
        new()
        {
            Key = key,
            Header = header,
            Kind = ReportColumnKind.Boolean,
            Filterable = filterable,
        };
}
