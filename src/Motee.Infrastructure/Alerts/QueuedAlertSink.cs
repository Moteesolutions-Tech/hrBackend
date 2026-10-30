using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Motee.Application.Alerts;

namespace Motee.Infrastructure.Alerts;

// Takes an alert off the request's hands.
//
// Raising one used to await an HTTP POST to Slack from inside the exception
// handler. The client has a five-second timeout, so the damage was bounded — but
// it meant every unhandled error took up to five seconds longer to answer, and a
// Slack outage made error responses slowest exactly when the system was already
// failing.
//
// Deliberately not Hangfire, unlike email and exports. Alerts frequently fire
// because the database is unreachable, and Hangfire's storage is that same
// database: queueing an alert about a Postgres outage into Postgres would lose
// precisely the alerts worth having. An in-memory channel keeps working when
// nothing else does.
//
// The cost of that choice is honest: alerts still queued at shutdown are lost.
// For a monitoring side-channel that is the right trade — the alternative is a
// durable store that shares a failure mode with the thing being monitored.
internal sealed class QueuedAlertSink(
    Channel<Alert> channel,
    ILogger<QueuedAlertSink> logger) : IAlertSink
{
    // Always, because this is only registered when a webhook is configured —
    // without one the no-op sink is used instead. Asking the destination would
    // mean depending on it just to read a flag, and a queue that needs an HTTP
    // client to answer "are you on" cannot be tested without one.
    public bool Enabled => true;

    public Task RaiseAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        // Never blocks and never throws. A full queue means something is wrong
        // far upstream of here, and the one thing an alert sink must not do is
        // become the reason a request hangs.
        if (!channel.Writer.TryWrite(alert))
        {
            logger.LogWarning(
                "Alert queue is full; dropped {Fingerprint}.", alert.Fingerprint);
        }

        return Task.CompletedTask;
    }
}

// Drains the queue, one alert at a time.
//
// Serial on purpose: a burst of the same failure is collapsed by the throttle
// inside the destination, and sending them in parallel would spend the burst on
// Slack's rate limit instead.
internal sealed class AlertDispatcher(
    Channel<Alert> channel,
    SlackAlertSink destination,
    ILogger<AlertDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (Alert alert in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                // Not passed the stopping token. An alert raised seconds before
                // shutdown is the most interesting kind, and cancelling the send
                // because the process is going down would throw away the message
                // explaining why.
                await destination.RaiseAsync(alert, CancellationToken.None);
            }
            catch (Exception exception)
            {
                // The destination swallows its own failures, so reaching here
                // means something unexpected. Logged rather than rethrown: a
                // dispatcher that dies takes every later alert with it.
                logger.LogWarning(
                    exception, "Could not dispatch alert {Fingerprint}.", alert.Fingerprint);
            }
        }
    }
}
