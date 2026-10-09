using Motee.Application.Common;
using Motee.Domain.Platform;

namespace Motee.Application.Platform;

// Motee's own view of its customers. Everything here crosses tenants by design, which is
// why it is a separate service behind a separate authorization axis rather than a wider
// permission on an existing one.
public interface IPlatformService
{
    Task<PagedResult<PlatformTenantDto>> ListTenantsAsync(
        PagedQuery query,
        CancellationToken cancellationToken = default);

    Task<PlatformTenantDto?> GetTenantAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlatformStaffDto>> ListStaffAsync(CancellationToken cancellationToken = default);

    // Promotes an existing account, rather than creating one. Deliberate: a platform
    // operator still needs an ordinary verified account with a password they set
    // themselves, and an endpoint that minted credentials would be a way to create a
    // cross-tenant login in one call.
    Task<PlatformStaffResult> GrantAsync(
        string email,
        PlatformRole role,
        CancellationToken cancellationToken = default);

    Task<PlatformStaffOutcome> RevokeAsync(Guid userId, CancellationToken cancellationToken = default);
}

public enum PlatformStaffOutcome
{
    Succeeded,
    NotFound,

    // The account belongs to a tenant. Promoting them would leave a person who is both
    // somebody's HR admin and a Motee operator, and the token guard refuses to represent
    // that — so it is refused here, where the reason can be explained.
    BelongsToTenant,

    // Refusing to remove the last administrator, which would leave nobody able to grant
    // it back without database access.
    LastAdministrator,
}

public sealed record PlatformStaffResult
{
    public required PlatformStaffOutcome Outcome { get; init; }

    public PlatformStaffDto? Staff { get; init; }

    public bool Succeeded => Outcome == PlatformStaffOutcome.Succeeded;

    public static PlatformStaffResult Failed(PlatformStaffOutcome outcome) => new() { Outcome = outcome };

    public static PlatformStaffResult Ok(PlatformStaffDto staff) =>
        new() { Outcome = PlatformStaffOutcome.Succeeded, Staff = staff };
}

public sealed record PlatformTenantDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public required string Plan { get; init; }

    public required string Status { get; init; }

    public required string CountryCode { get; init; }

    public string? BillingEmail { get; init; }

    // How much the company is actually using, which is the question support is usually
    // being asked. Counted across the tenant filter, since platform staff sit outside it.
    public required int Users { get; init; }

    public required int Employees { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? TrialEndsAt { get; init; }

    public DateTimeOffset? OnboardingCompletedAt { get; init; }
}

public sealed record PlatformStaffDto
{
    public required Guid UserId { get; init; }

    public required string Email { get; init; }

    public required string Name { get; init; }

    public required PlatformRole Role { get; init; }
}
