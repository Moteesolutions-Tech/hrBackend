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
        EnabledModules = ["employee-management"],
    };

    // The guard that was supposed to catch this and did not. It was named for the wizard
    // and asserted two members while the wizard offered three, so it pinned the
    // misalignment it existed to prevent — a backend rejecting a choice its own interface
    // presented, with a validation message naming the other two.
    [Fact]
    public void HasExactlyTheShapesTheWizardOffers()
    {
        Assert.Equal(
            [StructureType.Hierarchical, StructureType.Flat, StructureType.Matrix],
            Enum.GetValues<StructureType>());
    }

    // The wire format stays lowercase, so nothing the frontend already reads changes.
    [Theory]
    [InlineData(StructureType.Hierarchical, "\"hierarchical\"")]
    [InlineData(StructureType.Flat, "\"flat\"")]
    [InlineData(StructureType.Matrix, "\"matrix\"")]
    public void SerialisesToTheSameStringsAsBefore(StructureType value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Web));
    }

    [Theory]
    [InlineData("hierarchical", StructureType.Hierarchical)]
    [InlineData("flat", StructureType.Flat)]
    [InlineData("Hierarchical", StructureType.Hierarchical)]
    [InlineData("FLAT", StructureType.Flat)]
    [InlineData("matrix", StructureType.Matrix)]
    [InlineData("Matrix", StructureType.Matrix)]
    public void ParsesWhateverCasingArrives(string input, StructureType expected)
    {
        Assert.True(StructureTypes.TryParse(input, out StructureType parsed));
        Assert.Equal(expected, parsed);
    }

    // "matrix" used to be here, which recorded that the enum lacked it rather than a
    // decision to refuse it — the setup wizard has offered Matrix as its third option all
    // along, so the backend was rejecting a choice its own interface presented.
    [Theory]
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
        string message = _validator.Validate(With("holacracy")).Errors
            .Single(error => error.PropertyName == "StructureType")
            .ErrorMessage;

        // Every permitted value, listed. The message is built from the enum, so adding a
        // shape cannot leave it describing a rule that no longer exists.
        foreach (string name in Enum.GetNames<StructureType>())
        {
            Assert.Contains(name, message, StringComparison.OrdinalIgnoreCase);
        }
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
        Assert.Equal(
            Enum.GetValues<StructureType>(),
            TenantSetupOptions.Current.StructureTypes.Select(option => option.Id));
    }

    // Matrix is the only structure that permits a second reporting line, and the wizard
    // reads that from here rather than recognising the string itself. A client that
    // hardcoded the rule would be keeping its own copy of it, which is how the enum came
    // to offer two values while the interface offered three.
    [Fact]
    public void OnlyMatrixPermitsASecondReportingLine()
    {
        Assert.Equal(
            [StructureType.Matrix],
            TenantSetupOptions.Current.StructureTypes
                .Where(option => option.AllowsSecondaryReporting)
                .Select(option => option.Id));
    }


    // The guard against the drift that started this. Every member has to be described,
    // so a new structure cannot be added without deciding what it permits.
    [Fact]
    public void EveryStructureDeclaresItsCapabilities()
    {
        Assert.Equal(
            Enum.GetValues<StructureType>().Length,
            TenantSetupOptions.Current.StructureTypes.Select(option => option.Id).Distinct().Count());

        Assert.All(
            TenantSetupOptions.Current.StructureTypes,
            option => Assert.False(string.IsNullOrWhiteSpace(option.Label)));
    }

    [Fact]
    public void TheDefaultIsHierarchical()
    {
        Assert.Equal(StructureType.Hierarchical, new TenantSettings().StructureType);
    }
}
