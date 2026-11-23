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

    // With the company's session timeout applied.
    //
    // The security screen's "Session timeout: 1 hour" is an idle timeout, and
    // this is where it becomes one: the token rotates on every use, so an hour
    // from issue is an hour from last activity. Before this the setting was a
    // dropdown that changed nothing and everybody had fourteen days.
    //
    // Never longer than the ceiling above. A company is free to ask for a
    // shorter session than we allow and not a longer one.
    public static DateTimeOffset ExpiresAt(DateTimeOffset issuedAt, int? sessionMinutes)
    {
        if (sessionMinutes is not int minutes || minutes <= 0)
        {
            return ExpiresAt(issuedAt);
        }

        TimeSpan asked = TimeSpan.FromMinutes(minutes);

        return issuedAt.Add(asked < Lifetime ? asked : Lifetime);
    }

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
