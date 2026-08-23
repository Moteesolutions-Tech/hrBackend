using Motee.Domain.Authorization;
using Motee.Domain.Employees;
using Motee.Domain.Exports;
using Motee.Domain.Organisation;

namespace Motee.Application.Exports;

public interface IExportService
{
    Task<ExportJobDto> RequestEmployeeExportAsync(
        EmployeeExportFilters filters,
        CancellationToken cancellationToken = default);

    Task<ExportJobDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // A fresh signed link for a caller who is still signed in. The emailed one
    // expires; this is how they get another without re-running the export.
    Task<ExportLink?> CreateLinkAsync(Guid id, CancellationToken cancellationToken = default);
}

// Runs on the background worker. Separate from IExportService so the enqueue path
// and the execution path cannot be confused for each other.
public interface IExportRunner
{
    Task RunAsync(Guid exportId, CancellationToken cancellationToken = default);
}

// The filters, not a query. Scope and viewer are captured at request time and
// replayed, so the job cannot widen what the requester was allowed to see.
public sealed record EmployeeExportFilters
{
    // Serialised into the job row and replayed on the worker, so the whole reach has
    // to survive the round trip — including the departments a named scope covers.
    public required DataScope Scope { get; init; }

    public Guid? ViewerEmployeeId { get; init; }

    public string? Search { get; init; }

    public Guid? DepartmentId { get; init; }

    public EmployeeStatus? Status { get; init; }

    public EmploymentType? EmploymentType { get; init; }

    public WorkMode? WorkMode { get; init; }

    public DateOnly? StartedFrom { get; init; }

    public DateOnly? StartedTo { get; init; }

    // Which columns to write. Empty means every column — the caller opts in to a
    // narrower file rather than the server deciding for them.
    public IReadOnlyList<string>? Columns { get; init; }
}

public sealed record ExportJobDto
{
    public required Guid Id { get; init; }

    public required ExportStatus Status { get; init; }

    public required string Kind { get; init; }

    public int RowCount { get; init; }

    public string? FileName { get; init; }

    public string? Error { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

// What a column picker needs: the key to send back, and what to call it on screen.
// The value function stays server-side.
public sealed record ExportColumnDto
{
    public required string Key { get; init; }

    public required string Label { get; init; }
}

public sealed record ExportLink
{
    public required string Url { get; init; }

    public required string FileName { get; init; }

    // When the link stops working — not when the export is deleted.
    public required DateTimeOffset LinkExpiresAt { get; init; }
}
