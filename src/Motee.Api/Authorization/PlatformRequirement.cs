using Microsoft.AspNetCore.Authorization;
using Motee.Application.Auth;
using Motee.Domain.Platform;

namespace Motee.Api.Authorization;

internal sealed record PlatformRequirement(string Permission, bool Write) : IAuthorizationRequirement;

// Reads the claims and nothing else. No database call, deliberately: a platform role is
// fixed at sign-in like the tenant claim beside it, and a lookup per request would be a
// query on every platform endpoint to answer something the token already states.
//
// The cost is that demoting somebody takes effect at their next token refresh rather than
// instantly. That is the same tradeoff the tenant claim already makes, and revoking the
// refresh token is the way to end a session now.
internal sealed class PlatformAuthorizationHandler
    : AuthorizationHandler<PlatformRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformRequirement requirement)
    {
        // Both checked, not just the role. A token carrying a role without the flag should
        // never exist — TokenClaimsBuilder refuses to mint one — but this is the gate that
        // would be exploited if one ever did.
        if (context.User.FindFirst(MoteeClaimTypes.IsPlatformStaff)?.Value != "true")
        {
            return Task.CompletedTask;
        }

        string? claim = context.User.FindFirst(MoteeClaimTypes.PlatformRole)?.Value;

        // Numeric input is refused for the same reason the tenant role parser refuses it:
        // "2" must not resolve to Admin because the enum happens to order that way.
        if (string.IsNullOrWhiteSpace(claim)
            || int.TryParse(claim, out _)
            || !Enum.TryParse(claim, ignoreCase: true, out PlatformRole role)
            || !Enum.IsDefined(role))
        {
            return Task.CompletedTask;
        }

        if (PlatformPermissions.Allows(role, requirement.Permission, requirement.Write))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
