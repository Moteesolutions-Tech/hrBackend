using Motee.Application.Alerts;

namespace Motee.Infrastructure.Alerts;

// Alerting off. Registered when no webhook is configured, which is every test run and
// any local build nobody has pointed at a channel.
//
// A real type rather than a null check at each call site: a caller that had to ask
// whether alerting exists is a caller that can forget to.
internal sealed class NoopAlertSink : IAlertSink
{
    public bool Enabled => false;

    public Task RaiseAsync(Alert alert, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
