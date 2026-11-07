using Motee.Domain.Organisation;

namespace Motee.Application.Organisation;

// The establishment: the posts a company has, as distinct from the people in
// them.
//
// Employees link to a post explicitly, through a position id. Matching on job
// title would be the obvious shortcut and the wrong one — it is free text, so
// "Backend Engineer" and "Backend engineer" would be two posts, and renaming
// a post would empty it. An employee with no position set simply counts
// towards none, which is visible and true rather than guessed.
public interface IPositionService
{
    Task<IReadOnlyList<PositionDto>> ListAsync(
        PositionQuery query,
        CancellationToken cancellationToken = default);

    Task<PositionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // The figures across the top: how much establishment exists, how much of
    // it is filled, and how many people are in no post at all.
    Task<PositionSummaryDto> SummaryAsync(CancellationToken cancellationToken = default);

    Task<PositionResult> CreateAsync(
        PositionRequest request,
        CancellationToken cancellationToken = default);

    Task<PositionResult> UpdateAsync(
        Guid id,
        PositionRequest request,
        CancellationToken cancellationToken = default);

    // Put somebody in a post, or take them out of one by passing null.
    Task<PositionResult> AssignAsync(
        Guid id,
        Guid employeeId,
        CancellationToken cancellationToken = default);

    Task<PositionResult> UnassignAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default);

    Task<PositionResult> RetireAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PositionResult> RestoreAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PositionOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record PositionDto
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public Guid? DepartmentId { get; init; }

    public string? DepartmentName { get; init; }

    public string? Grade { get; init; }

    public string? Description { get; init; }

    // How many of this post exist.
    public required int Headcount { get; init; }

    // How many people are in it. Derived, because it is what the status
    // depends on and a stored copy would say "vacant" over a filled post.
    public required int Assigned { get; init; }

    public required int OpeningsLeft { get; init; }

    // Live vacancies recruiting into this post.
    public required int Recruiting { get; init; }

    public required PositionStatus Status { get; init; }

    public required bool IsActive { get; init; }

    public required IReadOnlyList<PositionHolderDto> Holders { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record PositionHolderDto
{
    public required Guid EmployeeId { get; init; }

    public required string Name { get; init; }

    public string? JobTitle { get; init; }
}

public sealed record PositionQuery
{
    public Guid? DepartmentId { get; init; }

    public PositionStatus? Status { get; init; }

    public bool IncludeInactive { get; init; } = true;

    public string? Search { get; init; }
}

public sealed record PositionSummaryDto
{
    public required int Positions { get; init; }

    // The establishment: the sum of every active post's complement.
    public required int Establishment { get; init; }

    public required int Filled { get; init; }

    public required int OpeningsLeft { get; init; }

    public required int Recruiting { get; init; }

    public required int Vacant { get; init; }

    public required int Retired { get; init; }

    // People in no post at all. Reported rather than hidden: it is the
    // measure of how complete the establishment actually is, and a company
    // that has never filled this in should see that plainly.
    public required int UnassignedEmployees { get; init; }
}

public sealed record PositionRequest
{
    public required string Title { get; init; }

    public Guid? DepartmentId { get; init; }

    public string? Grade { get; init; }

    public string? Description { get; init; }

    public int Headcount { get; init; } = 1;
}

public enum PositionOutcome
{
    Succeeded,
    NotFound,
    DepartmentNotFound,
    EmployeeNotFound,
    Refused,
}

public sealed record PositionResult
{
    public required PositionOutcome Outcome { get; init; }

    public PositionDto? Position { get; init; }

    public PositionRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == PositionOutcome.Succeeded;

    public static PositionResult Failed(PositionOutcome outcome) => new() { Outcome = outcome };

    public static PositionResult Refused(PositionRefusal refusal) =>
        new() { Outcome = PositionOutcome.Refused, Refusal = refusal };

    public static PositionResult Ok(PositionDto position) =>
        new() { Outcome = PositionOutcome.Succeeded, Position = position };

    // Taking somebody out of a post they were never in succeeds and has no
    // post to describe — the outcome somebody asked for either way.
    public static PositionResult Done() => new() { Outcome = PositionOutcome.Succeeded };
}
