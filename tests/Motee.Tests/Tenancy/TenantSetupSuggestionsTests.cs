using Motee.Application.Tenancy;
using Motee.Domain.Tenants;

namespace Motee.Tests.Tenancy;

// Manager title and department label are open fields. The lists only pre-fill the
// dropdowns — the wizard also offers "Custom", so a value outside the list has to
// keep validating or that option is broken.
public class TenantSetupSuggestionsTests
{
    private readonly TenantSetupValidator _validator = new();

    private static TenantSetupRequest With(string? managerTitle = null, string? departmentLabel = null) => new()
    {
        Industry = "Technology",
        CompanySize = "1–10",
        ManagerTitle = managerTitle ?? "Line Manager",
        DepartmentLabel = departmentLabel ?? "Department",
        StructureType = "hierarchical",
        EnabledModules = [],
    };

    [Fact]
    public void EverySuggestedManagerTitleIsAccepted()
    {
        Assert.All(TenantSetupOptions.Current.ManagerTitleSuggestions, title =>
            Assert.True(_validator.Validate(With(managerTitle: title)).IsValid, title));
    }

    [Fact]
    public void EverySuggestedDepartmentLabelIsAccepted()
    {
        Assert.All(TenantSetupOptions.Current.DepartmentLabelSuggestions, label =>
            Assert.True(_validator.Validate(With(departmentLabel: label)).IsValid, label));
    }

    // This is what "Custom" depends on.
    [Theory]
    [InlineData("Pod Lead")]
    [InlineData("Chief")]
    [InlineData("Oga")]
    public void AValueOutsideTheSuggestionsIsStillAccepted(string custom)
    {
        Assert.True(_validator.Validate(With(managerTitle: custom)).IsValid);
        Assert.True(_validator.Validate(With(departmentLabel: custom)).IsValid);
    }

    [Fact]
    public void TheSuggestionsCoverWhatTheWizardOffers()
    {
        Assert.Equal(
            ["Line Manager", "Reporting Manager", "Team Lead", "Supervisor"],
            TenantSetupOptions.Current.ManagerTitleSuggestions);

        Assert.Equal(
            ["Department", "Unit", "Division", "Team"],
            TenantSetupOptions.Current.DepartmentLabelSuggestions);
    }

    // "Custom" is the UI's affordance for typing your own, not a value to store.
    [Fact]
    public void CustomIsNotOfferedAsAValue()
    {
        Assert.DoesNotContain("Custom", TenantSetupOptions.Current.ManagerTitleSuggestions);
        Assert.DoesNotContain("Custom", TenantSetupOptions.Current.DepartmentLabelSuggestions);
    }

    [Fact]
    public void TheDefaultsAreTheFirstSuggestion()
    {
        TenantSettings defaults = new();

        Assert.Equal(TenantSetupOptions.Current.ManagerTitleSuggestions[0], defaults.ManagerTitle);
        Assert.Equal(TenantSetupOptions.Current.DepartmentLabelSuggestions[0], defaults.DepartmentLabel);
    }
}
