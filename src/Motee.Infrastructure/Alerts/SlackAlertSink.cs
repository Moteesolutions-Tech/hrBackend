using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Motee.Application.Alerts;

namespace Motee.Infrastructure.Alerts;

// Posts alerts to a Slack incoming webhook.
//
// No fallback URL. wallet-service defaults to a placeholder when unconfigured, which
// means every unhandled exception on a misconfigured deploy becomes an outbound request
// to a webhook that does not exist — and the catch around it hides that too. Here a
// missing URL means alerting is off, said once at startup.
internal sealed class SlackAlertSink(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
    AlertThrottle throttle,
    ILogger<SlackAlertSink> logger) : IAlertSink
{
    public const string HttpClientName = "slack-alerts";

    private readonly string? _webhookUrl = Trimmed(configuration["Alerts:Slack:WebhookUrl"]);

    // Which deployment this came from. Without it a staging exception and a production
    // one are indistinguishable in a shared channel, and somebody wastes an afternoon.
    //
    // Taken from ASPNETCORE_ENVIRONMENT, which every deployment already sets — a second
    // variable saying the same thing is one more place for them to disagree, and the one
    // that disagrees is always the one nobody updated. Alerts:Environment overrides it
    // for the case where two deployments share an environment name but not a channel.
    private readonly string _environment =
        Trimmed(configuration["Alerts:Environment"]) ?? hostEnvironment.EnvironmentName;

    public bool Enabled => _webhookUrl is not null;

    public async Task RaiseAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        if (_webhookUrl is null)
        {
            return;
        }

        (bool send, int suppressed) = throttle.Check(alert.Fingerprint);

        if (!send)
        {
            return;
        }

        try
        {
            using HttpClient client = httpClientFactory.CreateClient(HttpClientName);

            HttpResponseMessage response = await client.PostAsJsonAsync(
                _webhookUrl, Payload(alert, suppressed), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Logged at warning, not error. A webhook that has been revoked is worth
                // knowing about, but raising it as an error risks a loop: the alert sink
                // failing would itself be something to alert on.
                logger.LogWarning(
                    "Slack alert rejected with {Status} for {Fingerprint}.",
                    (int)response.StatusCode,
                    alert.Fingerprint);
            }
        }
        catch (Exception exception)
        {
            // Swallowed deliberately. This is called from an exception handler and from
            // background jobs; letting it throw would turn a handled failure into an
            // unhandled one, and a monitoring tool that can take the service down with it
            // is worse than no monitoring.
            logger.LogWarning(
                exception, "Could not send Slack alert for {Fingerprint}.", alert.Fingerprint);
        }
    }

    // Slack's block kit. A header to scan, the detail in a code block, and the facts as
    // fields — which is what makes a channel readable at a glance rather than a wall.
    private object Payload(Alert alert, int suppressed)
    {
        string marker = alert.Severity == AlertSeverity.Critical ? "🔴" : "⚠️";

        List<object> blocks =
        [
            new
            {
                type = "header",
                text = new { type = "plain_text", text = $"{marker} {Truncate(alert.Title, 140)}" },
            },
            new
            {
                type = "section",
                fields = Fields(alert, suppressed),
            },
            new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",

                    // Fenced so a stack trace keeps its shape, and capped because Slack
                    // rejects a block over 3,000 characters outright — which would turn a
                    // long failure into no alert at all.
                    text = $"```{Truncate(alert.Detail, 2600)}```",
                },
            },
        ];

        return new { text = $"{marker} {Truncate(alert.Title, 140)}", blocks };
    }

    private List<object> Fields(Alert alert, int suppressed)
    {
        List<object> fields =
        [
            Field("Environment", _environment),
        ];

        foreach ((string name, string? value) in alert.Facts)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                fields.Add(Field(name, value));
            }
        }

        if (suppressed > 0)
        {
            // "This happened 1,400 times" is a different problem from "twice", and a
            // throttle that hid the difference would be lying by omission.
            fields.Add(Field("Also suppressed", $"{suppressed} since the last alert"));
        }

        // Slack renders at most ten fields in a section and drops the rest silently.
        return [.. fields.Take(10)];
    }

    private static object Field(string name, string value) =>
        new { type = "mrkdwn", text = $"*{name}*\n{Truncate(value, 200)}" };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
