using Microsoft.EntityFrameworkCore;
using Motee.Application.Tenancy;
using Motee.Domain.Leave;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Leave;

// Which twelve months a date belongs to, for this company.
//
// Its own class rather than a line in each service, because two services disagreeing
// about where the year turns over would file the same leave under different years — and
// the balances would be wrong in a way that looks like an arithmetic bug rather than a
// configuration one.
internal sealed class LeaveYearResolver(MoteeDbContext dbContext, ICurrentTenant currentTenant)
{
    // January by default. Companies that run April to March change it in settings; the
    // default has to be the one that is right more often, and is also the one somebody
    // who never looks at the setting would assume.
    private const int DefaultStartMonth = 1;
    private const int DefaultStartDay = 1;

    public async Task<LeaveYear> ForAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        (int month, int day) = await TurnoverAsync(cancellationToken);

        return LeaveYear.Containing(date, month, day);
    }

    private async Task<(int Month, int Day)> TurnoverAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return (DefaultStartMonth, DefaultStartDay);
        }

        // Tenants are not tenant-scoped themselves, so this reads by id directly.
        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        int month = settings?.LeaveYearStartMonth ?? DefaultStartMonth;
        int day = settings?.LeaveYearStartDay ?? DefaultStartDay;

        // A stored 0 or 13 would throw deep inside DateOnly, presenting as a crash on the
        // leave screen rather than as the bad setting it is. Falling back keeps the
        // module working while the setting is wrong.
        return month is < 1 or > 12 || day is < 1 or > 31
            ? (DefaultStartMonth, DefaultStartDay)
            : (month, day);
    }
}
