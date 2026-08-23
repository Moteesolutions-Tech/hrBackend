namespace Motee.Domain.Authorization;

// The single entry point callers should use. Combines what the held access levels
// grant with what self-service grants, and returns the broader of the two — so an
// employee with no level assigned still reaches their own record, while an HR Admin
// is not narrowed to it.
//
// The self-service floor is deliberate. Access levels are tenant-owned now, and a
// tenant that deletes or deactivates the wrong one would otherwise lock its whole
// workforce out of their own profiles.
public static class AccessDecision
{
    public static DataScope Resolve(
        AccessLevelPermissions? accessLevel,
        string module,
        PermissionAction action)
    {
        DataScope granted = PermissionEvaluator.ScopeFor(accessLevel, module, action);

        if (!SelfServicePolicy.Grants(module, action))
        {
            return granted;
        }

        // Broader of the two. Comparing by kind is enough: Self is the floor, and any
        // named scope or All is at least as broad.
        return granted.Kind > DataScopeKind.Self
            ? granted
            : new DataScope { Kind = DataScopeKind.Self };
    }
}
