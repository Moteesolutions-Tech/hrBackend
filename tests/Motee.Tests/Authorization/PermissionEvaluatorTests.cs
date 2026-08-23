using Motee.Domain.Authorization;

namespace Motee.Tests.Authorization;

public class PermissionEvaluatorTests
{
    private const string Employees = "organization.employees";
    private const string Payroll = "time-payroll.payroll";

    // Scope moved from the module to the level, so a bare Level() reaches everywhere
    // and Scoped() sets the level's reach rather than one module's.
    private static AccessLevelPermissions Level(params ModulePermission[] modules) =>
        new() { Modules = modules, Scope = DataScope.Everything };

    private static AccessLevelPermissions LevelScoped(
        PermissionScope scope,
        params ModulePermission[] modules) =>
        new()
        {
            Modules = modules,
            Scope = scope switch
            {
                PermissionScope.All => DataScope.Everything,
                PermissionScope.Team => new DataScope { Kind = DataScopeKind.DirectReports },
                PermissionScope.Self => new DataScope { Kind = DataScopeKind.Self },
                _ => DataScope.Nothing,
            },
        };

    private static ModulePermission Module(
        string module,
        bool access,
        params PermissionAction[] actions) =>
        new() { Module = module, Access = access, Actions = actions };

    private static PermissionScope ScopeBreadth(
        AccessLevelPermissions? level,
        string module,
        PermissionAction action) =>
        PermissionEvaluator.ScopeFor(level, module, action).Breadth;

    [Fact]
    public void AllowsWhenTheModuleGrantsTheAction()
    {
        AccessLevelPermissions level = Level(
            Module(Employees, true, PermissionAction.View, PermissionAction.Create));

        Assert.NotEqual(PermissionScope.None, ScopeBreadth(level, Employees, PermissionAction.View));
        Assert.NotEqual(PermissionScope.None, ScopeBreadth(level, Employees, PermissionAction.Create));
    }

    [Fact]
    public void DeniesAnActionTheModuleDoesNotList()
    {
        AccessLevelPermissions level = Level(Module(Employees, true, PermissionAction.View));

        Assert.Equal(PermissionScope.None, ScopeBreadth(level, Employees, PermissionAction.Delete));
        Assert.Equal(PermissionScope.None, ScopeBreadth(level, Employees, PermissionAction.Approve));
    }

    // access:false is a hard off switch — listed actions must not leak through.
    [Fact]
    public void DeniesWhenModuleAccessIsRevokedEvenIfActionsRemain()
    {
        AccessLevelPermissions level = Level(
            Module(Employees, false, PermissionAction.View, PermissionAction.Edit));

        Assert.Equal(PermissionScope.None, ScopeBreadth(level, Employees, PermissionAction.View));
        Assert.Equal(PermissionScope.None, ScopeBreadth(level, Employees, PermissionAction.Edit));
    }

    [Fact]
    public void DeniesWhenTheModuleGrantsAccessButListsNoActions()
    {
        AccessLevelPermissions level = Level(Module(Employees, true));

        Assert.Equal(PermissionScope.None, ScopeBreadth(level, Employees, PermissionAction.View));
    }

    // The frontend's useCan returns true when the module is missing from the matrix.
    // The API must not.
    [Fact]
    public void DeniesAModuleAbsentFromTheMatrix()
    {
        AccessLevelPermissions level = Level(Module(Employees, true, PermissionAction.View));

        Assert.Equal(PermissionScope.None, ScopeBreadth(level, Payroll, PermissionAction.View));
    }

    // useCan: `if (!accessLevelId) return true`. Deliberately inverted here.
    [Fact]
    public void DeniesWhenThereIsNoAccessLevelAtAll()
    {
        Assert.Equal(PermissionScope.None, ScopeBreadth(null, Employees, PermissionAction.View));
    }

    [Fact]
    public void DeniesWhenTheAccessLevelHasNoModules()
    {
        Assert.Equal(PermissionScope.None, ScopeBreadth(Level(), Employees, PermissionAction.View));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void DeniesWhenTheModuleIdIsMissing(string? module)
    {
        AccessLevelPermissions level = Level(Module(Employees, true, PermissionAction.View));

        Assert.Equal(PermissionScope.None, ScopeBreadth(level, module!, PermissionAction.View));
    }

    // Module ids are shared constants with the frontend; a casing difference should
    // not silently deny a permission an admin believes they granted.
    [Fact]
    public void MatchesModuleIdsWithoutRegardToCase()
    {
        AccessLevelPermissions level = Level(
            Module("Organization.Employees", true, PermissionAction.View));

        Assert.NotEqual(PermissionScope.None, ScopeBreadth(level, Employees, PermissionAction.View));
    }

    [Fact]
    public void ResolvesDuplicateModuleEntriesDeterministically()
    {
        AccessLevelPermissions level = Level(
            Module(Employees, true, PermissionAction.View),
            Module(Employees, false, PermissionAction.Delete));

        Assert.NotEqual(PermissionScope.None, ScopeBreadth(level, Employees, PermissionAction.View));
        Assert.Equal(PermissionScope.None, ScopeBreadth(level, Employees, PermissionAction.Delete));
    }


}
