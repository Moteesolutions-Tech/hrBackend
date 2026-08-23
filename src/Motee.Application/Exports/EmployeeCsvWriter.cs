using Motee.Application.Employees;

namespace Motee.Application.Exports;

public static class EmployeeCsvWriter
{
    public static string Header(IReadOnlyList<ExportColumn> columns) =>
        Line(columns.Select(column => column.Header));

    public static string Row(EmployeeListItemDto employee, IReadOnlyList<ExportColumn> columns) =>
        Line(columns.Select(column => column.Value(employee)));

    private static string Line(IEnumerable<string?> values) =>
        string.Join(",", values.Select(Escape)) + "\r\n";

    // Quote everything and double any embedded quote. A name containing a comma
    // would otherwise shift every later column by one.
    private static string Escape(string? value) =>
        $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
