using Motee.Domain.Settings;

namespace Motee.Application.Settings;

// Which parts of the product this tenant has switched on.
//
// Its own service rather than a line in each caller, because there are two
// callers that must not disagree: the permission check, which decides whether
// an endpoint answers, and /auth/me, which decides what the sidebar offers. If
// those two part company the sidebar shows a link to something that returns
// 403, which looks like a permissions bug and is not one.
//
// Scoped, and resolved once per request. The tenant row is a primary-key read,
// but doing it per permission check would be one read per endpoint attribute.
public interface IEnabledFeatures
{
    Task<FeatureState> CurrentAsync(CancellationToken cancellationToken = default);

    // Whether a permission module key should answer at all for this tenant.
    //
    // Distinct from "is the caller allowed": a module switched off is not a
    // permission the caller is missing, it is a part of the product this company
    // does not have. The owner bypasses permissions and does not bypass this.
    Task<bool> AllowsAsync(string moduleKey, CancellationToken cancellationToken = default);
}
