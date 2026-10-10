using Hangfire;
using Motee.Application.Alerts;
using Motee.Application.Notifications;

namespace Motee.Infrastructure.Notifications;

public sealed class SendEmailJob(IEmailSender emailSender, IAlertSink alerts)
{
    // Short delays on purpose. A verification code expires in minutes, so an hour-long
    // backoff would deliver something already dead; three quick attempts cover a
    // provider blip, and anything beyond that is the user's Resend button.
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [10, 30, 60])]
    public async Task ExecuteAsync(EmailMessage message)
    {
        try
        {
            await emailSender.SendAsync(message);
        }
        catch (Exception exception)
        {
            // Alerted and rethrown, in that order. Rethrowing is what lets Hangfire
            // retry; alerting is what stops the failure being invisible when it does not
            // recover — a failed job sits in a dashboard nobody opens, while the user was
            // told their code was on its way.
            //
            // Raised on every attempt rather than only the last. The throttle collapses
            // repeats, so a provider outage produces one message rather than one per
            // email, and knowing immediately beats knowing after three backoffs.
            await alerts.RaiseAsync(new Alert
            {
                Severity = AlertSeverity.Critical,
                Title = $"Email send failed ({exception.GetType().Name})",
                Detail = $"{exception.GetType().Name}: {exception.Message}",

                // By failure type, not by recipient. A provider outage is one problem
                // however many addresses it affects, and fingerprinting per recipient
                // would put the throttle to no use at all.
                Fingerprint = $"job|email|{exception.GetType().FullName}",
                Facts = new Dictionary<string, string?>
                {
                    ["Job"] = "send-email",

                    // The subject, not the body and not the address. Enough to tell a
                    // verification code from an invitation; not enough to put somebody's
                    // details in a channel.
                    ["Subject"] = message.Subject,
                },
            });

            throw;
        }
    }
}
