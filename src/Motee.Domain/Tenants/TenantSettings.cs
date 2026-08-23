namespace Motee.Domain.Tenants;

// Defaults mirror the wizard's DEFAULT_COMPANY_SETUP, so a tenant that skips a step
// lands on the same values the form would have shown.
public sealed record TenantSettings
{
    public string ManagerTitle { get; init; } = "Line Manager";

    public string DepartmentLabel { get; init; } = "Department";

    public StructureType StructureType { get; init; } = StructureType.Hierarchical;

    public IReadOnlyList<string> EnabledModules { get; init; } = [];
}
