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

    // Ticked when the wizard first opens.
    //
    // Served rather than decided in the client, for the reason the module list itself
    // exists here: a default kept in the frontend is a second copy of a product decision,
    // and the copy is what drifts. Changing what a new company starts with should not
    // need a frontend release.
    public bool EnabledByDefault { get; init; }
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
        // Order matters: the wizard lays these out in a grid, and the order it reads
        // here is the order a company sees. Matches the screen rather than being
        // arbitrary — payroll seventh, not third.
        //
        // Employee Management and Leave Management start ticked. They are what every
        // company uses on day one and what most of the rest build on, where Payroll and
        // Benefits are deliberate purchases and Recruitment, Performance and Learning are
        // not day-one decisions. A wizard that opens with nothing selected, against a
        // rule that demands at least one, is a form that cannot be submitted until
        // somebody notices why.
        new() { Id = "employee-management", Label = "Employee Management", EnabledByDefault = true },
        new() { Id = "attendance", Label = "Attendance" },
        new() { Id = "leave-management", Label = "Leave Management", EnabledByDefault = true },
        new() { Id = "recruitment", Label = "Recruitment" },
        new() { Id = "performance", Label = "Performance" },
        new() { Id = "learning", Label = "Learning" },
        new() { Id = "payroll", Label = "Payroll" },
        new() { Id = "benefits", Label = "Benefits" },
    ];

    // What a company starts with, for a caller that wants the defaults without filtering
    // the list themselves.
    public static readonly IReadOnlyList<string> DefaultModules =
        [.. ModuleOptions.Where(option => option.EnabledByDefault).Select(option => option.Id)];

    public static readonly IReadOnlyList<string> Modules =
        [.. ModuleOptions.Select(option => option.Id)];

    // The capabilities, not the bare names. The wizard has to know whether to show a
    // second manager field, and that rule belongs here rather than as an
    // `if (structureType === "matrix")` in the client — a copy of a backend rule, in the
    // one place that cannot be kept in step with it.
    public static readonly IReadOnlyList<StructureTypeCapability> StructureTypes =
        Domain.Tenants.StructureTypes.All;

    // Suggestions, not a closed set. The wizard also offers "Custom", so the
    // validator accepts any reasonable string — these only pre-fill the dropdown.
    public static readonly IReadOnlyList<string> ManagerTitleSuggestions =
        ["Line Manager", "Reporting Manager", "Team Lead", "Supervisor"];

    public static readonly IReadOnlyList<string> DepartmentLabelSuggestions =
        ["Department", "Unit", "Division", "Team"];
}
