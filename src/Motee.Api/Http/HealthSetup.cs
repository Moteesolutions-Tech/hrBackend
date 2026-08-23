using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Motee.Infrastructure.Persistence;

namespace Motee.Api.Http;

// Two questions a host asks, which are not the same question.
//
// Liveness — is the process running? A restart is the only cure for a no.
// Readiness — can it serve a request that needs data? A no here is often temporary.
//
// Answering them together is the common mistake: a hosting platform that restarts the
// container because a serverless database was asleep never lets it come back.
internal static class HealthSetup
{
    private const string ReadyTag = "ready";

    public static IServiceCollection AddMoteeHealth(this IServiceCollection services) =>
        services
            .AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag])
            .Services;

    public static WebApplication MapMoteeHealth(this WebApplication app)
    {
        // No checks run: if this responds at all, the process is alive. Deliberately
        // does not touch the database.
        app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
        });

        return app;
    }
}

// CanConnect rather than a query: this is asked repeatedly by a load balancer, and it
// should cost a connection check, not a table scan.
internal sealed class DatabaseHealthCheck(MoteeDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The database did not accept a connection.");
        }
        catch (Exception exception)
        {
            // The reason stays in the log. What reaches an unauthenticated caller is
            // "unhealthy" and nothing about hosts, users or drivers.
            return HealthCheckResult.Unhealthy("The database could not be reached.", exception);
        }
    }
}
