using Microsoft.Extensions.Logging;
using Motee.Application.Common;
using Motee.Application.Notifications;

namespace Motee.Infrastructure.Notifications;

// Stand-in until a provider is configured. Two very different situations reach it, and
// conflating them is how a broken deployment looks fine: on a developer's machine there
// is nothing to deliver to and the message body is the point, while on a server this
// means every verification email is being dropped.
internal sealed class NoopEmailSender(
    ILogger<NoopEmailSender> logger,
    IDebugMode debugMode) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (debugMode.Enabled)
        {
            // The body carries the OTP and the invite link, so printing it is what lets
            // the flows be driven locally without a mailbox. It never runs outside
            // Development, which is what keeps codes out of a deployed log.
            logger.LogInformation(
                "Email not delivered (debug mode). To {Recipient}: {Subject}\n{Body}",
                message.To,
                message.Subject,
                message.TextBody);

            return Task.CompletedTask;
        }

        logger.LogWarning(
            "No email provider configured (Email:Provider). Dropped {Subject} to {Recipient}.",
            message.Subject,
            message.To);

        return Task.CompletedTask;
    }
}
