using Motee.Domain.Organisation;

namespace Motee.Application.Organisation;

// The company's physical sites. A different axis from departments and business units:
// exactly one per employee, and it cuts across both.
public interface IBranchService
{
    Task<IReadOnlyList<BranchDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<BranchDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<BranchResult> CreateAsync(
        BranchRequest request,
        CancellationToken cancellationToken = default);

    Task<BranchResult> UpdateAsync(
        Guid id,
        BranchRequest request,
        CancellationToken cancellationToken = default);

    // Refused while anybody is still posted there. Deleting would leave those people with
    // a branch id pointing at nothing, which reads as "no branch" and quietly drops them
    // out of every site-scoped list — including a fire register.
    Task<BranchOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public enum BranchOutcome
{
    Succeeded,
    NotFound,

    // Another site already uses that code. It appears on badges and in pickers, where two
    // identical codes cannot be chosen between.
    DuplicateCode,

    // People are still posted here.
    InUse,

    // The named manager is not an employee of this company.
    UnknownManager,
}

public sealed record BranchRequest
{
    public required string Name { get; init; }

    public required string Code { get; init; }

    public BranchKind Kind { get; init; } = BranchKind.Branch;

    public BranchStatus Status { get; init; } = BranchStatus.Active;

    public IReadOnlyList<string> AddressLines { get; init; } = [];

    public string? City { get; init; }

    public string? Region { get; init; }

    public string? PostalCode { get; init; }

    public string? Country { get; init; }

    public string? TimeZone { get; init; }

    public string? Phone { get; init; }

    public string? Email { get; init; }

    public Guid? ManagerEmployeeId { get; init; }

    public int? HeadcountTarget { get; init; }

    public DateOnly? OpenedAt { get; init; }
}

public sealed record BranchResult
{
    public required BranchOutcome Outcome { get; init; }

    public BranchDto? Branch { get; init; }

    public bool Succeeded => Outcome == BranchOutcome.Succeeded;

    public static BranchResult Failed(BranchOutcome outcome) => new() { Outcome = outcome };

    public static BranchResult Ok(BranchDto branch) =>
        new() { Outcome = BranchOutcome.Succeeded, Branch = branch };
}

public sealed record BranchDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Code { get; init; }

    public required BranchKind Kind { get; init; }

    public required BranchStatus Status { get; init; }

    public required IReadOnlyList<string> AddressLines { get; init; }

    public string? City { get; init; }

    public string? Region { get; init; }

    public string? PostalCode { get; init; }

    public string? Country { get; init; }

    public string? TimeZone { get; init; }

    public string? Phone { get; init; }

    public string? Email { get; init; }

    public Guid? ManagerEmployeeId { get; init; }

    public string? ManagerName { get; init; }

    public int? HeadcountTarget { get; init; }

    public DateOnly? OpenedAt { get; init; }

    // Active staff posted here. The number the screen leads with, and the one that
    // decides whether the site can be deleted.
    public required int EmployeeCount { get; init; }

    // Distinct departments with at least one person on site. Not a department count for
    // the company — a site is where departments overlap, and that overlap is the thing
    // worth seeing.
    public required int DepartmentCount { get; init; }

    // How far under target, or null when no target is set. Computed here rather than in
    // the client so "12 of 20" and "over by 3" come from one place.
    public int? OpenPositions => HeadcountTarget is int target && target > EmployeeCount
        ? target - EmployeeCount
        : null;

    // One line, for tables and cards. Joined here because every client would otherwise
    // join it slightly differently.
    public required string AddressLabel { get; init; }
}
