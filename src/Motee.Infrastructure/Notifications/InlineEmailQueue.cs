using Microsoft.Extensions.Logging;
using Motee.Application.Notifications;

namespace Motee.Infrastructure.Notifications;

// Used where Hangfire is not hosted — tests, and any process that should not run
// background work. Sends on the calling thread and swallows failures so a dead
// provider cannot fail the request that triggered the email.
internal sealed class InlineEmailQueue(
    IEmailSender emailSender,
    IEmailDeliveryLog deliveries,
    ILogger<InlineEmailQueue> logger) : IEmailQueue
{
    public void Enqueue(EmailMessage message)
    {
        try
        {
            emailSender.SendAsync(message).GetAwaiter().GetResult();

            if (message.About is EmailAbout about)
            {
                deliveries.MarkSentAsync(about).GetAwaiter().GetResult();
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Inline send of {Subject} to {Recipient} failed.",
                message.Subject, message.To);
        }
    }
}
