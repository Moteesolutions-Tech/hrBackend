namespace Motee.Domain.Auth;

public enum RefreshOutcome
{
    Valid,
    Expired,

    // Revoked deliberately — a sign-out. Refuse and move on.
    Revoked,

    // Revoked by rotation and presented again, so two parties hold the same value.
    // Treated as a leak: every live session for the user is ended.
    Reused,
}

public static class RefreshTokenPolicy
{
    // Long enough that a user is not signed out mid-week, short enough that a
    // stolen token stops working. Rotation on every use limits the window further.
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public static DateTimeOffset ExpiresAt(DateTimeOffset issuedAt) => issuedAt.Add(Lifetime);

    public static RefreshOutcome Evaluate(RefreshToken token, DateTimeOffset now)
    {
        // Checked before expiry: a leaked token presented late is still a leak.
        if (token.RevokedAt.HasValue)
        {
            return token.ReplacedByTokenId.HasValue ? RefreshOutcome.Reused : RefreshOutcome.Revoked;
        }

        return token.ExpiresAt <= now ? RefreshOutcome.Expired : RefreshOutcome.Valid;
    }
}
