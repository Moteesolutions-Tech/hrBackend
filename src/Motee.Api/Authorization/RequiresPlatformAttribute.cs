using Microsoft.AspNetCore.Authorization;
using Motee.Domain.Platform;

namespace Motee.Api.Authorization;

// Guards an endpoint that acts on companies rather than inside one.
//
// Separate from RequiresPermission rather than another module key in it, because the two
// read entirely different things: that one evaluates a tenant's own access levels, which
// platform staff do not have and must never be given. Sharing the attribute would mean
// one evaluator deciding both, and the first bug in it would be a tenant user reaching a
// platform endpoint.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequiresPlatformAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "motee.platform:";

    public RequiresPlatformAttribute(string permission, bool write = false)
    {
        Permission = permission;
        Write = write;
        Policy = $"{PolicyPrefix}{permission}:{(write ? "write" : "read")}";
    }

    public string Permission { get; }

    public bool Write { get; }

    public static bool TryParse(string policyName, out string permission, out bool write)
    {
        permission = string.Empty;
        write = false;

        if (!policyName.StartsWith(PolicyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = policyName[PolicyPrefix.Length..].Split(':');

        if (parts.Length != 2)
        {
            return false;
        }

        permission = parts[0];
        write = parts[1] == "write";

        // An unrecognised permission fails rather than defaulting to something. A typo in
        // an attribute should close the endpoint, not open it against a name nothing checks.
        return PlatformPermissions.All.Contains(permission);
    }
}
