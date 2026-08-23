namespace Motee.Domain.Auth;

public enum OtpAttemptOutcome
{
    Verified,
    IncorrectCode,
    Expired,
    LockedOut,
    AlreadyUsed,
}

public sealed record OtpChallenge
{
    public required DateTimeOffset IssuedAt { get; init; }

    public required DateTimeOffset LastSentAt { get; init; }

    public required int FailedAttempts { get; init; }

    public required DateTimeOffset? ConsumedAt { get; init; }
}
