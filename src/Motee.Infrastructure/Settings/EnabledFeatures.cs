using Microsoft.EntityFrameworkCore;
using Motee.Application.Settings;
using Motee.Application.Tenancy;
using Motee.Domain.Settings;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Settings;

// Which parts of the product this tenant has switched on.
//
// Resolved once per request and held, because the permission check asks it once
// per endpoint attribute. One primary-key read either way, but asking it
// repeatedly inside a single response would be one read per attribute for no
// answer that could have changed in between.
internal sealed class EnabledFeatures(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant) : IEnabledFeatures
{
    private FeatureState? _state;

    public async Task<FeatureState> CurrentAsync(CancellationToken cancellationToken = default)
    {
        if (_state is FeatureState held)
        {
            return held;
        }

        if (currentTenant.TenantId is not Guid tenantId)
        {
            // No tenant resolved yet — registration, a platform request, a
            // background job before it joins a tenant. The defaults are what a
            // new company gets, and nothing tenant-scoped is reachable anyway.
            _state = FeatureState.Default;

            return _state;
        }

        // Tenants are not tenant-scoped themselves, so this reads by id.
        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        // A tenant that has never been through the wizard has an empty list,
        // and an empty list means the defaults rather than nothing: a company
        // mid-registration that could reach no module at all would present as
        // every screen refusing.
        _state = settings is null || settings.EnabledModules.Count == 0
            ? FeatureState.Default
            : FeatureState.Of(settings.EnabledModules, settings.HiddenModules);

        return _state;
    }

    public async Task<bool> AllowsAsync(
        string moduleKey,
        CancellationToken cancellationToken = default)
    {
        // Asked before the governance lookup, because most keys belong to no
        // feature and that answer needs no tenant read at all.
        if (FeatureCatalogue.GovernorOf(moduleKey) is null)
        {
            return true;
        }

        FeatureState state = await CurrentAsync(cancellationToken);

        return state.Allows(moduleKey);
    }
}
