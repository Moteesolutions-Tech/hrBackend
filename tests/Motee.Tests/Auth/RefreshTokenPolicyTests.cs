using Motee.Domain.Auth;

namespace Motee.Tests.Auth;

public class RefreshTokenPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static RefreshToken Token(
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? revokedAt = null,
        Guid? replacedBy = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        TokenHash = new string('a', 64),
        CreatedAt = Now.AddDays(-1),
        ExpiresAt = expiresAt ?? Now.AddDays(13),
        RevokedAt = revokedAt,
        ReplacedByTokenId = replacedBy,
    };

    [Fact]
    public void AFreshTokenIsValid()
    {
        Assert.Equal(RefreshOutcome.Valid, RefreshTokenPolicy.Evaluate(Token(), Now));
    }

    [Fact]
    public void ATokenPastItsExpiryIsExpired()
    {
        Assert.Equal(
            RefreshOutcome.Expired,
            RefreshTokenPolicy.Evaluate(Token(expiresAt: Now.AddSeconds(-1)), Now));
    }

    [Fact]
    public void ExpiryIsInclusiveOfTheBoundary()
    {
        Assert.Equal(
            RefreshOutcome.Expired,
            RefreshTokenPolicy.Evaluate(Token(expiresAt: Now), Now));
    }

    // Signing out revokes without a successor. Presenting it again is simply refused.
    [Fact]
    public void ARevokedTokenWithNoSuccessorIsRevoked()
    {
        Assert.Equal(
            RefreshOutcome.Revoked,
            RefreshTokenPolicy.Evaluate(Token(revokedAt: Now.AddMinutes(-5)), Now));
    }

    // Rotation revokes and records a successor. Seeing that token again means two
    // parties hold it — the legitimate client and whoever copied it — so it is
    // treated as a leak rather than a refusal.
    [Fact]
    public void ARevokedTokenWithASuccessorIsAReuse()
    {
        Assert.Equal(
            RefreshOutcome.Reused,
            RefreshTokenPolicy.Evaluate(
                Token(revokedAt: Now.AddMinutes(-5), replacedBy: Guid.NewGuid()),
                Now));
    }

    // Reuse outranks expiry: a leaked token presented late is still a leak, and the
    // whole chain must go.
    [Fact]
    public void ReuseIsReportedEvenWhenTheTokenHasAlsoExpired()
    {
        Assert.Equal(
            RefreshOutcome.Reused,
            RefreshTokenPolicy.Evaluate(
                Token(expiresAt: Now.AddDays(-1), revokedAt: Now.AddDays(-2), replacedBy: Guid.NewGuid()),
                Now));
    }

    [Fact]
    public void LifetimeIsLongerThanAnAccessTokenButNotIndefinite()
    {
        Assert.InRange(RefreshTokenPolicy.Lifetime.TotalDays, 1, 90);
    }

    [Fact]
    public void ExpiryIsMeasuredFromIssue()
    {
        Assert.Equal(
            Now.Add(RefreshTokenPolicy.Lifetime),
            RefreshTokenPolicy.ExpiresAt(Now));
    }
}
