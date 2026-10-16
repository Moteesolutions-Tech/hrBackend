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


    public bool EnabledByDefault { get; init; }
}


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

        new() { Id = "employee-management", Label = "Employee Management", EnabledByDefault = true },
        new() { Id = "attendance", Label = "Attendance" },
        new() { Id = "leave-management", Label = "Leave Management", EnabledByDefault = true },
        new() { Id = "recruitment", Label = "Recruitment" },
        new() { Id = "performance", Label = "Performance" },
        new() { Id = "learning", Label = "Learning" },
        new() { Id = "payroll", Label = "Payroll" },
        new() { Id = "benefits", Label = "Benefits" },
    ];


    public static readonly IReadOnlyList<string> DefaultModules =
        [.. ModuleOptions.Where(option => option.EnabledByDefault).Select(option => option.Id)];

    public static readonly IReadOnlyList<string> Modules =
        [.. ModuleOptions.Select(option => option.Id)];


    public static readonly IReadOnlyList<StructureTypeCapability> StructureTypes =
        Domain.Tenants.StructureTypes.All;

    public static readonly IReadOnlyList<string> ManagerTitleSuggestions =
        ["Line Manager", "Reporting Manager", "Team Lead", "Supervisor"];

    public static readonly IReadOnlyList<string> DepartmentLabelSuggestions =
        ["Department", "Unit", "Division", "Team"];
}
