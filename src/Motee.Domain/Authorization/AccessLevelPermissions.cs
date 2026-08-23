namespace Motee.Domain.Authorization;

public sealed record ModulePermission
{
    // Module id from the shared catalogue, e.g. "organization.employees".
    public required string Module { get; init; }

    // Hard off switch. False denies the module regardless of Actions.
    public required bool Access { get; init; }

    public required IReadOnlyCollection<PermissionAction> Actions { get; init; }
}

// The permissions half of an access level. Scope lives on the level, not here: it is
// one answer for the whole level, which is what makes intersecting scopes across
// several levels a well-defined operation.
public sealed record AccessLevelPermissions
{
    public static readonly AccessLevelPermissions Nothing = new()
    {
        Modules = [],
        Scope = DataScope.Nothing,
    };

    public required IReadOnlyList<ModulePermission> Modules { get; init; }

    public required DataScope Scope { get; init; }

    // Someone holding several levels gets the union of what each permits, reaching
    // only as far as the narrowest of them allows.
    //
    // Union on permissions, intersect on scope. Unioning both would mean adding a
    // narrow level to a broad one widens nothing — fine — while adding a broad level
    // to a narrow one silently widens everything the narrow level could already do.
    // That is how "also make them a Recruiter" ends in organisation-wide access to
    // salaries.
    public static AccessLevelPermissions Merge(IEnumerable<AccessLevelPermissions> levels)
    {
        List<AccessLevelPermissions> held = [.. levels];

        if (held.Count == 0)
        {
            return Nothing;
        }

        if (held.Count == 1)
        {
            return held[0];
        }

        Dictionary<string, HashSet<PermissionAction>> unioned = [];

        foreach (AccessLevelPermissions level in held)
        {
            foreach (ModulePermission module in level.Modules)
            {
                // Access false is that level declining the module, not vetoing it for
                // the others. A veto would make adding a level able to take access
                // away, which nobody expects from granting something.
                if (!module.Access)
                {
                    continue;
                }

                if (!unioned.TryGetValue(module.Module, out HashSet<PermissionAction>? actions))
                {
                    actions = [];
                    unioned[module.Module] = actions;
                }

                actions.UnionWith(module.Actions);
            }
        }

        DataScope scope = held
            .Select(level => level.Scope)
            .Aggregate(DataScope.Intersect);

        return new AccessLevelPermissions
        {
            Modules =
            [
                .. unioned
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new ModulePermission
                    {
                        Module = entry.Key,
                        Access = true,
                        Actions = ActionDependencies.Expand(entry.Value),
                    }),
            ],
            Scope = scope,
        };
    }

    // Which of the held levels granted a given action. The client objected to a
    // silent merge: when someone can delete records, this is what names the level
    // responsible rather than leaving an admin to work it out from the matrix.
    public static IReadOnlyList<string> WhichGrant(
        IEnumerable<(string Name, AccessLevelPermissions Permissions)> held,
        string module,
        PermissionAction action) =>
        [.. held
            .Where(level => level.Permissions.Modules.Any(permission =>
                permission.Access
                && string.Equals(permission.Module, module, StringComparison.OrdinalIgnoreCase)
                && permission.Actions.Contains(action)))
            .Select(level => level.Name)];
}
