namespace Motee.Domain.Auth;


public class OtpChallengeRecord
{
    public Guid Id { get; set; }

    public required Guid UserId { get; set; }

    public required OtpPurpose Purpose { get; set; }

    public required DateTimeOffset IssuedAt { get; set; }

    public required DateTimeOffset LastSentAt { get; set; }

    public int FailedAttempts { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public OtpChallenge ToChallenge() => new()
    {
        IssuedAt = IssuedAt,
        LastSentAt = LastSentAt,
        FailedAttempts = FailedAttempts,
        ConsumedAt = ConsumedAt,
    };

    public void Apply(OtpChallenge challenge)
    {
        IssuedAt = challenge.IssuedAt;
        LastSentAt = challenge.LastSentAt;
        FailedAttempts = challenge.FailedAttempts;
        ConsumedAt = challenge.ConsumedAt;
    }
}
