namespace Motee.Application.Auth;

// Mints a signed-in session for a user whose identity has already been established.
// The caller owns that decision — a verified password, a verified OTP, a rotated
// refresh token — and this only turns it into tokens.
public interface ISessionIssuer
{
    Task<IssuedSession?> IssueAsync(
        Guid userId,
        string? ipAddress,
        CancellationToken cancellationToken = default);
}

public sealed record IssuedSession
{
    public required AccessToken AccessToken { get; init; }

    public required IssuedRefreshToken RefreshToken { get; init; }

    public required Guid UserId { get; init; }

    public Guid? TenantId { get; init; }

    // Lets the client route without a second call to /me, which would otherwise
    // land it on the dashboard first and bounce.
    public required bool OnboardingCompleted { get; init; }
}
