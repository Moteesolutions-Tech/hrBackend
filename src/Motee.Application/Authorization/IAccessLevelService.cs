using Motee.Domain.Authorization;

namespace Motee.Application.Authorization;

public interface IAccessLevelService
{
    Task<IReadOnlyList<AccessLevelDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<AccessLevelDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AccessLevelResult> CreateAsync(
        AccessLevelRequest request,
        CancellationToken cancellationToken = default);

    Task<AccessLevelResult> UpdateAsync(
        Guid id,
        AccessLevelRequest request,
        CancellationToken cancellationToken = default);

    Task<AccessLevelResult> ChangeStatusAsync(
        Guid id,
        AccessLevelStatus status,
        CancellationToken cancellationToken = default);


    Task<AccessLevelResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AccessLevelResult> AssignAsync(
        Guid userId,
        Guid accessLevelId,
        CancellationToken cancellationToken = default);

    Task<AccessLevelResult> WithdrawAsync(
        Guid userId,
        Guid accessLevelId,
        CancellationToken cancellationToken = default);
}

public enum AccessLevelOutcome
{
    Succeeded,
    NotFound,
    DuplicateName,

    // A module id the catalogue does not contain. Usually a stale client, and storing
    // it would leave a permission nothing can ever evaluate.
    UnknownModule,

    // Draft is unfinished and inactive is withdrawn. Neither can be handed to anyone.
    NotAssignable,

    // People hold it. Deactivate instead.
    InUse,

    UserNotFound,
    AlreadyHeld,
    NotHeld,
}

public sealed record AccessLevelRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required DataScope Scope { get; init; }

    public required IReadOnlyList<ModulePermission> Permissions { get; init; }

    // Start a new level as a copy of an existing one. Turns a niche role — a health
    // and safety officer, a facilities manager — into a two-minute edit of the
    // closest match, which is why seven shipped levels are enough.
    public Guid? CopyFromId { get; init; }
}

public sealed record AccessLevelResult
{
    public required AccessLevelOutcome Outcome { get; init; }

    public AccessLevelDto? AccessLevel { get; init; }

    public bool Succeeded => Outcome == AccessLevelOutcome.Succeeded;

    public static AccessLevelResult Failed(AccessLevelOutcome outcome) => new() { Outcome = outcome };

    public static AccessLevelResult Ok(AccessLevelDto level) =>
        new() { Outcome = AccessLevelOutcome.Succeeded, AccessLevel = level };
}

public sealed record AccessLevelDto
{
    public required Guid Id { get; init; }

    public string? TemplateSlug { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required AccessLevelKind Kind { get; init; }

    public required AccessLevelStatus Status { get; init; }

    public required DataScope Scope { get; init; }

    public required IReadOnlyList<ModulePermission> Permissions { get; init; }

    // How many people hold it, and when it was last used to sign someone in. A level
    // with no holders and no use in a year is the one safe to retire, and assignment
    // counts alone do not show that.
    public required int AssignedCount { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
