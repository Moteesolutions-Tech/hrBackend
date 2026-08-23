using Microsoft.EntityFrameworkCore;
using Motee.Application.Tenancy;
using Motee.Domain.Tenants;

namespace Motee.Infrastructure.Persistence;

internal sealed class TenantRepository(MoteeDbContext dbContext) : ITenantRepository
{
    public Task<Tenant?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(tenant => tenant.Id == id, cancellationToken);

    public Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        dbContext.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(tenant => tenant.Slug == slug, cancellationToken);
}
