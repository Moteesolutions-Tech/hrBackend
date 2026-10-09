using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Common;

internal sealed class CurrentEmployee(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    IRequestContext requestContext) : ICurrentEmployee
{
    public async Task<Guid?> IdAsync(CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(requestContext.UserId, out Guid userId))
        {
            return null;
        }

        if (currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        // The tenant clause is the whole point of this class. Users are not covered by
        // the global filter, so without it a session from one company could resolve an
        // employee id belonging to another.
        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.TenantId == tenantId)
            .Select(user => user.EmployeeId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
