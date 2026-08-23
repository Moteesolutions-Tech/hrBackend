using Motee.Domain.Authorization;

namespace Motee.Api.Contracts.Auth;

public sealed record VerifyOtpApiRequest
{
    public required Guid UserId { get; init; }

    public required string Code { get; init; }
}

public sealed record ResendOtpApiRequest
{
    public required Guid UserId { get; init; }
}

public sealed record LoginApiRequest
{
    public required string Email { get; init; }

    public required string Password { get; init; }
}

public sealed record RegisterResponse
{
    public required Guid UserId { get; init; }

    public required Guid TenantId { get; init; }

    public required string TenantSlug { get; init; }

    public required string Email { get; init; }

    public required string CountryCode { get; init; }

    public required bool VerificationRequired { get; init; }
}

public sealed record ForgotPasswordApiRequest
{
    public required string Email { get; init; }
}

public sealed record ResetPasswordApiRequest
{
    public required string Email { get; init; }

    public required string Code { get; init; }

    public required string NewPassword { get; init; }
}

public sealed record RefreshApiRequest
{
    public required string RefreshToken { get; init; }
}

public sealed record LoginResponse
{
    public required string AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    public DateTimeOffset? RefreshTokenExpiresAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required Guid UserId { get; init; }

    public Guid? TenantId { get; init; }

    public required bool OnboardingCompleted { get; init; }
}

public sealed record CurrentUserResponse
{
    public required string UserId { get; init; }

    public required string Email { get; init; }

    public string? TenantId { get; init; }

    public string? Role { get; init; }

    public string? EmployeeId { get; init; }

    public required bool IsPlatformStaff { get; init; }

    // False sends the client into the setup wizard.
    public required bool OnboardingCompleted { get; init; }

    public DateTimeOffset? OnboardingCompletedAt { get; init; }

    // The access levels this person holds, by name. Several is normal — a Line
    // Manager who is also a Recruiter — and these are what the conflicts panel names
    // when two of them disagree about an action.
    public required IReadOnlyList<string> AccessLevels { get; init; }

    // The account owner bypasses every permission check. The UI should not hide
    // anything from them on the strength of an empty matrix.
    public required bool IsOwner { get; init; }

    // The permission matrix the frontend's useCan hook reads: the union of what the
    // held levels permit, resolved now rather than carried in the token, so an edit
    // to a level shows up on the next request.
    public required IReadOnlyList<ModulePermission> Permissions { get; init; }

    // How far those permissions reach — the narrowest of the held levels. The matrix
    // says which modules open; this says whose records come back.
    public required DataScope Scope { get; init; }
}

