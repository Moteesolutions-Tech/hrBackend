using Motee.Application.Countries;
using Motee.Application.Tenancy;
using Motee.Domain.Countries;
using Motee.Domain.Tenants;

namespace Motee.Application.Localization;

internal sealed class TenantLocaleService(
    ICurrentTenant currentTenant,
    ITenantRepository tenants,
    ICountryProfileProvider countryProfiles) : ITenantLocaleService
{
    public async Task<TenantLocaleDto?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        Tenant? tenant = await tenants.FindAsync(tenantId, cancellationToken);

        if (tenant is null)
        {
            return null;
        }

        ICountryProfile profile = countryProfiles.Get(tenant.CountryCode);

        return new TenantLocaleDto
        {
            Id = tenant.Id,
            Name = tenant.Name,
            Slug = tenant.Slug,
            Plan = tenant.Plan,
            Status = tenant.Status,
            Industry = tenant.Industry,
            Country = profile.DisplayName,
            CountryCode = profile.CountryCode.Value,
            Timezone = profile.TimeZoneId,
            Currency = profile.CurrencyCode,
            CurrencySymbol = profile.CurrencySymbol,
            Locale = profile.Locale,
            LogoUrl = tenant.LogoUrl,
            PrimaryColor = tenant.PrimaryColor,
            UsesPayeStarterRecords = profile.UsesPayeStarterRecords,
            CreatedAt = tenant.CreatedAt,
            TrialEndsAt = tenant.TrialEndsAt,
            BillingEmail = tenant.BillingEmail,
        };
    }
}
