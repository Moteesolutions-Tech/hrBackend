using Motee.Domain.Tenants;

namespace Motee.Application.Tenancy;

public interface ITenantRepository
{
    Task<Tenant?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default);
}
