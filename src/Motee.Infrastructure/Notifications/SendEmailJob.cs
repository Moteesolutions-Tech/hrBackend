using Hangfire;
using Motee.Application.Auth;

namespace Motee.Infrastructure.Notifications;

public sealed class SendEmailJob(IEmailSender emailSender)
{
    // Short delays on purpose. A verification code expires in minutes, so an hour-long
    // backoff would deliver something already dead; three quick attempts cover a
    // provider blip, and anything beyond that is the user's Resend button.
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [10, 30, 60])]
    public Task ExecuteAsync(EmailMessage message) => emailSender.SendAsync(message);
}
