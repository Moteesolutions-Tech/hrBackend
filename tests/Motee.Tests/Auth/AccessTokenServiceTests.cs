using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Motee.Application.Auth;
using Motee.Domain.Identity;
using Motee.Infrastructure.Auth;

namespace Motee.Tests.Auth;

public class AccessTokenServiceTests
{
    private const string Key = "a-development-signing-key-long-enough-for-hmac-sha256";

    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static TokenSubject Subject() => new()
    {
        UserId = UserId,
        Email = "ada@acme.com",
        TenantId = TenantId,
        EmployeeId = null,
        Role = Roles.ToSlug(Role.HrAdmin),
        IsPlatformStaff = false,
    };

    private static AccessTokenService Build(string? key = Key, int minutes = 15)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:SigningKey"] = key,
                ["JwtSettings:AccessTokenMinutes"] = minutes.ToString(),
            })
            .Build();

        return new AccessTokenService(configuration, TimeProvider.System);
    }

    private static async Task<TokenValidationResult> ValidateAsync(string token, string key)
    {
        return await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidateIssuer = false,
            ValidateAudience = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
        });
    }

    [Fact]
    public async Task IssuesATokenThatValidatesWithTheConfiguredKey()
    {
        AccessToken token = Build().Issue(Subject());

        TokenValidationResult result = await ValidateAsync(token.Value, Key);

        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Fact]
    public async Task CarriesTheSubjectClaims()
    {
        AccessToken token = Build().Issue(Subject());

        TokenValidationResult result = await ValidateAsync(token.Value, Key);

        Assert.Equal(UserId.ToString(), result.Claims[MoteeClaimTypes.Subject]);
        Assert.Equal(TenantId.ToString(), result.Claims[MoteeClaimTypes.TenantId]);
        Assert.Equal(Roles.ToSlug(Role.HrAdmin), result.Claims[MoteeClaimTypes.Role]);
        Assert.Equal("ada@acme.com", result.Claims[MoteeClaimTypes.Email]);
    }

    // A token signed with another key must never validate, or anyone able to guess
    // a key could mint tenant access.
    [Fact]
    public async Task DoesNotValidateAgainstADifferentKey()
    {
        AccessToken token = Build().Issue(Subject());

        TokenValidationResult result =
            await ValidateAsync(token.Value, "a-completely-different-key-of-sufficient-length");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ReportsAnExpiryMatchingConfiguration()
    {
        AccessToken token = Build(minutes: 30).Issue(Subject());

        TimeSpan lifetime = token.ExpiresAt - DateTimeOffset.UtcNow;

        Assert.InRange(lifetime.TotalMinutes, 29, 30.5);
    }

    [Fact]
    public void EachTokenHasItsOwnIdentifier()
    {
        AccessTokenService service = Build();

        Assert.NotEqual(service.Issue(Subject()).Value, service.Issue(Subject()).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesToIssueWithoutASigningKey(string? key)
    {
        Assert.Throws<InvalidOperationException>(() => Build(key).Issue(Subject()));
    }

    // HMAC-SHA256 needs at least 256 bits. A shorter key is rejected deep inside the
    // token handler with an opaque message, so it is caught here instead.
    [Fact]
    public void RefusesASigningKeyShorterThan256Bits()
    {
        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => Build("too-short").Issue(Subject()));

        Assert.Contains("32", error.Message, StringComparison.Ordinal);
    }

    // TokenClaimsBuilder guards this, and issuing must not bypass it.
    [Fact]
    public void RefusesATenantUserWithoutATenant()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Build().Issue(Subject() with { TenantId = null }));
    }
}
