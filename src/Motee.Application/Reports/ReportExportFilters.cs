using Motee.Domain.Authorization;

namespace Motee.Application.Reports;

// What a queued report export carries, stored as JSON on the export job.
//
// The scope on this record is the point, and leaving it out would be a quiet leak.
//
// An export is run later by a background worker with no request behind it: no signed-in
// user, no claims, nothing to resolve a permission against. The runner even has to make
// the tenant ambient from the job row before any query sees anything. So the worker
// cannot ask "how much of this may the caller see" — by the time it runs, there is no
// caller.
//
// Which leaves two wrong options and one right one. Run unscoped, and a team-scoped
// manager's export quietly contains the whole company — the same data their screen
// narrows, delivered by email as a spreadsheet. Run with nothing, and every export is
// an empty file. So the scope is resolved at request time, while there is still a user
// to resolve it from, and travels with the job.
//
// The viewer's employee id travels too, because some scopes are relative to the holder:
// DataScope.NeedsHolder is true for OwnDepartment and OwnBranch, and those mean nothing
// without knowing whose department.
public sealed record ReportExportFilters
{
    public required string Dataset { get; init; }

    // Already resolved and already gated against the requester's permissions, not the
    // keys they asked for. Required and never empty, because an absent list means "every
    // column the dataset declares" — which on the worker would quietly restore a column
    // the caller was not allowed to see, pay being the one that matters.
    public required IReadOnlyList<string> Columns { get; init; }

    public IReadOnlyDictionary<string, string?>? Filters { get; init; }

    // Resolved when the export was asked for, against the dataset's own module. Never
    // re-derived by the worker, which has no user to derive it from.
    public required DataScope Scope { get; init; }

    public Guid? ViewerEmployeeId { get; init; }
}
