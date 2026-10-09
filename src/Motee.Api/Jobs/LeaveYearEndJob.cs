using Microsoft.EntityFrameworkCore;
using Motee.Application.Leave;
using Motee.Application.Tenancy;
using Motee.Infrastructure.Persistence;

namespace Motee.Api.Jobs;

// Closes each company's leave year on the morning after it ends.
//
// Runs daily rather than annually, because companies do not share a year end — one runs
// January to December, the next April to March — and a single yearly schedule would be
// right for at most one of them. Checking every day and acting on the few tenants whose
// year actually turned over is both simpler and correct for all of them.
//
// Safe to run repeatedly. Carry-over rows are unique per person, type and year, and the
// service skips what it has already written, so a retry after a partial failure finishes
// the job rather than doubling it.
internal sealed class LeaveYearEndJob(
    MoteeDbContext dbContext,
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<LeaveYearEndJob> logger)
{
    public const string RecurringId = "leave-year-end";

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        // Tenants are not tenant-scoped, so this reads them all directly — which is the
        // point: the job has no request and no signed-in user, and works across every
        // company on the instance.
        List<Guid> tenantIds = await dbContext.Tenants
            .AsNoTracking()
            .Select(tenant => tenant.Id)
            .ToListAsync(cancellationToken);

        foreach (Guid tenantId in tenantIds)
        {
            try
            {
                await CloseAsync(tenantId, today, cancellationToken);
            }
            catch (Exception exception)
            {
                // One company's bad configuration must not stop every other company's
                // year from closing. Logged and carried on, and the next daily run will
                // try this one again.
                logger.LogError(
                    exception,
                    "Leave year end failed for tenant {TenantId}; continuing with the rest.",
                    tenantId);
            }
        }
    }

    private async Task CloseAsync(
        Guid tenantId,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        // A scope per tenant, because the DbContext caches the tenant it was built for in
        // its query filter. Reusing one across companies would read the first tenant's
        // rows for all of them.
        using IServiceScope scope = scopes.CreateScope();
        using IDisposable ambient = AmbientTenant.Use(tenantId);

        ILeaveYearEndService yearEnd =
            scope.ServiceProvider.GetRequiredService<ILeaveYearEndService>();

        // Only on the first day of a new leave year. Every other day this is a cheap
        // no-op, and deliberately so — recomputing carry-over daily would move days
        // somebody has already started spending.
        if (!await yearEnd.IsFirstDayOfYearAsync(today, cancellationToken))
        {
            return;
        }

        LeaveYearEndResult result = await yearEnd.CloseAsync(
            today.AddDays(-1), cancellationToken);

        if (result.AlreadyClosed && result.CarriedOver.Count == 0)
        {
            return;
        }

        logger.LogInformation(
            "Closed leave year {Year} for tenant {TenantId}: {Carried} carry-overs written, "
            + "{Lapsed} days lapsed.",
            result.ClosedYearLabel,
            tenantId,
            result.CarriedOver.Count,
            result.DaysLapsed);
    }
}
