using Motee.Application.Employees;

namespace Motee.Application.Exports;

public static class EmployeeCsvWriter
{
    public static string Header(IReadOnlyList<ExportColumn> columns) =>
        Csv.Line(columns.Select(column => column.Header));

    public static string Row(EmployeeListItemDto employee, IReadOnlyList<ExportColumn> columns) =>
        Csv.Line(columns.Select(column => column.Value(employee)));
}
