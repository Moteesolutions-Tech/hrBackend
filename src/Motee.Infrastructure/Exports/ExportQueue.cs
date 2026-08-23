using Hangfire;
using Motee.Application.Exports;

namespace Motee.Infrastructure.Exports;

// Hangfire hosts the worker in the API. Anything else — tests, tooling — runs the
// export inline instead, so the flow is exercised end to end without a queue.
public sealed class HangfireExportQueue(IBackgroundJobClient backgroundJobs) : IExportQueue
{
    public void Enqueue(Guid exportId) =>
        backgroundJobs.Enqueue<IExportRunner>(runner => runner.RunAsync(exportId, CancellationToken.None));
}

internal sealed class InlineExportQueue(IExportRunner runner) : IExportQueue
{
    public void Enqueue(Guid exportId) =>
        runner.RunAsync(exportId, CancellationToken.None).GetAwaiter().GetResult();
}
