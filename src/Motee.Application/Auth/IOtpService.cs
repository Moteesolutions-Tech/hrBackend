using Motee.Domain.Auth;

namespace Motee.Application.Auth;

public interface IOtpService
{
    Task<OtpIssueResult> IssueAsync(
        Guid userId,
        OtpPurpose purpose,
        CancellationToken cancellationToken = default);

    Task<OtpAttemptOutcome> VerifyAsync(
        Guid userId,
        OtpPurpose purpose,
        string code,
        CancellationToken cancellationToken = default);
}

public sealed record OtpIssueResult
{
    public required bool Sent { get; init; }

    // Populated when a resend was refused during the cooldown.
    public TimeSpan RetryAfter { get; init; }
}
