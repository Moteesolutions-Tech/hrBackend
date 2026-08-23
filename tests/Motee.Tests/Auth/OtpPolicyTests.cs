using Motee.Domain.Auth;

namespace Motee.Tests.Auth;

public class OtpPolicyTests
{
    private static readonly DateTimeOffset Issued = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static OtpChallenge Fresh() => new()
    {
        IssuedAt = Issued,
        LastSentAt = Issued,
        FailedAttempts = 0,
        ConsumedAt = null,
    };

    [Fact]
    public void VerifiesAMatchingCodeWithinTheWindow()
    {
        OtpAttemptOutcome outcome = OtpPolicy.Evaluate(Fresh(), codeMatched: true, Issued.AddMinutes(1));

        Assert.Equal(OtpAttemptOutcome.Verified, outcome);
    }

    [Fact]
    public void RejectsANonMatchingCode()
    {
        OtpAttemptOutcome outcome = OtpPolicy.Evaluate(Fresh(), codeMatched: false, Issued.AddMinutes(1));

        Assert.Equal(OtpAttemptOutcome.IncorrectCode, outcome);
    }

    [Fact]
    public void AcceptsACodeInTheLastInstantBeforeExpiry()
    {
        DateTimeOffset justInside = Issued.Add(OtpPolicy.Lifetime).AddTicks(-1);

        Assert.Equal(
            OtpAttemptOutcome.Verified,
            OtpPolicy.Evaluate(Fresh(), codeMatched: true, justInside));
    }

    [Fact]
    public void ExpiresExactlyAtTheLifetimeBoundary()
    {
        Assert.Equal(
            OtpAttemptOutcome.Expired,
            OtpPolicy.Evaluate(Fresh(), codeMatched: true, Issued.Add(OtpPolicy.Lifetime)));
    }

    // A correct code must not rescue an exhausted challenge, or the attempt limit
    // is no limit at all.
    [Fact]
    public void LocksOutOnceAttemptsAreExhaustedEvenWhenTheCodeIsCorrect()
    {
        OtpChallenge challenge = Fresh() with { FailedAttempts = OtpPolicy.MaxAttempts };

        Assert.Equal(
            OtpAttemptOutcome.LockedOut,
            OtpPolicy.Evaluate(challenge, codeMatched: true, Issued.AddMinutes(1)));
    }

    [Fact]
    public void AllowsTheFinalPermittedAttempt()
    {
        OtpChallenge challenge = Fresh() with { FailedAttempts = OtpPolicy.MaxAttempts - 1 };

        Assert.Equal(
            OtpAttemptOutcome.Verified,
            OtpPolicy.Evaluate(challenge, codeMatched: true, Issued.AddMinutes(1)));
    }

    [Fact]
    public void RefusesToReuseAConsumedChallenge()
    {
        OtpChallenge challenge = Fresh() with { ConsumedAt = Issued.AddMinutes(1) };

        Assert.Equal(
            OtpAttemptOutcome.AlreadyUsed,
            OtpPolicy.Evaluate(challenge, codeMatched: true, Issued.AddMinutes(2)));
    }

    [Fact]
    public void ConsumptionOutranksExpiryAndLockout()
    {
        OtpChallenge challenge = Fresh() with
        {
            ConsumedAt = Issued.AddMinutes(1),
            FailedAttempts = OtpPolicy.MaxAttempts,
        };

        Assert.Equal(
            OtpAttemptOutcome.AlreadyUsed,
            OtpPolicy.Evaluate(challenge, codeMatched: true, Issued.AddHours(1)));
    }

    [Fact]
    public void RecordingAFailureIncrementsTheCount()
    {
        Assert.Equal(1, OtpPolicy.RecordFailure(Fresh()).FailedAttempts);
    }

    [Fact]
    public void CannotResendDuringTheCooldown()
    {
        Assert.False(OtpPolicy.CanResend(Fresh(), Issued));
        Assert.False(OtpPolicy.CanResend(Fresh(), Issued.Add(OtpPolicy.ResendCooldown).AddTicks(-1)));
    }

    [Fact]
    public void CanResendOnceTheCooldownElapses()
    {
        Assert.True(OtpPolicy.CanResend(Fresh(), Issued.Add(OtpPolicy.ResendCooldown)));
    }

    [Fact]
    public void ReportsRemainingCooldownForTheRetryAfterHeader()
    {
        TimeSpan remaining = OtpPolicy.RetryAfter(Fresh(), Issued.AddSeconds(10));

        Assert.Equal(OtpPolicy.ResendCooldown - TimeSpan.FromSeconds(10), remaining);
    }

    [Fact]
    public void RetryAfterNeverGoesNegative()
    {
        Assert.Equal(TimeSpan.Zero, OtpPolicy.RetryAfter(Fresh(), Issued.AddHours(1)));
    }

    // Clock skew between app instances must not produce a negative wait.
    [Fact]
    public void ToleratesAClockRunningBehindTheIssueTime()
    {
        Assert.False(OtpPolicy.CanResend(Fresh(), Issued.AddSeconds(-30)));
        Assert.True(OtpPolicy.RetryAfter(Fresh(), Issued.AddSeconds(-30)) >= TimeSpan.Zero);
    }
}
