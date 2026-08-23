using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Application.Tenancy;
using Motee.Domain.Tenants;

namespace Motee.Tests.Tenancy;

public class StructureTypeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly TenantSetupValidator _validator = new();

    private static TenantSetupRequest With(string structureType) => new()
    {
        Industry = "Technology",
        CompanySize = "1–10",
        ManagerTitle = "Line Manager",
        DepartmentLabel = "Department",
        StructureType = structureType,
        EnabledModules = [],
    };

    [Fact]
    public void HasExactlyTheTwoShapesTheWizardOffers()
    {
        Assert.Equal(
            [StructureType.Hierarchical, StructureType.Flat],
            Enum.GetValues<StructureType>());
    }

    // The wire format stays lowercase, so nothing the frontend already reads changes.
    [Theory]
    [InlineData(StructureType.Hierarchical, "\"hierarchical\"")]
    [InlineData(StructureType.Flat, "\"flat\"")]
    public void SerialisesToTheSameStringsAsBefore(StructureType value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Web));
    }

    [Theory]
    [InlineData("hierarchical", StructureType.Hierarchical)]
    [InlineData("flat", StructureType.Flat)]
    [InlineData("Hierarchical", StructureType.Hierarchical)]
    [InlineData("FLAT", StructureType.Flat)]
    public void ParsesWhateverCasingArrives(string input, StructureType expected)
    {
        Assert.True(StructureTypes.TryParse(input, out StructureType parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("matrix")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("1")]
    public void RefusesAnythingElse(string? input)
    {
        Assert.False(StructureTypes.TryParse(input, out _));
    }

    // Going through the validator rather than the deserialiser keeps the friendly
    // message; an enum-typed request property would fail as a raw ProblemDetails.
    [Fact]
    public void AnUnknownValueIsRejectedWithAReadableMessage()
    {
        string message = _validator.Validate(With("matrix")).Errors
            .Single(error => error.PropertyName == "StructureType")
            .ErrorMessage;

        Assert.Contains("hierarchical", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("flat", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("hierarchical")]
    [InlineData("flat")]
    public void TheValidatorAcceptsBoth(string structureType)
    {
        Assert.True(_validator.Validate(With(structureType)).IsValid);
    }

    [Fact]
    public void OptionsServeTheEnumNotFreeText()
    {
        Assert.Equal(Enum.GetValues<StructureType>(), TenantSetupOptions.Current.StructureTypes);
    }

    [Fact]
    public void TheDefaultIsHierarchical()
    {
        Assert.Equal(StructureType.Hierarchical, new TenantSettings().StructureType);
    }
}
