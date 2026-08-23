namespace Motee.Domain.Auth;

// Governs the rules around a 6-digit code. Generating and comparing the code
// itself is Identity's job; this decides whether an attempt may be accepted.
// Time is always passed in so the rules stay testable and instance clocks agree.
public static class OtpPolicy
{
    // Must stay inside Identity's EmailTokenProvider window. That provider is TOTP
    // over 3-minute timesteps accepting ±2 steps, so a code stops verifying roughly
    // six minutes after issue. A longer policy lifetime here would accept a code
    // Identity then rejects, surfacing as "incorrect" rather than "expired".
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    public const int MaxAttempts = 5;

    public static OtpAttemptOutcome Evaluate(
        OtpChallenge challenge,
        bool codeMatched,
        DateTimeOffset now)
    {
        // Order matters: a consumed or exhausted challenge is refused before the
        // code is considered, so a correct code cannot rescue it.
        if (challenge.ConsumedAt is not null)
        {
            return OtpAttemptOutcome.AlreadyUsed;
        }

        if (now - challenge.IssuedAt >= Lifetime)
        {
            return OtpAttemptOutcome.Expired;
        }

        if (challenge.FailedAttempts >= MaxAttempts)
        {
            return OtpAttemptOutcome.LockedOut;
        }

        return codeMatched ? OtpAttemptOutcome.Verified : OtpAttemptOutcome.IncorrectCode;
    }

    public static OtpChallenge RecordFailure(OtpChallenge challenge) =>
        challenge with { FailedAttempts = challenge.FailedAttempts + 1 };

    public static OtpChallenge Consume(OtpChallenge challenge, DateTimeOffset now) =>
        challenge with { ConsumedAt = now };

    public static bool CanResend(OtpChallenge challenge, DateTimeOffset now) =>
        now - challenge.LastSentAt >= ResendCooldown;

    public static TimeSpan RetryAfter(OtpChallenge challenge, DateTimeOffset now)
    {
        TimeSpan remaining = ResendCooldown - (now - challenge.LastSentAt);

        // A clock behind the issue time would otherwise yield a negative wait.
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}
