using Motee.Application.Tenancy;
using Motee.Domain.Tenants;

namespace Motee.Tests.Tenancy;

public class CompanySizeTests
{
    private readonly TenantSetupValidator _validator = new();

    private static TenantSetupRequest With(string size) => new()
    {
        Industry = "Technology",
        CompanySize = size,
        ManagerTitle = "Line Manager",
        DepartmentLabel = "Department",
        StructureType = "hierarchical",
        EnabledModules = [],
    };

    [Theory]
    [InlineData(CompanySize.Micro, "1–10")]
    [InlineData(CompanySize.Small, "10–50")]
    [InlineData(CompanySize.Medium, "50–200")]
    [InlineData(CompanySize.Large, "200–500")]
    [InlineData(CompanySize.VeryLarge, "500–1000")]
    [InlineData(CompanySize.Enterprise, "1000+")]
    public void EachBandHasItsDisplayLabel(CompanySize size, string expected)
    {
        Assert.Equal(expected, CompanySizes.ToLabel(size));
    }

    // Declaration order is the point: pricing and feature gates compare bands, and
    // string comparison would sort "1000+" before "200–500".
    [Fact]
    public void BandsOrderFromSmallestToLargest()
    {
        Assert.True(CompanySize.Micro < CompanySize.Small);
        Assert.True(CompanySize.Small < CompanySize.Medium);
        Assert.True(CompanySize.Medium < CompanySize.Large);
        Assert.True(CompanySize.Large < CompanySize.VeryLarge);
        Assert.True(CompanySize.VeryLarge < CompanySize.Enterprise);
    }

    [Fact]
    public void ComparisonsReadTheWayPricingRulesWillBeWritten()
    {
        Assert.True(CompanySize.Enterprise >= CompanySize.Large);
        Assert.False(CompanySize.Small >= CompanySize.Large);
    }

    // The member name is what persists, so a reworded label never rewrites stored data.
    [Theory]
    [InlineData("Small", CompanySize.Small)]
    [InlineData("small", CompanySize.Small)]
    [InlineData("VeryLarge", CompanySize.VeryLarge)]
    public void ParsesTheStoredName(string input, CompanySize expected)
    {
        Assert.True(CompanySizes.TryParse(input, out CompanySize parsed));
        Assert.Equal(expected, parsed);
    }

    // Accepting the label too keeps existing rows and the current CSV template working.
    [Theory]
    [InlineData("10–50", CompanySize.Small)]
    [InlineData("1000+", CompanySize.Enterprise)]
    public void AlsoParsesTheDisplayLabel(string input, CompanySize expected)
    {
        Assert.True(CompanySizes.TryParse(input, out CompanySize parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Huge")]
    [InlineData("10-50")]
    [InlineData("0")]
    [InlineData("3")]
    public void RefusesAnythingElse(string? input)
    {
        Assert.False(CompanySizes.TryParse(input, out _));
    }

    [Fact]
    public void EveryOfferedOptionIsAccepted()
    {
        Assert.All(TenantSetupOptions.Current.CompanySizes, option =>
        {
            Assert.True(_validator.Validate(With(option.Id)).IsValid, option.Id);
            Assert.True(_validator.Validate(With(option.Label)).IsValid, option.Label);
        });
    }

    [Fact]
    public void OptionsCarryBothTheStoredNameAndTheLabel()
    {
        CompanySizeOption small = TenantSetupOptions.Current.CompanySizes
            .Single(option => option.Id == nameof(CompanySize.Small));

        Assert.Equal("10–50", small.Label);
    }

    [Fact]
    public void TheHyphenatedFormIsStillRejected()
    {
        Assert.False(_validator.Validate(With("10-50")).IsValid);
    }
}
