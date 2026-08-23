namespace Motee.Domain.Authorization;

// Fails closed. The frontend's useCan hook deliberately fails open so pre-login UI
// still renders; this is the enforcement point and must do the opposite. Anything
// unknown — no access level, an unlisted module, an unlisted action, no scope — is
// a denial.
public static class PermissionEvaluator
{
    // Returns reach rather than a boolean on purpose. A caller that only asks
    // "allowed?" gives someone scoped to their own team organisation-wide access.
    public static DataScope ScopeFor(
        AccessLevelPermissions? accessLevel,
        string module,
        PermissionAction action)
    {
        if (accessLevel is null || string.IsNullOrWhiteSpace(module))
        {
            return DataScope.Nothing;
        }

        ModulePermission? permission = accessLevel.Modules
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Module, module, StringComparison.OrdinalIgnoreCase));

        if (permission is not { Access: true } || !permission.Actions.Contains(action))
        {
            return DataScope.Nothing;
        }

        // The level's scope, not the module's. A module the level can open is reached
        // as far as the level reaches — and a scope that names no departments reaches
        // nothing, which is a denial rather than an oversight.
        return accessLevel.Scope.ReachesNothing ? DataScope.Nothing : accessLevel.Scope;
    }
}
