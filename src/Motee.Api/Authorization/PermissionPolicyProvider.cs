using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Motee.Domain.Authorization;

namespace Motee.Api.Authorization;

// Policies are per module+action, so there are 240 possible combinations. Rather
// than registering them all up front, they are built on demand from the name that
// RequiresPermissionAttribute encoded.
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!RequiresPermissionAttribute.TryParse(policyName, out string module, out PermissionAction action))
        {
            return _fallback.GetPolicyAsync(policyName);
        }

        AuthorizationPolicy policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(module, action))
            .Build();

        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}
