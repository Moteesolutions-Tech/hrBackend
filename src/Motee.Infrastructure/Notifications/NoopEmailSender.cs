using Microsoft.Extensions.Logging;
using Motee.Application.Auth;

namespace Motee.Infrastructure.Notifications;

// Stand-in until a provider is configured. Deliberately loud: a silent no-op would
// let a deployment look healthy while every verification email is dropped.
internal sealed class NoopEmailSender(ILogger<NoopEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "No email provider configured (Email:Provider). Dropped {Subject} to {Recipient}.",
            message.Subject,
            message.To);

        return Task.CompletedTask;
    }
}
