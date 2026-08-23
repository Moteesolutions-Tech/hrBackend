using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Motee.Application.Auth;

namespace Motee.Infrastructure.Auth;

public sealed class AccessTokenService(IConfiguration configuration, TimeProvider timeProvider)
    : IAccessTokenService
{
    private const int MinimumKeyBytes = 32;
    private const int DefaultLifetimeMinutes = 15;

    public AccessToken Issue(TokenSubject subject)
    {
        SymmetricSecurityKey key = ReadSigningKey();

        // Throws for a tenant user with no tenant, or platform staff carrying one.
        IReadOnlyList<Claim> claims = TokenClaimsBuilder.Build(subject);

        DateTimeOffset issuedAt = timeProvider.GetUtcNow();
        DateTimeOffset expiresAt = issuedAt.AddMinutes(LifetimeMinutes());

        SecurityTokenDescriptor descriptor = new()
        {
            Subject = new ClaimsIdentity(claims),
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };

        return new AccessToken
        {
            Value = new JsonWebTokenHandler().CreateToken(descriptor),
            ExpiresAt = expiresAt,
        };
    }

    private SymmetricSecurityKey ReadSigningKey()
    {
        string? configured = configuration["JwtSettings:SigningKey"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "JwtSettings:SigningKey is not configured; access tokens cannot be issued.");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(configured);

        // HMAC-SHA256 requires a 256-bit key. The handler rejects shorter ones with
        // an opaque message, so it is caught here with a usable one.
        if (bytes.Length < MinimumKeyBytes)
        {
            throw new InvalidOperationException(
                $"JwtSettings:SigningKey must be at least {MinimumKeyBytes} bytes for HMAC-SHA256; "
                + $"the configured key is {bytes.Length}.");
        }

        return new SymmetricSecurityKey(bytes);
    }

    private int LifetimeMinutes() =>
        configuration.GetValue<int?>("JwtSettings:AccessTokenMinutes") ?? DefaultLifetimeMinutes;
}
