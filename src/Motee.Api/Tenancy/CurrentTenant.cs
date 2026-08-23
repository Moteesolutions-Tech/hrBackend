using Motee.Application.Auth;
using Motee.Application.Tenancy;

namespace Motee.Api.Tenancy;

internal sealed class CurrentTenant(IHttpContextAccessor httpContextAccessor) : ICurrentTenant
{
    public Guid? TenantId
    {
        get
        {
            string? claim = httpContextAccessor.HttpContext?
                .User.FindFirst(MoteeClaimTypes.TenantId)?.Value;

            if (Guid.TryParse(claim, out Guid tenantId))
            {
                return tenantId;
            }

            // No claim means no signed-in caller: a Hangfire job, or a joiner
            // accepting an invitation before they have an account. Those set the
            // tenant they are working for explicitly. The claim still wins, so an
            // ambient value can never widen what a real request may see.
            return AmbientTenant.TenantId;
        }
    }
}
