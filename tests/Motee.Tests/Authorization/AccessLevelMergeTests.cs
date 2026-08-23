using Motee.Domain.Authorization;

namespace Motee.Tests.Authorization;

// Someone holding several access levels gets the union of what each permits, reaching
// only as far as the narrowest allows. This is the rule with the most room to be
// quietly wrong, so it is pinned case by case.
public class AccessLevelMergeTests
{
    private const string Employees = "organization.employees";
    private const string Leave = "time-payroll.leave";

    private static readonly Guid Engineering = Guid.NewGuid();
    private static readonly Guid Finance = Guid.NewGuid();
    private static readonly Guid Sales = Guid.NewGuid();

    private static AccessLevelPermissions Level(
        DataScope scope,
        params ModulePermission[] modules) =>
        new() { Modules = modules, Scope = scope };

    private static ModulePermission Module(
        string module,
        bool access,
        params PermissionAction[] actions) =>
        new() { Module = module, Access = access, Actions = actions };

    private static IReadOnlyCollection<PermissionAction> ActionsFor(
        AccessLevelPermissions merged,
        string module) =>
        merged.Modules.SingleOrDefault(permission => permission.Module == module)?.Actions ?? [];

    // ---- scope ----

    [Fact]
    public void EverythingIsTheIdentityForIntersection()
    {
        DataScope team = new() { Kind = DataScopeKind.DirectReports };

        Assert.Equal(team, DataScope.Intersect(DataScope.Everything, team));
        Assert.Equal(team, DataScope.Intersect(team, DataScope.Everything));
    }

    // A level that reaches nothing drags the whole merge down with it. Anything else
    // would make holding an extra level able to widen access.
    [Fact]
    public void NothingSwallowsEverythingElse()
    {
        Assert.Equal(
            DataScopeKind.None,
            DataScope.Intersect(DataScope.Nothing, DataScope.Everything).Kind);
    }

    [Fact]
    public void TheNarrowerKindWinsWhenTwoDiffer()
    {
        DataScope self = new() { Kind = DataScopeKind.Self };
        DataScope reports = new() { Kind = DataScopeKind.DirectReports };

        Assert.Equal(DataScopeKind.Self, DataScope.Intersect(self, reports).Kind);
        Assert.Equal(DataScopeKind.Self, DataScope.Intersect(reports, self).Kind);
    }

    // Two department-scoped levels reach the departments they agree on, not the union
    // of them.
    [Fact]
    public void DepartmentsIntersectByList()
    {
        DataScope merged = DataScope.Intersect(
            DataScope.Departments(Engineering, Finance),
            DataScope.Departments(Finance, Sales));

        Assert.Equal(DataScopeKind.Department, merged.Kind);
        Assert.Equal([Finance], merged.DepartmentIds);
    }

    // Departments that overlap in nothing reach nothing. An empty allowlist is not an
    // open one, and the evaluator has to treat it as a denial.
    [Fact]
    public void DepartmentsWithNoOverlapReachNothing()
    {
        DataScope merged = DataScope.Intersect(
            DataScope.Departments(Engineering),
            DataScope.Departments(Sales));

        Assert.Empty(merged.DepartmentIds);
        Assert.True(merged.ReachesNothing);
    }

    // ---- permissions ----

    [Fact]
    public void HoldingNothingGrantsNothing()
    {
        Assert.Empty(AccessLevelPermissions.Merge([]).Modules);
        Assert.Equal(DataScopeKind.None, AccessLevelPermissions.Merge([]).Scope.Kind);
    }

    [Fact]
    public void ActionsAcrossLevelsAreUnioned()
    {
        AccessLevelPermissions merged = AccessLevelPermissions.Merge(
        [
            Level(DataScope.Everything, Module(Employees, true, PermissionAction.View)),
            Level(DataScope.Everything, Module(Employees, true, PermissionAction.Export)),
        ]);

        Assert.Contains(PermissionAction.View, ActionsFor(merged, Employees));
        Assert.Contains(PermissionAction.Export, ActionsFor(merged, Employees));
    }

    [Fact]
    public void ModulesFromDifferentLevelsAreAllPresent()
    {
        AccessLevelPermissions merged = AccessLevelPermissions.Merge(
        [
            Level(DataScope.Everything, Module(Employees, true, PermissionAction.View)),
            Level(DataScope.Everything, Module(Leave, true, PermissionAction.Approve)),
        ]);

        Assert.Equal(2, merged.Modules.Count);
    }

    // Access: false is that level declining the module, not vetoing it for the others.
    // A veto would mean granting someone an extra level could take access away, which
    // is not what anyone means by granting.
    [Fact]
    public void ALevelThatDeclinesAModuleDoesNotVetoIt()
    {
        AccessLevelPermissions merged = AccessLevelPermissions.Merge(
        [
            Level(DataScope.Everything, Module(Employees, true, PermissionAction.View)),
            Level(DataScope.Everything, Module(Employees, false)),
        ]);

        Assert.Contains(PermissionAction.View, ActionsFor(merged, Employees));
    }

    // The whole point: adding a broad level to a narrow one must not widen what the
    // narrow one could already do. This is how "also make them a Recruiter" would
    // otherwise end in organisation-wide access to salaries.
    [Fact]
    public void AddingABroadLevelDoesNotWidenReach()
    {
        AccessLevelPermissions merged = AccessLevelPermissions.Merge(
        [
            Level(
                new DataScope { Kind = DataScopeKind.DirectReports },
                Module(Employees, true, PermissionAction.View)),
            Level(DataScope.Everything, Module(Employees, true, PermissionAction.Export)),
        ]);

        Assert.Equal(DataScopeKind.DirectReports, merged.Scope.Kind);

        // It does widen what they may *do* — that half is a union.
        Assert.Contains(PermissionAction.Export, ActionsFor(merged, Employees));
    }

    // A merged set is stored and audited, so it must say what it grants rather than
    // relying on the evaluator to expand it later.
    [Fact]
    public void DependenciesAreExpandedInTheResult()
    {
        AccessLevelPermissions merged = AccessLevelPermissions.Merge(
        [
            Level(DataScope.Everything, Module(Employees, true, PermissionAction.Administer)),
            Level(DataScope.Everything, Module(Employees, true, PermissionAction.Approve)),
        ]);

        IReadOnlyCollection<PermissionAction> actions = ActionsFor(merged, Employees);

        Assert.Contains(PermissionAction.View, actions);
        Assert.Contains(PermissionAction.Edit, actions);
    }

    // The client objected to a silent merge: when someone can delete records, an
    // admin has to be able to see which level is responsible.
    [Fact]
    public void TheLevelResponsibleForAGrantCanBeNamed()
    {
        (string, AccessLevelPermissions)[] held =
        [
            ("Line Manager", Level(
                new DataScope { Kind = DataScopeKind.DirectReports },
                Module(Employees, true, PermissionAction.View))),

            ("Recruiter", Level(
                DataScope.Everything,
                Module(Employees, true, PermissionAction.View, PermissionAction.Delete))),
        ];

        Assert.Equal(
            ["Recruiter"],
            AccessLevelPermissions.WhichGrant(held, Employees, PermissionAction.Delete));

        Assert.Equal(
            ["Line Manager", "Recruiter"],
            AccessLevelPermissions.WhichGrant(held, Employees, PermissionAction.View));
    }
}
