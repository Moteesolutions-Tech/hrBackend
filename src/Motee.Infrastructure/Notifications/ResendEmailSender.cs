using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Motee.Application.Auth;

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

        object payload = new
        {
            from = $"\"{senderName}\" <{senderEmail}>",
            to = message.To,
            subject = message.Subject,
            text = message.Body,
        };

        HttpResponseMessage response =
            await httpClient.PostAsJsonAsync("emails", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancellationToken);

            logger.LogError(
                "Resend rejected {Subject} to {Recipient}. Status {Status}: {Error}",
                message.Subject,
                message.To,
                (int)response.StatusCode,
                error);

            // Thrown so the Hangfire job records a failure and retries.
            throw new InvalidOperationException(
                $"Resend returned {(int)response.StatusCode} sending to {message.To}.");
        }

        logger.LogInformation("Sent {Subject} to {Recipient} via Resend.", message.Subject, message.To);
    }

    private string Required(string key) =>
        configuration[key]
        ?? throw new InvalidOperationException($"{key} is not configured; Resend cannot send.");
}
