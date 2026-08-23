using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authorization;
using Motee.Api.Authorization;
using Motee.Application.Auth;

namespace Motee.Api.Auth;

internal static class AuthenticationSetup
{
    // Registered so [Authorize] challenges with 401 rather than throwing. Token
    // issuance and Identity land in the auth phase; until a signing key is
    // configured every presented token simply fails validation.
    public static IServiceCollection AddMoteeAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string? signingKey = configuration["JwtSettings:SigningKey"];

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Left on, the handler rewrites "sub" and "role" to WS-Federation
                // schema URIs, so claims read back under names nothing emitted.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    NameClaimType = MoteeClaimTypes.Subject,
                    RoleClaimType = MoteeClaimTypes.Role,
                    IssuerSigningKey = string.IsNullOrWhiteSpace(signingKey)
                        ? null
                        : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                };
            });

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        return services;
    }
}
