using Motee.Domain.Organisation;

namespace Motee.Application.Organisation;

public interface IDepartmentService
{
    Task<IReadOnlyList<DepartmentDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<DepartmentDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<DepartmentResult> CreateAsync(DepartmentRequest request, CancellationToken cancellationToken = default);

    Task<DepartmentResult> UpdateAsync(Guid id, DepartmentRequest request, CancellationToken cancellationToken = default);

    Task<DepartmentOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public enum DepartmentOutcome
{
    Succeeded,
    NotFound,
    DuplicateName,
    DuplicateCode,

    // Deleting would orphan people. The tenant deactivates instead.
    HasEmployees,
}

public sealed record DepartmentRequest
{
    public required string Name { get; init; }

    public required string Code { get; init; }

    public string? Description { get; init; }

    public Guid? HeadEmployeeId { get; init; }

    // Which business unit this department sits under. Optional — a company with no
    // divisions leaves it null — but it is the link an access level scoped to a
    // business unit walks to find employees, so a department left unassigned is
    // invisible to every such level.
    public Guid? BusinessUnitId { get; init; }

    public decimal? BudgetMonthly { get; init; }

    public DepartmentStatus Status { get; init; } = DepartmentStatus.Active;
}

public sealed record DepartmentResult
{
    public required DepartmentOutcome Outcome { get; init; }

    public DepartmentDto? Department { get; init; }

    public bool Succeeded => Outcome == DepartmentOutcome.Succeeded;

    public static DepartmentResult Failed(DepartmentOutcome outcome) => new() { Outcome = outcome };

    public static DepartmentResult Ok(DepartmentDto department) =>
        new() { Outcome = DepartmentOutcome.Succeeded, Department = department };
}

public sealed record DepartmentDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Code { get; init; }

    public string? Description { get; init; }

    public Guid? HeadEmployeeId { get; init; }

    // Resolved from the employee record so a rename cannot leave a stale label.
    public string? HeadName { get; init; }

    public string? HeadInitials { get; init; }

    public Guid? BusinessUnitId { get; init; }

    // Resolved rather than stored, so renaming a unit cannot leave a stale label here.
    public string? BusinessUnitName { get; init; }

    public decimal? BudgetMonthly { get; init; }

    public required DepartmentStatus Status { get; init; }

    // Counted, never stored — a stored total goes wrong the moment someone transfers.
    public required int EmployeeCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}
