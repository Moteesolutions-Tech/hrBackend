using System.Globalization;
using Motee.Application.Employees;

namespace Motee.Application.Exports;

public sealed record ExportColumn
{
    // What the caller asks for, e.g. "email". Stable across header rewording.
    public required string Key { get; init; }

    public required string Header { get; init; }

    public required Func<EmployeeListItemDto, string?> Value { get; init; }
}

// One definition per column: the key the caller selects by, the header it prints,
// and how to read it. Adding a field is one entry rather than three edits in three
// places that drift apart.
public static class EmployeeExportColumns
{
    public static readonly IReadOnlyList<ExportColumn> All =
    [
        Column("employeeNumber", "Employee ID", employee => employee.EmployeeNumber),
        Column("name", "Name", employee => employee.Name),
        Column("email", "Email", employee => employee.Email),
        Column("phone", "Phone", employee => employee.Phone),
        Column("jobTitle", "Job Title", employee => employee.JobTitle),
        Column("department", "Department", employee => employee.Department),
        Column("employmentType", "Employment Type", employee => employee.EmploymentType?.ToString()),
        Column("workMode", "Work Mode", employee => employee.WorkMode?.ToString()),
        Column("workLocation", "Work Location", employee => employee.WorkLocation),
        Column("manager", "Manager", employee => employee.ManagerName),
        Column("directReports", "Direct Reports",
            employee => employee.DirectReportCount.ToString(CultureInfo.InvariantCulture)),
        Column("startDate", "Start Date",
            employee => employee.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        Column("status", "Status", employee => employee.Status.ToString()),
    ];

    // Unknown keys are ignored rather than rejected: a saved view referencing a
    // column that has since gone should still export, minus that column.
    public static IReadOnlyList<ExportColumn> Resolve(IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count == 0)
        {
            return All;
        }

        List<ExportColumn> selected = [.. keys
            .Select(key => All.FirstOrDefault(column =>
                string.Equals(column.Key, key, StringComparison.OrdinalIgnoreCase)))
            .Where(column => column is not null)
            .Select(column => column!)];

        // Selecting only unknown keys would otherwise produce a file with no columns.
        return selected.Count == 0 ? All : selected;
    }

    private static ExportColumn Column(
        string key,
        string header,
        Func<EmployeeListItemDto, string?> value) =>
        new() { Key = key, Header = header, Value = value };
}
