using Hangfire;
using Motee.Application.Notifications;

namespace Motee.Infrastructure.Notifications;

public sealed class HangfireEmailQueue(IBackgroundJobClient backgroundJobs) : IEmailQueue
{
    public void Enqueue(EmailMessage message) =>
        backgroundJobs.Enqueue<SendEmailJob>(job => job.ExecuteAsync(message));
}
