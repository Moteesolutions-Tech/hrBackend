using Motee.Domain.Tenants;

namespace Motee.Application.Tenancy;

public interface ITenantSlugGenerator
{
    Task<string> GenerateAsync(string companyName, CancellationToken cancellationToken = default);
}

// Availability is checked here for a useful slug, but the unique index on
// tenants.slug is what actually guarantees it — two concurrent registrations can
// both see the same slug as free. Registration retries on the constraint violation.
public sealed class TenantSlugGenerator(ITenantRepository tenants) : ITenantSlugGenerator
{
    private const int MaxAttempts = 100;

    public async Task<string> GenerateAsync(string companyName, CancellationToken cancellationToken = default)
    {
        string root = TenantSlug.Normalise(companyName);

        if (await IsFreeAsync(root, cancellationToken))
        {
            return root;
        }

        for (int counter = 2; counter <= MaxAttempts; counter++)
        {
            string candidate = TenantSlug.WithSuffix(root, counter);

            if (await IsFreeAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        return TenantSlug.WithSuffix(root, Random.Shared.Next(1_000, 9_999));
    }

    private async Task<bool> IsFreeAsync(string slug, CancellationToken cancellationToken) =>
        await tenants.FindBySlugAsync(slug, cancellationToken) is null;
}
