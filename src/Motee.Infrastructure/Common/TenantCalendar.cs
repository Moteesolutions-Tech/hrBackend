using Microsoft.EntityFrameworkCore;
using Motee.Application.Tenancy;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Common;

// Today, in the company's own time zone.
//
// Overdue, due today and due this week are comparisons against a date, and
// which date it is decides whether work reads as late. The client takes it from
// the browser, so the answer moves with whoever is looking.
//
// One instance per request, so the services inside one response cannot disagree
// about what day it is.
internal sealed class TenantCalendar(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider)
{
    private string? _zone;
    private bool _looked;

    public async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        TenantClock.DayOf(timeProvider.GetUtcNow(), await ZoneAsync(cancellationToken));

    private async Task<string?> ZoneAsync(CancellationToken cancellationToken)
    {
        if (_looked)
        {
            return _zone;
        }

        _looked = true;

        if (currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        _zone = settings?.TimeZoneId;

        return _zone;
    }
}
