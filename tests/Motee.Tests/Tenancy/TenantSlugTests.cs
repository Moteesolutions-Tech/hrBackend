using Motee.Application.Tenancy;

namespace Motee.Tests.Tenancy;

public class TenantSlugTests
{
    [Theory]
    [InlineData("Acme Corporation", "acme-corporation")]
    [InlineData("Sahel Fintech", "sahel-fintech")]
    [InlineData("Northwind Logistics UK", "northwind-logistics-uk")]
    public void LowercasesAndHyphenatesWords(string companyName, string expected)
    {
        Assert.Equal(expected, TenantSlug.Normalise(companyName));
    }

    [Theory]
    [InlineData("Acme (Nigeria) Plc", "acme-nigeria-plc")]
    [InlineData("A+B Consulting", "a-b-consulting")]
    [InlineData("Sons, Ltd.", "sons-ltd")]
    public void PunctuationSeparatesWords(string companyName, string expected)
    {
        Assert.Equal(expected, TenantSlug.Normalise(companyName));
    }

    // O'Brien must not become o-brien.
    [Theory]
    [InlineData("O'Brien & Sons, Ltd.", "obrien-sons-ltd")]
    [InlineData("Nando’s", "nandos")]
    public void ApostrophesJoinRatherThanSeparate(string companyName, string expected)
    {
        Assert.Equal(expected, TenantSlug.Normalise(companyName));
    }

    [Theory]
    [InlineData("  Acme   Corporation  ", "acme-corporation")]
    [InlineData("Acme\tCorporation", "acme-corporation")]
    [InlineData("Acme--Corporation", "acme-corporation")]
    public void CollapsesWhitespaceAndRepeatedSeparators(string companyName, string expected)
    {
        Assert.Equal(expected, TenantSlug.Normalise(companyName));
    }

    [Theory]
    [InlineData("Café Ltd", "cafe-ltd")]
    [InlineData("Škoda Motors", "skoda-motors")]
    public void FoldsDiacriticsToAscii(string companyName, string expected)
    {
        Assert.Equal(expected, TenantSlug.Normalise(companyName));
    }

    [Fact]
    public void TruncatesToColumnLimitWithoutTrailingSeparator()
    {
        string companyName = string.Join(' ', Enumerable.Repeat("Logistics", 30));

        string slug = TenantSlug.Normalise(companyName);

        Assert.True(slug.Length <= TenantSlug.MaxLength, $"length was {slug.Length}");
        Assert.False(slug.EndsWith('-'), "slug should not end with a separator");
    }

    [Theory]
    [InlineData("株式会社")]
    [InlineData("!!!")]
    [InlineData("   ")]
    [InlineData("")]
    public void YieldsFallbackWhenNothingSurvivesNormalisation(string companyName)
    {
        string slug = TenantSlug.Normalise(companyName);

        Assert.False(string.IsNullOrWhiteSpace(slug));
        Assert.Matches("^[a-z0-9-]+$", slug);
    }

    [Fact]
    public void IsStableForTheSameInput()
    {
        Assert.Equal(TenantSlug.Normalise("Acme Corp"), TenantSlug.Normalise("Acme Corp"));
    }
}
