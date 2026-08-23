using Motee.Application.Tenancy;
using Motee.Domain.Common;
using Motee.Domain.Tenants;

namespace Motee.Tests.Tenancy;

public class TenantSlugGeneratorTests
{
    [Fact]
    public async Task UsesTheNormalisedSlugWhenFree()
    {
        TenantSlugGenerator generator = new(new StubTenantRepository());

        Assert.Equal("acme-corporation", await generator.GenerateAsync("Acme Corporation"));
    }

    [Fact]
    public async Task AppendsCounterOnCollision()
    {
        TenantSlugGenerator generator = new(new StubTenantRepository("acme"));

        Assert.Equal("acme-2", await generator.GenerateAsync("Acme"));
    }

    [Fact]
    public async Task KeepsCountingPastConsecutiveCollisions()
    {
        TenantSlugGenerator generator = new(new StubTenantRepository("acme", "acme-2", "acme-3"));

        Assert.Equal("acme-4", await generator.GenerateAsync("Acme"));
    }

    // The slug column is varchar(100); a suffix must not push it over.
    [Fact]
    public async Task StaysWithinColumnLimitWhenSuffixed()
    {
        string longName = new('a', TenantSlug.MaxLength + 20);
        TenantSlugGenerator generator = new(new StubTenantRepository(new string('a', TenantSlug.MaxLength)));

        string slug = await generator.GenerateAsync(longName);

        Assert.True(slug.Length <= TenantSlug.MaxLength, $"length was {slug.Length}");
    }

    private sealed class StubTenantRepository(params string[] takenSlugs) : ITenantRepository
    {
        private readonly HashSet<string> _taken = new(takenSlugs, StringComparer.Ordinal);

        public Task<Tenant?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Tenant?>(null);

        public Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(_taken.Contains(slug)
                ? new Tenant
                {
                    Id = Guid.NewGuid(),
                    Name = slug,
                    Slug = slug,
                    CountryCode = CountryCode.Nigeria,
                }
                : null);
    }
}
