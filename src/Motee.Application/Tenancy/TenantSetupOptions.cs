using Motee.Domain.Tenants;

namespace Motee.Application.Tenancy;

// What the wizard may offer. Identical for every tenant and only changes on deploy,
// so it is served separately from a tenant's own setup and can be cached.
public sealed record TenantSetupOptions
{
    public required IReadOnlyList<string> Industries { get; init; }

    public required IReadOnlyList<CompanySizeOption> CompanySizes { get; init; }

    public required IReadOnlyList<StructureType> StructureTypes { get; init; }

    public required IReadOnlyList<ModuleOption> Modules { get; init; }

    // Open fields: these pre-fill the dropdown, and anything else is accepted too.
    public required IReadOnlyList<string> ManagerTitleSuggestions { get; init; }

    public required IReadOnlyList<string> DepartmentLabelSuggestions { get; init; }

    public static TenantSetupOptions Current => new()
    {
        Industries = TenantSetupCatalogue.Industries,
        CompanySizes = TenantSetupCatalogue.CompanySizes,
        StructureTypes = TenantSetupCatalogue.StructureTypes,
        Modules = TenantSetupCatalogue.ModuleOptions,
        ManagerTitleSuggestions = TenantSetupCatalogue.ManagerTitleSuggestions,
        DepartmentLabelSuggestions = TenantSetupCatalogue.DepartmentLabelSuggestions,
    };
}
