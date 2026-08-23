namespace Motee.Domain.Authorization;

// Self-service is a rule, not an access level. Of ~5,000 users, almost all need
// exactly one thing: their own record. Modelling that as a matrix row would make
// every employee a permissions object somebody has to maintain, and the nearest
// existing level (Read-only) grants view across employees, contracts and
// grievances — an organisation-wide leak.
//
// So this grants Self scope on person-centric modules to any authenticated
// employee, independent of whether they hold an access level at all.
public static class SelfServicePolicy
{
    // Deliberately no Delete, Approve or Export. Nobody approves their own request,
    // deletes their own employment record, or exports a dataset from self-service.
    private static readonly IReadOnlyDictionary<string, PermissionAction[]> Capabilities =
        new Dictionary<string, PermissionAction[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["organization.employees"] = [PermissionAction.View, PermissionAction.Edit],
            ["time-payroll.leave"] = [PermissionAction.View, PermissionAction.Create],
            ["time-payroll.attendance"] = [PermissionAction.View],
            ["talent.performance"] = [PermissionAction.View],
            ["talent.training"] = [PermissionAction.View],
            ["operations.documents"] = [PermissionAction.View],
            ["operations.contracts"] = [PermissionAction.View],
            ["operations.assets"] = [PermissionAction.View],
            ["submissions.queue"] = [PermissionAction.View, PermissionAction.Create],
        };

    public static bool Grants(string module, PermissionAction action) =>
        !string.IsNullOrWhiteSpace(module)
        && Capabilities.TryGetValue(module, out PermissionAction[]? actions)
        && actions.Contains(action);

}
