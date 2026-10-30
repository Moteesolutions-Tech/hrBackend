using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Motee.Application.Alerts;
using Motee.Infrastructure.Alerts;

namespace Motee.Tests.Alerts;

// Raising an alert must not make a request wait.
//
// It used to await an HTTP POST to Slack from inside the exception handler, so a
// slow webhook added up to five seconds to every unhandled error — worst during
// an incident, when errors are frequent and Slack is the thing being leaned on.
public class QueuedAlertSinkTests
{
    private static Alert Alert(string fingerprint = "test|one") => new()
    {
        Severity = AlertSeverity.Error,
        Title = "Something broke",
        Detail = "A thing went wrong.",
        Fingerprint = fingerprint,
    };

    private static QueuedAlertSink SinkOver(Channel<Alert> channel) =>
        new(channel, NullLogger<QueuedAlertSink>.Instance);

    [Fact]
    public async Task RaisingCompletesWithoutWaitingForAnybody()
    {
        Channel<Alert> channel = Channel.CreateBounded<Alert>(10);

        Task raised = SinkOver(channel).RaiseAsync(Alert());

        // Already finished: nothing was awaited on the way through. If this ever
        // becomes false, an alert has been put back in the request's path.
        Assert.True(raised.IsCompletedSuccessfully);

        Assert.True(channel.Reader.TryRead(out Alert? queued));
        Assert.Equal("test|one", queued!.Fingerprint);
    }

    // A queue that grows without limit turns a burst of failures into a memory
    // problem on top of whatever was already wrong.
    [Fact]
    public async Task AFullQueueDropsRatherThanBlocking()
    {
        Channel<Alert> channel = Channel.CreateBounded<Alert>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });

        QueuedAlertSink sink = SinkOver(channel);

        for (int i = 0; i < 10; i++)
        {
            Task raised = sink.RaiseAsync(Alert($"test|{i}"));

            Assert.True(raised.IsCompletedSuccessfully);
        }

        // Two kept, and the newest ones — an old alert matters less than the one
        // describing what is happening now.
        Assert.True(channel.Reader.TryRead(out Alert? first));
        Assert.True(channel.Reader.TryRead(out Alert? second));
        Assert.False(channel.Reader.TryRead(out _));

        Assert.Equal("test|8", first!.Fingerprint);
        Assert.Equal("test|9", second!.Fingerprint);
    }

    // Registered only when a webhook is configured, so it never has to answer
    // "am I on" by asking something that needs an HTTP client.
    [Fact]
    public void ItReportsItselfEnabled() =>
        Assert.True(SinkOver(Channel.CreateBounded<Alert>(1)).Enabled);
}
