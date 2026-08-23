using Motee.Domain.Identity;

namespace Motee.Domain.Authorization;

public sealed record AccessLevelDefinition
{
    public required Role Role { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required IReadOnlyList<ModulePermission> Permissions { get; init; }
}

// Mirrors permissions/seeds.ts. Every tenant starts with these ten, and may then
// edit them or add its own from the access-levels screen.
public static class DefaultAccessLevels
{
    private static readonly PermissionAction[] Full =
    [
        PermissionAction.View,
        PermissionAction.Create,
        PermissionAction.Edit,
        PermissionAction.Delete,
        PermissionAction.Export,
        PermissionAction.Approve,
    ];

    private static readonly PermissionAction[] Management =
        [PermissionAction.View, PermissionAction.Create, PermissionAction.Edit, PermissionAction.Approve];

    private static readonly PermissionAction[] Staff =
        [PermissionAction.View, PermissionAction.Create, PermissionAction.Edit];

    private static readonly PermissionAction[] ViewOnly = [PermissionAction.View];

    private static readonly PermissionAction[] ViewExport =
        [PermissionAction.View, PermissionAction.Export];

    // No discard arm on purpose: adding a Role member raises CS8509 and breaks the
    // build until its actions are declared, rather than silently granting nothing.
    // CS8524 — the "someone cast an int" case — is the only reason a discard would
    // otherwise be required, and Roles.TryParse is the only way a Role is produced
    // from input.
#pragma warning disable CS8524
    public static IReadOnlyList<PermissionAction> ActionsFor(Role role) => role switch
    {
        Role.SuperAdmin or Role.HrAdmin => Full,
        Role.HrManager or Role.Finance => Management,
        Role.LineManager => [PermissionAction.View, PermissionAction.Edit, PermissionAction.Approve],
        Role.Executive => [PermissionAction.View, PermissionAction.Approve, PermissionAction.Export],
        Role.Recruiter or Role.ItAdmin => Staff,
        Role.Auditor => ViewExport,
        Role.ReadOnly => ViewOnly,
    };
#pragma warning restore CS8524

    // Modules holding records about a specific person. A Line Manager reaches these
    // for their direct reports only; everywhere else scope is organisation-wide.
    private static readonly HashSet<string> PersonScoped =
    [
        "organization.employees",
        "organization.structure",
        "organization.employee-checklist",
        "employee.medical",
        "employee.disciplinary",
        "employee.grievances",
        "employee.notes",
        "talent.performance",
        "talent.training",
        "talent.workforce-requests",
        "talent.offboarding",
        "time-payroll.attendance",
        "time-payroll.leave",
        "operations.documents",
        "operations.contracts",
        "submissions.queue",
    ];

    public static PermissionScope ScopeFor(Role role, string module) =>
        role == Role.LineManager && PersonScoped.Contains(module)
            ? PermissionScope.Team
            : PermissionScope.All;

    public static IReadOnlyList<ModulePermission> PermissionsFor(Role role)
    {
        IReadOnlyList<PermissionAction> actions = ActionsFor(role);

        return ModuleCatalogue.All
            .Select(module =>
            {
                bool granted = ModuleCatalogue.AccessByModule.TryGetValue(module, out IReadOnlyList<Role>? roles)
                    && roles.Contains(role);

                return new ModulePermission
                {
                    Module = module,
                    Access = granted,

                    // Expanded here so a template stores what it grants. Otherwise a
                    // template carrying Approve without View would produce a level
                    // that opens nothing, and the reason would not be in the row.
                    Actions = granted ? ActionDependencies.Expand(actions) : [],
                };
            })
            .ToList();
    }

    public static IReadOnlyList<AccessLevelDefinition> All =>
    [
        Define(Role.SuperAdmin, "Super Admin", "Full unrestricted access to every module and action"),
        Define(Role.HrAdmin, "HR Admin", "Manage all HR modules end-to-end"),
        Define(Role.HrManager, "HR Manager", "Run day-to-day people operations: employees, leave, performance, engagement"),
        Define(Role.Finance, "Finance", "Payroll, benefits, compensation and finance-side reporting"),
        Define(Role.LineManager, "Line Manager", "Manage direct reports: team view, leave approvals, performance reviews"),
        Define(Role.Executive, "Executive", "Executive oversight: approvals, headcount, workforce planning and reports"),
        Define(Role.Recruiter, "Recruiter", "Own the hiring pipeline and headcount planning"),
        Define(Role.ItAdmin, "IT Admin", "Manage assets, helpdesk, access levels and platform settings"),
        Define(Role.Auditor, "Auditor", "Read-only access across HR plus full audit trail visibility"),
        Define(Role.ReadOnly, "Read-only", "View-only access across the system, no Audit Trail or Settings"),
    ];

    private static AccessLevelDefinition Define(Role role, string name, string description) => new()
    {
        Role = role,
        Name = name,
        Description = description,
        Permissions = PermissionsFor(role),
    };
}
