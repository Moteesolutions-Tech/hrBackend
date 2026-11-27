namespace Motee.Application.Exports;

// One CSV spelling for every export.
//
// Shared rather than written twice because the failure mode is invisible: a writer that
// forgets to quote produces a file that opens fine until one row contains a comma, and
// then every column after it is shifted by one in a spreadsheet somebody believes.
public static class Csv
{
    public static string Line(IEnumerable<string?> values) =>
        string.Join(",", values.Select(Escape)) + "\r\n";

    // Quote everything and double any embedded quote. A name containing a comma
    // would otherwise shift every later column by one.
    private static string Escape(string? value) =>
        $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
