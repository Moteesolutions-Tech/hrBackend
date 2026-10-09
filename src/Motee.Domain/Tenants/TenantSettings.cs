namespace Motee.Domain.Tenants;

// Defaults mirror the wizard's DEFAULT_COMPANY_SETUP, so a tenant that skips a step
// lands on the same values the form would have shown.
public sealed record TenantSettings
{
    public string ManagerTitle { get; init; } = "Line Manager";

    public string DepartmentLabel { get; init; } = "Department";

    public StructureType StructureType { get; init; } = StructureType.Hierarchical;

    public IReadOnlyList<string> EnabledModules { get; init; } = [];

    // When the company's leave year turns over, as a month and day rather than a date —
    // "1 April" means every April, not one specific one.
    //
    // Not always January. UK companies commonly run April to March to line up with the
    // tax year. Getting it wrong does not fail loudly: it files leave under the wrong
    // year, and the balance is quietly wrong with nothing to point at.
    public int LeaveYearStartMonth { get; init; } = 1;

    public int LeaveYearStartDay { get; init; } = 1;
}
