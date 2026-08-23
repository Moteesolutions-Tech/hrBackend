using Microsoft.AspNetCore.Authorization;
using Motee.Domain.Authorization;

namespace Motee.Api.Authorization;

internal sealed class PermissionRequirement(string module, PermissionAction action)
    : IAuthorizationRequirement
{
    public string Module { get; } = module;

    public PermissionAction Action { get; } = action;
}
