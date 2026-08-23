using Motee.Application.Tenancy;
using Motee.Domain.Tenants;

namespace Motee.Tests.Integration;

// The wizard renders its dropdowns from these, and the validator accepts exactly
// these. Anything served here that the validator rejects is an option the user can
// pick and then be told is invalid.
public class TenantSetupOptionsTests
{
    private readonly TenantSetupValidator _validator = new();

    private static TenantSetupRequest With(
        string? industry = null,
        string? size = null,
        string? structure = null,
        IReadOnlyList<string>? modules = null) => new()
    {
        Industry = industry ?? "Technology",
        CompanySize = size ?? "1–10",
        ManagerTitle = "Line Manager",
        DepartmentLabel = "Department",
        StructureType = structure ?? "hierarchical",
        EnabledModules = modules ?? [],
    };

    [Fact]
    public void EveryOfferedIndustryIsAccepted()
    {
        Assert.All(TenantSetupOptions.Current.Industries, industry =>
            Assert.True(_validator.Validate(With(industry: industry)).IsValid, industry));
    }

    [Fact]
    public void EveryOfferedSizeIsAccepted()
    {
        Assert.All(TenantSetupOptions.Current.CompanySizes, size =>
            Assert.True(_validator.Validate(With(size: size.Id)).IsValid, size.Id));
    }

    [Fact]
    public void EveryOfferedStructureIsAccepted()
    {
        Assert.All(TenantSetupOptions.Current.StructureTypes, structure =>
            Assert.True(
                _validator.Validate(With(structure: structure.ToString())).IsValid,
                structure.ToString()));
    }

    [Fact]
    public void EveryOfferedModuleIsAccepted()
    {
        Assert.All(TenantSetupOptions.Current.Modules, module =>
            Assert.True(_validator.Validate(With(modules: [module.Id])).IsValid, module.Id));
    }

    [Fact]
    public void AllModulesTogetherAreAccepted()
    {
        IReadOnlyList<string> everything =
            [.. TenantSetupOptions.Current.Modules.Select(module => module.Id)];

        Assert.True(_validator.Validate(With(modules: everything)).IsValid);
    }

    // The en dash in "10–50" is the trap: a hyphen typed by hand fails validation.
    [Fact]
    public void SizesAreServedWithTheExactCharactersTheValidatorExpects()
    {
        Assert.Contains("10–50", TenantSetupOptions.Current.CompanySizes.Select(size => size.Label));
        Assert.DoesNotContain("10-50", TenantSetupOptions.Current.CompanySizes.Select(size => size.Label));
    }

    [Fact]
    public void ModulesCarryALabelForDisplay()
    {
        Assert.All(TenantSetupOptions.Current.Modules, module =>
        {
            Assert.False(string.IsNullOrWhiteSpace(module.Id));
            Assert.False(string.IsNullOrWhiteSpace(module.Label));
        });

        Assert.Equal(
            "Employee Management",
            TenantSetupOptions.Current.Modules.First(m => m.Id == "employee-management").Label);
    }

    [Fact]
    public void ServesTheWholeCatalogue()
    {
        Assert.Equal(TenantSetupCatalogue.Industries, TenantSetupOptions.Current.Industries);
        Assert.Equal(TenantSetupCatalogue.CompanySizes, TenantSetupOptions.Current.CompanySizes);
        Assert.Equal(TenantSetupCatalogue.StructureTypes, TenantSetupOptions.Current.StructureTypes);
        Assert.Equal(TenantSetupCatalogue.Modules.Count, TenantSetupOptions.Current.Modules.Count);
    }
}
