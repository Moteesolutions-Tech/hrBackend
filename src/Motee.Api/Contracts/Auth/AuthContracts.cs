using Motee.Domain.Authorization;

namespace Motee.Api.Contracts.Auth;

// Keyed on email rather than a user id, because registration can no longer hand one
// back: it answers identically whether or not the address is known, and a user id only
// exists in one of those cases.
//
// The id was acting as a second secret, so the six digits now stand alone - which the
// OtpPolicy budget of five attempts inside five minutes is what makes sound.
public sealed record VerifyOtpApiRequest
{
    public required string Email { get; init; }

    public required string Code { get; init; }
}

public sealed record ResendOtpApiRequest
{
    public required string Email { get; init; }
}

public sealed record LoginApiRequest
{
    public required string Email { get; init; }

    public required string Password { get; init; }
}

// Everything here exists in both outcomes - a new account, and an address that already
// had one. UserId, TenantId and TenantSlug used to be here and had to go: they only
// exist when something was created, so returning them announced which case the caller
// had hit. They arrive from verify-otp instead, once the code proves the mailbox.
//
// Nothing may be added here that is knowable only in one case.
public sealed record RegisterResponse
{
    public required string Email { get; init; }

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

