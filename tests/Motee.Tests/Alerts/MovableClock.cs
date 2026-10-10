namespace Motee.Tests.Alerts;

// A clock a test can move.
//
// Hand-rolled rather than pulling in Microsoft.Extensions.TimeProvider.Testing: the whole
// surface needed is "what time is it" and "move forward", and a package reference for two
// methods is a dependency to keep current for no gain.
//
// Anything time-dependent has to be testable without waiting. A throttle with a ten
// minute window, verified by sleeping, is a test nobody runs twice.
internal sealed class MovableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
