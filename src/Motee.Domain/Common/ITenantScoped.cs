namespace Motee.Domain.Common;

// Marks an entity as belonging to exactly one tenant. Every such entity is filtered
// automatically by MoteeDbContext, so a query that forgets a tenant predicate
// returns nothing rather than another company's rows.
public interface ITenantScoped
{
    Guid TenantId { get; set; }
}
