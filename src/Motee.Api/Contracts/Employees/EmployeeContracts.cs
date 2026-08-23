using Motee.Domain.Employees;
using Motee.Domain.Organisation;

namespace Motee.Api.Contracts.Employees;

public sealed record ChangeEmployeeStatusRequest
{
    public required EmployeeStatus Status { get; init; }
}

// Bound from the query string by the list and the stats. One record rather than a
// row of positional parameters: two adjacent enums and two adjacent dates would
// transpose without the compiler noticing.
public sealed record EmployeeFilters
{
    public string? Search { get; init; }

    public Guid? DepartmentId { get; init; }

    public EmployeeStatus? Status { get; init; }

    public EmploymentType? EmploymentType { get; init; }

    public WorkMode? WorkMode { get; init; }

    public DateOnly? StartedFrom { get; init; }

    public DateOnly? StartedTo { get; init; }
}

// A body rather than query strings: the column list is an array, and a filter set
// long enough to be worth exporting does not belong in a URL. Every field is
// optional — an empty body exports everything the caller may see.
public sealed record ExportEmployeesRequest
{
    public EmployeeFilters Filters { get; init; } = new();

    // Keys from GET /employees/export/columns. Unknown keys are ignored and an empty
    // list means every column, so a saved view never fails to export.
    public IReadOnlyList<string>? Columns { get; init; }
}
