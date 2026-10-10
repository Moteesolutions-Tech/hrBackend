using System.Collections.Concurrent;

namespace Motee.Infrastructure.Alerts;

// Collapses repeats of the same failure.
//
// Without this, the first alert worth having is also the last one anybody reads. A
// failing dependency produces one exception per request, which on a busy endpoint is
// hundreds a minute — and a channel that fills with the same message gets muted, which
// is the outcome alerting exists to avoid.
//
// A singleton holding state in memory, which is the right scope and also the limitation:
// two instances behind a load balancer each throttle their own traffic, so the ceiling is
// per instance rather than per deployment. That is acceptable for a handful of instances
// and would not be for fifty.
internal sealed class AlertThrottle(TimeProvider timeProvider)
{
    // Long enough that a crash loop produces one message rather than a wall, short
    // enough that a problem recurring an hour later is reported again instead of being
    // silently absorbed.
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    // Bounded so a fingerprint generated per unique input — which would be a bug in
    // whoever built it, but a plausible one — cannot grow without limit.
    private const int MaxTracked = 500;

    private readonly ConcurrentDictionary<string, Occurrence> _seen = new(StringComparer.Ordinal);

    // Whether to send, and how many were suppressed since the last one that went out.
    //
    // The count matters: "this happened 1,400 times" is a different problem from "this
    // happened twice", and a throttle that hid the difference would be lying by omission.
    public (bool Send, int Suppressed) Check(string fingerprint)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        if (_seen.Count >= MaxTracked)
        {
            Prune(now);
        }

        while (true)
        {
            if (!_seen.TryGetValue(fingerprint, out Occurrence? existing))
            {
                if (_seen.TryAdd(fingerprint, new Occurrence(now, 0)))
                {
                    return (true, 0);
                }

                continue;
            }

            if (now - existing.LastSent >= Window)
            {
                // Replaced rather than mutated, and only if nothing else got there
                // first — two threads hitting the same failure at once must produce one
                // message, not two.
                if (_seen.TryUpdate(fingerprint, new Occurrence(now, 0), existing))
                {
                    return (true, existing.Suppressed);
                }

                continue;
            }

            if (_seen.TryUpdate(
                fingerprint,
                new Occurrence(existing.LastSent, existing.Suppressed + 1),
                existing))
            {
                return (false, existing.Suppressed + 1);
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach ((string key, Occurrence occurrence) in _seen)
        {
            if (now - occurrence.LastSent >= Window)
            {
                _seen.TryRemove(key, out _);
            }
        }
    }

    private sealed record Occurrence(DateTimeOffset LastSent, int Suppressed);
}
