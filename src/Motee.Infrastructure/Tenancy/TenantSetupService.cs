using Microsoft.EntityFrameworkCore;
using Motee.Application.Countries;
using Motee.Application.Tenancy;
using Motee.Domain.Countries;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Tenancy;

internal sealed class TenantSetupService(
    MoteeDbContext dbContext,
    ICountryProfileProvider countryProfiles,
    TimeProvider timeProvider) : ITenantSetupService
{
    public async Task<TenantSetupDto?> GetAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        Tenant? tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == tenantId, cancellationToken);

        if (tenant is null)
        {
            return null;
        }

        ICountryProfile profile = countryProfiles.Get(tenant.CountryCode);

        return new TenantSetupDto
        {
            CompanyName = tenant.Name,
            CountryCode = profile.CountryCode.Value,
            Country = profile.DisplayName,
            Industry = tenant.Industry,
            CompanySize = tenant.CompanySize,
            CompanyEmailDomain = tenant.CompanyEmailDomain,
            CompanyPolicies = tenant.CompanyPolicies,
            ManagerTitle = tenant.Settings.ManagerTitle,
            DepartmentLabel = tenant.Settings.DepartmentLabel,
            StructureType = tenant.Settings.StructureType,
            EnabledModules = tenant.Settings.EnabledModules,
            OnboardingCompleted = tenant.OnboardingCompletedAt is not null,
            OnboardingCompletedAt = tenant.OnboardingCompletedAt,
        };
    }

    public async Task<bool> SaveAsync(
        Guid tenantId,
        TenantSetupRequest request,
        CancellationToken cancellationToken = default)
    {
        Tenant? tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(candidate => candidate.Id == tenantId, cancellationToken);

        if (tenant is null)
        {
            return false;
        }

        // Name, slug and country are not taken from the request at all, so a crafted
        // payload cannot rename the company or move its jurisdiction.
        tenant.Industry = request.Industry;
        tenant.CompanySize = CompanySizes.TryParse(request.CompanySize, out CompanySize size)
            ? size
            : null;
        tenant.CompanyEmailDomain = Trimmed(request.CompanyEmailDomain);
        tenant.CompanyPolicies = Trimmed(request.CompanyPolicies);
        // LogoUrl is left alone: a logo is uploaded, not typed into this form.

        tenant.Settings = new TenantSettings
        {
            ManagerTitle = request.ManagerTitle.Trim(),
            DepartmentLabel = request.DepartmentLabel.Trim(),
            StructureType = StructureTypes.TryParse(request.StructureType, out StructureType structureType)
                ? structureType
                : StructureType.Hierarchical,
            EnabledModules = request.EnabledModules,
        };

        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> CompleteAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        Tenant? tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(candidate => candidate.Id == tenantId, cancellationToken);

        if (tenant is null)
        {
            return false;
        }

        // Left alone once set: revisiting settings later must not rewrite the date
        // the tenant actually went live.
        if (tenant.OnboardingCompletedAt is null)
        {
            tenant.OnboardingCompletedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
