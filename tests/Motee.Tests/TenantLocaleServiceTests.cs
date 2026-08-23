using Motee.Application.Countries;
using Motee.Application.Localization;
using Motee.Application.Tenancy;
using Motee.Domain.Common;
using Motee.Domain.Countries;
using Motee.Domain.Tenants;

namespace Motee.Tests;

public class TenantLocaleServiceTests
{
    [Fact]
    public async Task NigerianTenantResolvesToNairaAndLagos()
    {
        TenantLocaleDto? locale = await ResolveAsync(CountryCode.Nigeria);

        Assert.NotNull(locale);
        Assert.Equal("NG", locale.CountryCode);
        Assert.Equal("Nigeria", locale.Country);
        Assert.Equal("NGN", locale.Currency);
        Assert.Equal("₦", locale.CurrencySymbol);
        Assert.Equal("en-NG", locale.Locale);
        Assert.Equal("Africa/Lagos", locale.Timezone);
    }

    [Fact]
    public async Task UkTenantResolvesToPoundsAndLondon()
    {
        TenantLocaleDto? locale = await ResolveAsync(CountryCode.UnitedKingdom);

        Assert.NotNull(locale);
        Assert.Equal("GB", locale.CountryCode);
        Assert.Equal("United Kingdom", locale.Country);
        Assert.Equal("GBP", locale.Currency);
        Assert.Equal("£", locale.CurrencySymbol);
        Assert.Equal("en-GB", locale.Locale);
        Assert.Equal("Europe/London", locale.Timezone);
    }

    // starter-tax.ts: "UK-only. Nigerian tenants use a different model and never
    // generate these records."
    [Fact]
    public async Task PayeStarterRecordsAreUkOnly()
    {
        TenantLocaleDto? nigeria = await ResolveAsync(CountryCode.Nigeria);
        TenantLocaleDto? unitedKingdom = await ResolveAsync(CountryCode.UnitedKingdom);

        Assert.False(nigeria!.UsesPayeStarterRecords);
        Assert.True(unitedKingdom!.UsesPayeStarterRecords);
    }

    [Fact]
    public async Task ReturnsNullWhenNoTenantOnTheToken()
    {
        TenantLocaleService service = new(
            new StubCurrentTenant(null),
            new StubTenantRepository(null),
            BuildProvider());

        Assert.Null(await service.GetCurrentAsync());
    }

    private static async Task<TenantLocaleDto?> ResolveAsync(CountryCode countryCode)
    {
        Guid tenantId = Guid.NewGuid();

        Tenant tenant = new()
        {
            Id = tenantId,
            Name = "Acme",
            Slug = "acme",
            CountryCode = countryCode,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        TenantLocaleService service = new(
            new StubCurrentTenant(tenantId),
            new StubTenantRepository(tenant),
            BuildProvider());

        return await service.GetCurrentAsync();
    }

    private static ICountryProfileProvider BuildProvider() =>
        new CountryProfileProvider([new NigeriaProfile(), new UnitedKingdomProfile()]);

    private sealed class StubCurrentTenant(Guid? tenantId) : ICurrentTenant
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class StubTenantRepository(Tenant? tenant) : ITenantRepository
    {
        public Task<Tenant?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenant);

        public Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenant);
    }
}
