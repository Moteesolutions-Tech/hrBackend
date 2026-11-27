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

    private DataScopeKind _minimumScope = DataScopeKind.None;

    public RequiresPermissionAttribute(string module, PermissionAction action)
    {
        Module = module;
        Action = action;
        Policy = Name(module, action, DataScopeKind.None);
    }

    public string Module { get; }

    public PermissionAction Action { get; }

    // How much breadth the endpoint needs, beyond holding the permission at all.
    //
    // This exists because holding a module is not the same as being allowed to read
    // every record in it, and the gap between those two is where an entire class of
    // leak lived. SelfServicePolicy grants View at Self scope on the person-centric
    // modules to every signed-in employee, with no access level required — that is what
    // makes self-service work. The handler succeeds on any scope above None, so a Self
    // grant satisfies a bare [RequiresPermission] and the endpoint is then expected to
    // narrow its own query. An organisation-wide list that forgets to narrow hands the
    // whole company to anybody who can log in.
    //
    // So the organisation-wide lists state the breadth they need. DataScopeKind is
    // ordered None < Self < DirectReports < ... < All, and the requirement is "at least
    // this", so MinimumScope = DirectReports means "more than just your own records" —
    // each of those modules has its own /mine route for that.
    //
    // Set as a named argument, which runs after the constructor, so the policy name is
    // rewritten to carry it.
    public DataScopeKind MinimumScope
    {
        get => _minimumScope;
        set
        {
            _minimumScope = value;
            Policy = Name(Module, Action, value);
        }
    }

    public static bool TryParse(
        string policyName,
        out string module,
        out PermissionAction action,
        out DataScopeKind minimumScope)
    {
        module = string.Empty;
        action = default;
        minimumScope = DataScopeKind.None;

        if (!policyName.StartsWith(PolicyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = policyName[PolicyPrefix.Length..].Split(':');

        if (parts.Length != 3
            || !Enum.TryParse(parts[1], ignoreCase: true, out action)
            || !Enum.TryParse(parts[2], ignoreCase: true, out minimumScope))
        {
            return false;
        }

        module = parts[0];

        return !string.IsNullOrWhiteSpace(module);
    }

    private static string Name(string module, PermissionAction action, DataScopeKind minimumScope) =>
        $"{PolicyPrefix}{module}:{action}:{minimumScope}";
}
