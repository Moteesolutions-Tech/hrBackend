using FluentValidation.Results;
using Motee.Application.Auth;

namespace Motee.Tests.Auth;

public class RegisterTenantValidatorTests
{
    private readonly RegisterTenantValidator _validator = new();

    private static RegisterTenantRequest Valid() => new()
    {
        FirstName = "Ada",
        MiddleName = null,
        LastName = "Okafor",
        Email = "ada@acme.com",
        CompanyName = "Acme Corporation",
        Password = "correct-horse",
        CountryCode = "NG",
    };

    private bool IsValid(RegisterTenantRequest request) => _validator.Validate(request).IsValid;

    private string[] ErrorsFor(RegisterTenantRequest request, string property) =>
        _validator.Validate(request).Errors
            .Where(error => error.PropertyName == property)
            .Select(error => error.ErrorMessage)
            .ToArray();

    [Fact]
    public void AcceptsACompleteRequest()
    {
        Assert.True(IsValid(Valid()));
    }

    [Fact]
    public void MiddleNameIsOptional()
    {
        Assert.True(IsValid(Valid() with { MiddleName = null }));
        Assert.True(IsValid(Valid() with { MiddleName = "A." }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RequiresFirstName(string? firstName)
    {
        Assert.NotEmpty(ErrorsFor(Valid() with { FirstName = firstName! }, nameof(RegisterTenantRequest.FirstName)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RequiresLastName(string? lastName)
    {
        Assert.NotEmpty(ErrorsFor(Valid() with { LastName = lastName! }, nameof(RegisterTenantRequest.LastName)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("missing@domain")]
    [InlineData("@nolocal.com")]
    [InlineData("spaces in@email.com")]
    public void RejectsMalformedEmail(string email)
    {
        Assert.NotEmpty(ErrorsFor(Valid() with { Email = email }, nameof(RegisterTenantRequest.Email)));
    }

    [Theory]
    [InlineData("ada@acme.com")]
    [InlineData("ada.okafor+hr@acme.co.uk")]
    public void AcceptsWellFormedEmail(string email)
    {
        Assert.Empty(ErrorsFor(Valid() with { Email = email }, nameof(RegisterTenantRequest.Email)));
    }

    // The sign-up form states "Min. 8 chars".
    [Theory]
    [InlineData("short")]
    [InlineData("1234567")]
    public void RejectsPasswordUnderEightCharacters(string password)
    {
        Assert.NotEmpty(ErrorsFor(Valid() with { Password = password }, nameof(RegisterTenantRequest.Password)));
    }

    [Fact]
    public void AcceptsPasswordOfExactlyEightCharacters()
    {
        Assert.Empty(ErrorsFor(Valid() with { Password = "12345678" }, nameof(RegisterTenantRequest.Password)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RequiresCompanyName(string companyName)
    {
        Assert.NotEmpty(ErrorsFor(Valid() with { CompanyName = companyName }, nameof(RegisterTenantRequest.CompanyName)));
    }

    [Theory]
    [InlineData("NG")]
    [InlineData("GB")]
    [InlineData("ng")]
    [InlineData("uk")]
    public void AcceptsSupportedCountries(string countryCode)
    {
        Assert.Empty(ErrorsFor(Valid() with { CountryCode = countryCode }, nameof(RegisterTenantRequest.CountryCode)));
    }

    // A visitor detected in an unsupported country can still reach the form; the
    // toggle must not be able to submit a jurisdiction Motee cannot operate in.
    [Theory]
    [InlineData("GH")]
    [InlineData("US")]
    [InlineData("")]
    [InlineData("XX")]
    public void RejectsUnsupportedCountries(string countryCode)
    {
        Assert.NotEmpty(ErrorsFor(Valid() with { CountryCode = countryCode }, nameof(RegisterTenantRequest.CountryCode)));
    }

    [Fact]
    public void ReportsEveryProblemAtOnceRatherThanStoppingAtTheFirst()
    {
        ValidationResult result = _validator.Validate(new RegisterTenantRequest
        {
            FirstName = "",
            LastName = "",
            Email = "nope",
            CompanyName = "",
            Password = "x",
            CountryCode = "ZZ",
        });

        Assert.True(result.Errors.Count >= 6, $"expected every field reported, got {result.Errors.Count}");
    }
}
