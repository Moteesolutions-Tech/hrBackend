using Motee.Infrastructure.Alerts;

namespace Motee.Tests.Alerts;

// The piece that decides whether alerting is useful or ignored.
//
// Without throttling the first alert worth having is also the last one anybody reads: a
// failing dependency produces one exception per request, and a channel that fills with
// the same message gets muted — which is the outcome alerting exists to prevent.
public class AlertThrottleTests
{
    private static (AlertThrottle Throttle, MovableClock Clock) Build()
    {
        MovableClock clock = new(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));

        return (new AlertThrottle(clock), clock);
    }

    [Fact]
    public void TheFirstOccurrenceIsAlwaysSent()
    {
        (AlertThrottle throttle, _) = Build();

        (bool send, int suppressed) = throttle.Check("boom");

        Assert.True(send);
        Assert.Equal(0, suppressed);
    }

    [Fact]
    public void RepeatsWithinTheWindowAreSuppressed()
    {
        (AlertThrottle throttle, _) = Build();

        throttle.Check("boom");

        for (int i = 1; i <= 50; i++)
        {
            (bool send, int suppressed) = throttle.Check("boom");

            Assert.False(send);
            Assert.Equal(i, suppressed);
        }
    }

    // The count is the point. "This happened 1,400 times" is a different problem from
    // "twice", and a throttle that hid the difference would be lying by omission.
    [Fact]
    public void TheNextAlertReportsHowManyWereSuppressed()
    {
        (AlertThrottle throttle, MovableClock clock) = Build();

        throttle.Check("boom");

        for (int i = 0; i < 1399; i++)
        {
            throttle.Check("boom");
        }

        clock.Advance(TimeSpan.FromMinutes(11));

        (bool send, int suppressed) = throttle.Check("boom");

        Assert.True(send);
        Assert.Equal(1399, suppressed);
    }

    // And the count resets, so the message after that does not keep reporting an old
    // total as though it were new.
    [Fact]
    public void TheSuppressedCountResetsAfterBeingReported()
    {
        (AlertThrottle throttle, MovableClock clock) = Build();

        throttle.Check("boom");
        throttle.Check("boom");

        clock.Advance(TimeSpan.FromMinutes(11));
        throttle.Check("boom");

        clock.Advance(TimeSpan.FromMinutes(11));
        (bool send, int suppressed) = throttle.Check("boom");

        Assert.True(send);
        Assert.Equal(0, suppressed);
    }

    // A problem recurring an hour later is a new problem to report, not one already
    // absorbed.
    [Fact]
    public void TheSameFailureIsReportedAgainOnceTheWindowPasses()
    {
        (AlertThrottle throttle, MovableClock clock) = Build();

        Assert.True(throttle.Check("boom").Send);
        Assert.False(throttle.Check("boom").Send);

        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.True(throttle.Check("boom").Send);
    }

    // Different failures must not throttle each other. One noisy endpoint silencing
    // every other alert would be worse than no throttle at all.
    [Fact]
    public void DistinctFailuresAreThrottledIndependently()
    {
        (AlertThrottle throttle, _) = Build();

        Assert.True(throttle.Check("first").Send);
        Assert.True(throttle.Check("second").Send);

        Assert.False(throttle.Check("first").Send);
        Assert.False(throttle.Check("second").Send);
    }

    // A fingerprint accidentally built from per-request data would grow without limit.
    // The cap means that is a degraded throttle rather than a memory leak.
    [Fact]
    public void TrackingIsBoundedWhenFingerprintsNeverRepeat()
    {
        (AlertThrottle throttle, MovableClock clock) = Build();

        for (int i = 0; i < 600; i++)
        {
            Assert.True(throttle.Check($"unique-{i}").Send);
        }

        // Nothing has expired yet, so pruning cannot reclaim anything — the point is
        // that it still answers rather than throwing or hanging.
        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.True(throttle.Check("unique-0").Send);
    }

    // Two threads hitting the same failure at once must produce one message, not two.
    [Fact]
    public async Task ConcurrentChecksSendExactlyOnce()
    {
        (AlertThrottle throttle, _) = Build();

        int sent = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            if (throttle.Check("boom").Send)
            {
                Interlocked.Increment(ref sent);
            }
        })));

        Assert.Equal(1, sent);
    }
}
