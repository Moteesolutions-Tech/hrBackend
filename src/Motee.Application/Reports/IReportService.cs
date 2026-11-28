using Motee.Application.Common;

namespace Motee.Application.Reports;

// Running a report over one of the declared datasets.
//
// The catalogue beside this explains why it is a catalogue and not a query builder.
// What matters here is the shape of a result: a table of rows keyed by column, which is
// what a grid wants and what a CSV is — not a typed DTO per dataset, because the point
// is that one screen draws any of them.
public interface IReportService
{
    // Everything the caller may report on, with its columns and how many records it
    // currently holds.
    //
    // Datasets they cannot read are left out rather than listed and refused on use: a
    // catalogue offering a report that always comes back forbidden is a menu with
    // dishes the kitchen will not cook.
    Task<IReadOnlyList<ReportDatasetDto>> DatasetsAsync(
        CancellationToken cancellationToken = default);

    // A page of rows. Columns and filters are checked against the dataset, so an
    // unknown column is dropped and an unfilterable one is refused — quietly ignoring
    // a filter would answer a different question from the one asked.
    Task<ReportRunResult> RunAsync(
        ReportRunRequest request,
        CancellationToken cancellationToken = default);

    // The same report as a file, queued rather than read.
    //
    // Here rather than on the exports service because the decision an export needs is
    // the one this service makes: which datasets exist, whether the caller may read
    // this one, whether its filters can be honoured, and — the part that cannot be
    // deferred — how much of it they may see. A controller cannot supply that last
    // answer, and the worker that writes the file is past the point of being able to
    // ask. See ReportExportFilters.
    //
    // Refuses exactly what RunAsync refuses, through the same checks. An export path
    // that validated differently is how you get a queued report nobody can open.
    Task<ReportExportResult> ExportAsync(
        ReportExportRequest request,
        CancellationToken cancellationToken = default);
}

public enum ReportOutcome
{
    Succeeded,

    // No dataset by that key, or one the caller may not read. The same answer to both:
    // the catalogue already says what exists, so there is nothing to learn from a
    // sharper message.
    UnknownDataset,

    // A filter naming a column the dataset does not offer, or offers but cannot narrow
    // on. Named in the refusal, so whoever sent it knows which one.
    NotFilterable,
}

public sealed record ReportRunRequest : PagedQuery
{
    public required string Dataset { get; init; }

    // Omitted means every column the dataset offers.
    public IReadOnlyList<string>? Columns { get; init; }

    // Column key to value, matched case-insensitively and as a contains for text.
    // Exact for everything else, because a partial number or date is not a filter
    // anybody means.
    public IReadOnlyDictionary<string, string?>? Filters { get; init; }
}

// No paging, deliberately. An export is the whole result, and a Page of 3 on a request
// for a file would be a trap: it would succeed and hand back a job whose spreadsheet
// was missing most of the company.
public sealed record ReportExportRequest
{
    public required string Dataset { get; init; }

    public IReadOnlyList<string>? Columns { get; init; }

    public IReadOnlyDictionary<string, string?>? Filters { get; init; }

    // CSV unless asked otherwise, which is what every client sending no format today
    // already gets.
    public ReportExportFormat Format { get; init; } = ReportExportFormat.Csv;
}

public sealed record ReportExportResult
{
    public required ReportOutcome Outcome { get; init; }

    // The queued job, for polling or for /exports/mine. Null on a refusal, and nothing
    // is queued in that case — a failed export row would sit in somebody's list
    // claiming a file exists.
    public Exports.ExportJobDto? Job { get; init; }

    public string? Column { get; init; }

    public bool Succeeded => Outcome == ReportOutcome.Succeeded;

    public static ReportExportResult Failed(ReportOutcome outcome, string? column = null) =>
        new() { Outcome = outcome, Column = column };

    public static ReportExportResult Ok(Exports.ExportJobDto job) =>
        new() { Outcome = ReportOutcome.Succeeded, Job = job };
}

public sealed record ReportRunResult
{
    public required ReportOutcome Outcome { get; init; }

    public ReportTableDto? Table { get; init; }

    // Which filter was refused, so the message can name it.
    public string? Column { get; init; }

    public bool Succeeded => Outcome == ReportOutcome.Succeeded;

    public static ReportRunResult Failed(ReportOutcome outcome, string? column = null) =>
        new() { Outcome = outcome, Column = column };

    public static ReportRunResult Ok(ReportTableDto table) =>
        new() { Outcome = ReportOutcome.Succeeded, Table = table };
}

public sealed record ReportTableDto
{
    public required string Dataset { get; init; }

    public required string Label { get; init; }

    // The columns actually returned, in order, with their headers and kinds — so the
    // grid can draw itself from the response rather than holding its own copy of the
    // catalogue and getting out of step with it.
    public required IReadOnlyList<ReportColumn> Columns { get; init; }

    // One dictionary per row, keyed by column. Values are strings: a report grid and a
    // CSV both render text, and typing each cell would mean a DTO per dataset, which is
    // the thing this design exists to avoid. The column's Kind says how to read it.
    public required PagedResult<IReadOnlyDictionary<string, string?>> Rows { get; init; }

    // The day the figures were read, in the tenant's own zone. On the response because
    // "started this month" means nothing without it, and a client in another timezone
    // would label it wrongly.
    public required DateOnly AsOf { get; init; }
}

public sealed record ReportDatasetDto
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required string Group { get; init; }

    public required IReadOnlyList<ReportColumn> Columns { get; init; }

    // How many records it holds right now, so the reports list can say "1,284 records"
    // beside each one rather than making somebody open it to find out it is empty.
    public required int RecordCount { get; init; }
}
