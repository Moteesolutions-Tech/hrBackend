namespace Motee.Domain.Authorization;

public enum PermissionAction
{
    View,
    Create,
    Edit,
    Delete,
    Export,
    Approve,

    // Configure the module itself — its settings, templates and workflow rules.
    // Distinct from Edit, which changes records inside the module.
    Administer,
}

// Some actions are meaningless without others. An approver who cannot see the queue
// cannot approve anything, so granting the dependent action grants what it rests on.
//
// Enforced in the domain rather than only in the form that builds a level: a
// permission set arriving through the API would otherwise describe a state the UI
// treats as impossible, and it would surface as an approver staring at an empty
// screen with no rule to point at.
public static class ActionDependencies
{
    private static readonly Dictionary<PermissionAction, PermissionAction[]> Requires = new()
    {
        [PermissionAction.Create] = [PermissionAction.View],
        [PermissionAction.Edit] = [PermissionAction.View],
        [PermissionAction.Delete] = [PermissionAction.View],
        [PermissionAction.Export] = [PermissionAction.View],
        [PermissionAction.Approve] = [PermissionAction.View],
        [PermissionAction.Administer] = [PermissionAction.View, PermissionAction.Edit],
    };

    // Grows the set to include everything the chosen actions rest on. Applied when a
    // level is saved, so what is stored is what is granted — expanding at read time
    // instead would mean an audit of the stored row could not explain the access.
    public static IReadOnlyList<PermissionAction> Expand(IEnumerable<PermissionAction> actions)
    {
        HashSet<PermissionAction> expanded = [.. actions];

        // Administer pulls in Edit, which pulls in View, so a single pass over the
        // original set would stop one short.
        bool added;

        do
        {
            added = false;

            foreach (PermissionAction action in expanded.ToArray())
            {
                if (!Requires.TryGetValue(action, out PermissionAction[]? needed))
                {
                    continue;
                }

                foreach (PermissionAction dependency in needed)
                {
                    added |= expanded.Add(dependency);
                }
            }
        }
        while (added);

        // Ordered so a stored set is stable. Otherwise two identical levels serialise
        // differently and every save reads as a change in the audit trail.
        return [.. expanded.Order()];
    }

    // Which actions stop working if this one is removed. The editor warns with this
    // before someone unticks View and silently takes five other grants with it.
    public static IReadOnlyList<PermissionAction> DependentsOf(PermissionAction action) =>
        [.. Requires
            .Where(entry => entry.Value.Contains(action))
            .Select(entry => entry.Key)
            .Order()];
}
