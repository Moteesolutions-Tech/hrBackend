using Motee.Domain.Common;

namespace Motee.Tests;

public class CountryCodeTests
{
    [Theory]
    [InlineData("NG")]
    [InlineData("ng")]
    [InlineData(" ng ")]
    public void ParsesNigeria(string input)
    {
        Assert.Equal(CountryCode.Nigeria, CountryCode.Parse(input));
    }

    [Theory]
    [InlineData("GB")]
    [InlineData("gb")]
    public void ParsesUnitedKingdom(string input)
    {
        Assert.Equal(CountryCode.UnitedKingdom, CountryCode.Parse(input));
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("UK")]
    public void MapsFrontendUkKeyToIsoGb(string input)
    {
        CountryCode code = CountryCode.Parse(input);

        Assert.Equal(CountryCode.UnitedKingdom, code);
        Assert.Equal("GB", code.Value);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsUnsupportedCodes(string? input)
    {
        Assert.Throws<ArgumentException>(() => CountryCode.Parse(input));
    }
}
