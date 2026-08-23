using Motee.Domain.Tenants;

namespace Motee.Tests.Tenancy;

public class CompanyEmailDomainTests
{
    // Format only. Which domain a tenant uses is their business — a consultancy on a
    // free provider is as valid a customer as one with its own domain.
    [Theory]
    [InlineData("acme.com", true)]
    [InlineData("sahelfintech.ng", true)]
    [InlineData("northwind.co.uk", true)]
    [InlineData("sub.acme.com", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("acme", false)]
    [InlineData("acme .com", false)]
    [InlineData("http://acme.com", false)]
    [InlineData("ada@acme.com", false)]
    [InlineData("-acme.com", false)]
    [InlineData("acme-.com", false)]
    public void AcceptsAnyWellFormedDomain(string? domain, bool expected)
    {
        Assert.Equal(expected, CompanyEmailDomain.IsValid(domain));
    }

    // Previously refused as "not a real company domain". The platform no longer
    // makes that judgement.
    [Theory]
    [InlineData("gmail.com")]
    [InlineData("yahoo.co.uk")]
    [InlineData("outlook.com")]
    [InlineData("proton.me")]
    public void DoesNotSecondGuessFreeProviders(string domain)
    {
        Assert.True(CompanyEmailDomain.IsValid(domain));
    }

    [Fact]
    public void IgnoresSurroundingSpace()
    {
        Assert.True(CompanyEmailDomain.IsValid("  acme.com  "));
    }
}
