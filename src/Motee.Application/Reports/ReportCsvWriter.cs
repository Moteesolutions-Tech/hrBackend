using Motee.Application.Exports;

namespace Motee.Application.Reports;

// A report row is a dictionary keyed by column, not a typed DTO — that is the whole
// point of the catalogue — so the column list decides the order and the row supplies
// the values. Writing the dictionary's own keys instead would give a file whose columns
// move about depending on which rows came back.
public static class ReportCsvWriter
{
    public static string Header(IReadOnlyList<ReportColumn> columns) =>
        Csv.Line(columns.Select(column => column.Header));

    // A column the row does not carry writes an empty cell rather than throwing. The
    // row shape comes from a provider and the column list from the catalogue; a
    // provider that stops emitting one should leave a gap in the file, not fail an
    // export somebody has been waiting on.
    public static string Row(
        IReadOnlyDictionary<string, string?> row,
        IReadOnlyList<ReportColumn> columns) =>
        Csv.Line(columns.Select(column => row.GetValueOrDefault(column.Key)));
}
