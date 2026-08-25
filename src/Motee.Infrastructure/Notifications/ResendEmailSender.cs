using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Motee.Application.Notifications;

namespace Motee.Infrastructure.Notifications;

internal sealed class ResendEmailSender(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<ResendEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        string senderEmail = Required("Resend:SenderEmail");
        string senderName = configuration["Resend:SenderName"] ?? "Motee";

        // Both parts when there is HTML. Sending text alone loses the branding; sending
        // HTML alone raises the spam score and leaves plain-text clients with nothing.
        object payload = message.HtmlBody is null
            ? new
            {
                from = $"\"{senderName}\" <{senderEmail}>",
                to = message.To,
                subject = message.Subject,
                text = message.TextBody,
            }
            : new
            {
                from = $"\"{senderName}\" <{senderEmail}>",
                to = message.To,
                subject = message.Subject,
                text = message.TextBody,
                html = message.HtmlBody,
            };

        HttpResponseMessage response =
            await httpClient.PostAsJsonAsync("emails", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancellationToken);

            // Sender included. It is configuration rather than anything about the
            // message, so it is the likeliest cause of a rejection and the one field
            // the caller cannot infer - a 403 naming a domain is meaningless without
            // knowing which address was actually used.
            logger.LogError(
                "Resend rejected {Subject} from {Sender} to {Recipient}. Status {Status}: {Error}",
                message.Subject,
                senderEmail,
                message.To,
                (int)response.StatusCode,
                error);

            // A 4xx is the configuration being wrong: a key scoped to another domain, an
            // unverified sender, a malformed address. None of that changes in ten
            // seconds, so retrying only buries the log line under three identical
            // copies. Returning marks the job done; the Error above is the alert.
            //
            // 408 and 429 are the exceptions - both mean "ask again later".
            if (IsPermanent(response.StatusCode))
            {
                logger.LogError(
                    "Not retrying: {Status} is a configuration failure, not a transient one. "
                    + "Nobody received this message.",
                    (int)response.StatusCode);

                return;
            }

            // Thrown so the Hangfire job records a failure and retries.
            throw new InvalidOperationException(
                $"Resend returned {(int)response.StatusCode} sending to {message.To}.");
        }

        logger.LogInformation("Sent {Subject} to {Recipient} via Resend.", message.Subject, message.To);
    }

    private static bool IsPermanent(HttpStatusCode status) =>
        (int)status is >= 400 and < 500
        && status is not HttpStatusCode.TooManyRequests
        && status is not HttpStatusCode.RequestTimeout;

    private string Required(string key) =>
        configuration[key]
        ?? throw new InvalidOperationException($"{key} is not configured; Resend cannot send.");
}
