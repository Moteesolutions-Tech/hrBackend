using Microsoft.AspNetCore.Authorization;
using Motee.Domain.Authorization;

namespace Motee.Api.Authorization;

internal sealed class PermissionRequirement(
    string module,
    PermissionAction action,
    DataScopeKind minimumScope = DataScopeKind.None)
    : IAuthorizationRequirement
{
    public string Module { get; } = module;

    public PermissionAction Action { get; } = action;

    // The breadth the endpoint needs on top of holding the permission. None means it
    // asks for nothing beyond the grant itself, which is the ordinary case. See
    // RequiresPermissionAttribute.MinimumScope for why this exists.
    public DataScopeKind MinimumScope { get; } = minimumScope;
}
