using Motee.Application.Tenancy;
using Motee.Domain.Tenants;

namespace Motee.Tests.Tenancy;

public class TenantSetupValidatorTests
{
    private readonly TenantSetupValidator _validator = new();

    private static TenantSetupRequest Valid() => new()
    {
        Industry = "Technology",
        CompanySize = "1–10",
        CompanyEmailDomain = "acme.com",
        CompanyPolicies = null,
        ManagerTitle = "Line Manager",
        DepartmentLabel = "Department",
        StructureType = "hierarchical",
        EnabledModules = ["employee-management", "leave-management"],
    };

    private string[] ErrorsFor(TenantSetupRequest request, string property) =>
        _validator.Validate(request).Errors
            .Where(error => error.PropertyName == property)
            .Select(error => error.ErrorMessage)
            .ToArray();

    [Fact]
    public void AcceptsACompleteSetup()
    {
        Assert.True(_validator.Validate(Valid()).IsValid);
    }

    // The wizard offers a fixed list, so anything else means a tampered payload.
    [Theory]
    [InlineData("Technology", true)]
    [InlineData("Non-Profit", true)]
    [InlineData("Other", true)]
    [InlineData("Underwater Basket Weaving", false)]
    [InlineData("", false)]
    public void IndustryMustComeFromTheCatalogue(string industry, bool expected)
    {
        Assert.Equal(expected, ErrorsFor(Valid() with { Industry = industry }, "Industry").Length == 0);
    }

    // The dashes in "1–10" are en dashes, not hyphens — a mismatch here would reject
    // every submission the wizard makes.
    [Theory]
    [InlineData("1–10", true)]
    [InlineData("1000+", true)]
    [InlineData("1-10", false)]
    [InlineData("massive", false)]
    public void CompanySizeMustComeFromTheCatalogue(string size, bool expected)
    {
        Assert.Equal(expected, ErrorsFor(Valid() with { CompanySize = size }, "CompanySize").Length == 0);
    }

    [Theory]
    [InlineData("hierarchical", true)]
    [InlineData("flat", true)]
    [InlineData("matrix", false)]
    [InlineData("", false)]
    public void StructureTypeIsOneOfTwo(string structureType, bool expected)
    {
        Assert.Equal(
            expected,
            ErrorsFor(Valid() with { StructureType = structureType }, "StructureType").Length == 0);
    }

    [Fact]
    public void EveryEnabledModuleMustExist()
    {
        Assert.NotEmpty(ErrorsFor(
            Valid() with { EnabledModules = ["employee-management", "time-travel"] },
            "EnabledModules"));
    }

    [Fact]
    public void EnablingNothingIsAllowed()
    {
        Assert.Empty(ErrorsFor(Valid() with { EnabledModules = [] }, "EnabledModules"));
    }

    [Fact]
    public void TheSameModuleTwiceIsRejected()
    {
        Assert.NotEmpty(ErrorsFor(
            Valid() with { EnabledModules = ["payroll", "payroll"] },
            "EnabledModules"));
    }

    [Theory]
    [InlineData("acme.com", true)]
    [InlineData("northwind.co.uk", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("not a domain", false)]
    [InlineData("ada@acme.com", false)]
    public void EmailDomainIsOptionalButMustBeADomainWhenGiven(string? domain, bool expected)
    {
        Assert.Equal(
            expected,
            ErrorsFor(Valid() with { CompanyEmailDomain = domain }, "CompanyEmailDomain").Length == 0);
    }

    [Fact]
    public void ManagerTitleAndDepartmentLabelAreRequired()
    {
        Assert.NotEmpty(ErrorsFor(Valid() with { ManagerTitle = "  " }, "ManagerTitle"));
        Assert.NotEmpty(ErrorsFor(Valid() with { DepartmentLabel = "" }, "DepartmentLabel"));
    }

    [Fact]
    public void LabelsAreLengthCapped()
    {
        Assert.NotEmpty(ErrorsFor(Valid() with { ManagerTitle = new string('x', 101) }, "ManagerTitle"));
        Assert.NotEmpty(ErrorsFor(Valid() with { CompanyPolicies = new string('x', 20_001) }, "CompanyPolicies"));
    }

    [Fact]
    public void ReportsEveryProblemAtOnce()
    {
        int errors = _validator.Validate(new TenantSetupRequest
        {
            Industry = "Nope",
            CompanySize = "Nope",
            CompanyEmailDomain = "nope",
            ManagerTitle = "",
            DepartmentLabel = "",
            StructureType = "nope",
            EnabledModules = ["nope"],
        }).Errors.Count;

        Assert.True(errors >= 7, $"expected each field reported, got {errors}");
    }

    [Fact]
    public void TheCatalogueMatchesTheWizard()
    {
        Assert.Equal(15, TenantSetupCatalogue.Industries.Count);
        Assert.Equal(6, TenantSetupCatalogue.CompanySizes.Count);
        Assert.Equal(6, TenantSetupCatalogue.Modules.Count);
    }
}
