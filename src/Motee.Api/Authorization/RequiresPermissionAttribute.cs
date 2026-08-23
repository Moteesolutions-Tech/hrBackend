using Microsoft.AspNetCore.Authorization;
using Motee.Domain.Authorization;

namespace Motee.Api.Authorization;

// Extends AuthorizeAttribute rather than acting as a filter, so the framework
// still distinguishes "not signed in" (401) from "signed in but not allowed" (403).
// A bare IActionFilter, as wallet-service uses, returns 403 to anonymous callers too.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequiresPermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "motee.permission:";

    public RequiresPermissionAttribute(string module, PermissionAction action)
    {
        Module = module;
        Action = action;
        Policy = $"{PolicyPrefix}{module}:{action}";
    }

    public string Module { get; }

    public PermissionAction Action { get; }

    public static bool TryParse(string policyName, out string module, out PermissionAction action)
    {
        module = string.Empty;
        action = default;

        if (!policyName.StartsWith(PolicyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = policyName[PolicyPrefix.Length..].Split(':');

        if (parts.Length != 2 || !Enum.TryParse(parts[1], ignoreCase: true, out action))
        {
            return false;
        }

        module = parts[0];

        return !string.IsNullOrWhiteSpace(module);
    }
}
