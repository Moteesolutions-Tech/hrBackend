namespace Motee.Application.Auth;

public interface ILoginService
{
    Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
}

public sealed record LoginRequest
{
    public required string Email { get; init; }

    public required string Password { get; init; }

    // Recorded against the refresh token so a session can be traced to an origin.
    public string? IpAddress { get; init; }
}

public enum LoginOutcome
{
    Succeeded,
    InvalidCredentials,
    EmailNotConfirmed,
    LockedOut,
}

public sealed record LoginResult
{
    public required LoginOutcome Outcome { get; init; }

    public AccessToken? AccessToken { get; init; }

    public IssuedRefreshToken? RefreshToken { get; init; }

    public Guid UserId { get; init; }

    public Guid? TenantId { get; init; }

    public bool OnboardingCompleted { get; init; }

    public static LoginResult Failed(LoginOutcome outcome) => new() { Outcome = outcome };
}
