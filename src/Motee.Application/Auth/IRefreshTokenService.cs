using Motee.Domain.Auth;

namespace Motee.Application.Auth;

public interface IRefreshTokenService
{
    Task<IssuedRefreshToken> IssueAsync(
        Guid userId,
        string? createdByIp,
        CancellationToken cancellationToken = default);

    Task<RefreshResult> RotateAsync(
        string rawToken,
        string? usedByIp,
        CancellationToken cancellationToken = default);

    Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed record IssuedRefreshToken
{
    // The only time the raw value exists. Only its hash is stored.
    public required string Value { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed record RefreshResult
{
    public required RefreshOutcome Outcome { get; init; }

    public AccessToken? AccessToken { get; init; }

    public IssuedRefreshToken? RefreshToken { get; init; }

    public Guid UserId { get; init; }

    public Guid? TenantId { get; init; }

    public bool OnboardingCompleted { get; init; }

    public bool Succeeded => Outcome == RefreshOutcome.Valid;

    public static RefreshResult Failed(RefreshOutcome outcome) => new() { Outcome = outcome };
}
