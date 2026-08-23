namespace Motee.Domain.Tenants;

public sealed record CompanySizeOption
{
    // The stored value.
    public required string Id { get; init; }

    // What the dropdown shows.
    public required string Label { get; init; }
}

public sealed record ModuleOption
{
    public required string Id { get; init; }

    public required string Label { get; init; }
}

// Generated from the frontend's onboarding-setup.types.ts. Served by
// GET /tenant/setup/options so the wizard renders from this list rather than
// keeping its own copy — two copies drift into a 400 nobody predicted.
public static class TenantSetupCatalogue
{
    public static readonly IReadOnlyList<string> Industries =
    [
        "Technology",
        "Finance & Banking",
        "Healthcare",
        "Education",
        "Manufacturing",
        "Retail & E-commerce",
        "Real Estate",
        "Legal Services",
        "Hospitality & Tourism",
        "Media & Entertainment",
        "Logistics & Supply Chain",
        "Energy & Utilities",
        "Government & Public Sector",
        "Non-Profit",
        "Other",
    ];

    public static readonly IReadOnlyList<CompanySizeOption> CompanySizes =
        [.. CompanySizes_All()];

    private static IEnumerable<CompanySizeOption> CompanySizes_All() =>
        Tenants.CompanySizes.All.Select(size => new CompanySizeOption
        {
            Id = size.ToString(),
            Label = Tenants.CompanySizes.ToLabel(size),
        });


    public static readonly IReadOnlyList<ModuleOption> ModuleOptions =
    [
        new() { Id = "employee-management", Label = "Employee Management" },
        new() { Id = "attendance", Label = "Attendance" },
        new() { Id = "payroll", Label = "Payroll" },
        new() { Id = "leave-management", Label = "Leave Management" },
        new() { Id = "recruitment", Label = "Recruitment" },
        new() { Id = "performance", Label = "Performance" },
    ];

    public static readonly IReadOnlyList<string> Modules =
        [.. ModuleOptions.Select(option => option.Id)];

    public static readonly IReadOnlyList<StructureType> StructureTypes =
        [.. Enum.GetValues<StructureType>()];

    // Suggestions, not a closed set. The wizard also offers "Custom", so the
    // validator accepts any reasonable string — these only pre-fill the dropdown.
    public static readonly IReadOnlyList<string> ManagerTitleSuggestions =
        ["Line Manager", "Reporting Manager", "Team Lead", "Supervisor"];

    public static readonly IReadOnlyList<string> DepartmentLabelSuggestions =
        ["Department", "Unit", "Division", "Team"];
}
