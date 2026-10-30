using Hangfire;
using Motee.Application.Alerts;

namespace Motee.Api.Jobs;

// Registers the recurring jobs, after the application is already serving.
//
// This used to run inline during startup, which meant the first thing the process
// did was open a database connection — and a refused one took the whole API down
// before it bound a port. That is the wrong failure in both places it happens: on a
// developer's machine the API will not boot because a tunnel is closed, and in
// production a database that is thirty seconds behind the app turns a rolling
// deploy into an outage.
//
// Nothing about registering a cron entry needs to happen before the first request.
// Health checks answer, sign-in works, and the only thing missing in the window is
// a job that runs at a quarter past midnight.
//
// Retried rather than attempted once. A single try that fails quietly would leave a
// server where leave carry-over never runs and nothing says so — which is worse
// than crashing, because it is invisible until January.
internal sealed class RecurringJobRegistrar(
    IRecurringJobManager recurringJobs,
    IAlertSink alerts,
    ILogger<RecurringJobRegistrar> logger) : BackgroundService
{
    // Short at first, because the overwhelmingly common case is a database that is
    // seconds from ready. Capped so a long outage does not spin.
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(1);

    // Roughly a minute and a half of trying before anybody is told. Below that it is
    // a slow start rather than a problem, and alerting on every deploy is how an
    // alert stops being read.
    private const int AttemptsBeforeAlerting = 6;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan delay = FirstDelay;
        int attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;

            try
            {
                // Idempotent, which is what makes retrying safe: AddOrUpdate on an
                // id that already exists rewrites the schedule rather than adding a
                // second copy of the job.
                recurringJobs.AddOrUpdate<LeaveYearEndJob>(
                    LeaveYearEndJob.RecurringId,
                    job => job.RunAsync(CancellationToken.None),

                    // Just after midnight UTC. Late enough that a company whose year
                    // turned over at midnight has genuinely started the new one;
                    // early enough that carry-over is in place before anybody books
                    // leave against it on the first morning.
                    "15 0 * * *");

                // An hour later, and after carry-over, so a contract and the
                // leave year never land in somebody's panel at the same minute.
                // Early enough to be waiting when the first person signs in.
                recurringJobs.AddOrUpdate<ExpirySweepJob>(
                    ExpirySweepJob.RecurringId,
                    job => job.RunAsync(CancellationToken.None),
                    "0 6 * * *");

                logger.LogInformation(
                    "Recurring jobs registered on attempt {Attempt}.", attempt);

                return;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    exception,
                    "Could not register recurring jobs (attempt {Attempt}). Retrying in {Delay}.",
                    attempt,
                    delay);

                if (attempt == AttemptsBeforeAlerting)
                {
                    await RaiseAsync(exception, attempt);
                }

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = delay < MaximumDelay
                    ? TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaximumDelay.Ticks))
                    : MaximumDelay;
            }
        }
    }

    // Raised once, at the point it stops looking like a slow start. Keeping quiet
    // for ever would hide a server whose scheduled work never runs; alerting every
    // attempt would bury the one message that matters.
    private async Task RaiseAsync(Exception exception, int attempt)
    {
        try
        {
            await alerts.RaiseAsync(new Alert
            {
                Severity = AlertSeverity.Critical,
                Title = "Recurring jobs could not be registered",
                Detail =
                    $"{exception.GetType().Name}: {exception.Message}. Still retrying. "
                    + "Until this succeeds, nothing scheduled runs — including leave "
                    + "carry-over at the turn of the year.",
                Fingerprint = $"startup|recurring-jobs|{exception.GetType().FullName}",
                Facts = new Dictionary<string, string?>
                {
                    ["Attempts"] = attempt.ToString(),
                },
            });
        }
        catch (Exception alerting)
        {
            // An alert sink that is itself broken must not stop the retry loop. The
            // log line above has already recorded the original failure.
            logger.LogError(alerting, "Could not raise the recurring-jobs alert.");
        }
    }
}
