using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Domain.Organisation;

namespace Motee.Tests.Organisation;

public class EmploymentTypeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void CoversTheFrontendTaxonomy()
    {
        Assert.Equal(11, Enum.GetValues<EmploymentType>().Length);
    }

    [Theory]
    [InlineData(EmploymentType.FullTime, "\"fullTime\"")]
    [InlineData(EmploymentType.FieldBased, "\"fieldBased\"")]
    [InlineData(EmploymentType.Contract, "\"contract\"")]
    public void SerialisesAsCamelCase(EmploymentType type, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(type, Web));
    }

    [Fact]
    public void EveryValueSurvivesARoundTrip()
    {
        Assert.All(Enum.GetValues<EmploymentType>(), type =>
        {
            string json = JsonSerializer.Serialize(type, Web);

            Assert.Equal(type, JsonSerializer.Deserialize<EmploymentType>(json, Web));
            Assert.True(EmploymentTypes.TryParse(json.Trim('"'), out EmploymentType parsed));
            Assert.Equal(type, parsed);
        });
    }

    [Theory]
    [InlineData("fullTime", EmploymentType.FullTime)]
    [InlineData("FullTime", EmploymentType.FullTime)]
    [InlineData("  contract  ", EmploymentType.Contract)]
    public void AcceptsTheValueThisApiEmits(string input, EmploymentType expected)
    {
        Assert.True(EmploymentTypes.TryParse(input, out EmploymentType parsed));
        Assert.Equal(expected, parsed);
    }

    // The frontend's own spelling is not a second accepted form. It sends what this
    // API emits, so full_time is simply not a value.
    [Theory]
    [InlineData("full_time")]
    [InlineData("field_based")]
    [InlineData("part_time")]
    public void DoesNotAcceptTheFrontendsSnakeCaseSpelling(string input)
    {
        Assert.False(EmploymentTypes.TryParse(input, out _));
    }

    // employmentTypeFromName() in the frontend silently falls back to full_time.
    // Recording an employment type nobody chose is worse than refusing the input.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("permanent")]
    [InlineData("NYSC")]
    [InlineData("0")]
    [InlineData("3")]
    public void RefusesRatherThanGuessing(string? input)
    {
        Assert.False(EmploymentTypes.TryParse(input, out _));
    }
}
