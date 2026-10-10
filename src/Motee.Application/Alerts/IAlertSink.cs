namespace Motee.Application.Alerts;

// Where something that went wrong gets reported to a human.
//
// An interface because the destination is a deployment decision, not a product one — a
// Slack webhook here, nothing at all in tests, and whatever comes next without touching
// the places that raise alerts.
//
// Every implementation swallows its own failures. An alert that threw would turn a
// handled 500 into an unhandled one, and a monitoring tool that can take the service
// down with it is worse than no monitoring.
public interface IAlertSink
{
    // False when no destination is configured. Callers can skip assembling a payload
    // nobody will read, and startup can say once that alerting is off rather than
    // leaving somebody to wonder why the channel is quiet.
    bool Enabled { get; }

    Task RaiseAsync(Alert alert, CancellationToken cancellationToken = default);
}

public enum AlertSeverity
{
    // Something a user just hit. They saw an error page; somebody should know why.
    Error,

    // Something nobody saw. A background job that failed, which is worse in one
    // respect: a 500 at least reaches the person who caused it, and this reaches
    // nobody until it is noticed.
    Critical,
}

public sealed record Alert
{
    public required AlertSeverity Severity { get; init; }

    // One line, scannable in a channel: "Unhandled exception on POST employees".
    public required string Title { get; init; }

    public required string Detail { get; init; }

    // Groups repeats so a crash loop collapses into one message with a count rather
    // than ten thousand. Derived from the shape of the failure — exception type plus
    // where it happened — never from anything per-request, or every occurrence would
    // look distinct and the throttle would never engage.
    public required string Fingerprint { get; init; }

    // Named facts, rendered as fields. Deliberately a fixed set assembled by the
    // caller rather than a free dump of context: a Slack channel is not an access-
    // controlled surface, and a request body in one is a data breach with a nice
    // colour scheme.
    public IReadOnlyDictionary<string, string?> Facts { get; init; } =
        new Dictionary<string, string?>();
}
