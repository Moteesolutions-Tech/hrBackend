using Motee.Domain.Authorization;
using Motee.Domain.Identity;

namespace Motee.Tests.Authorization;

// Guards the port of permissions/seeds.ts. If the frontend matrix changes and this
// is not regenerated, the API and the UI disagree about who can do what — and the
// API is the one that decides.
public class DefaultAccessLevelsTests
{
    [Fact]
    public void CoversEveryModuleInTheCatalogue()
    {
        Assert.Equal(40, ModuleCatalogue.All.Count);
        Assert.Equal(ModuleCatalogue.All.Count, ModuleCatalogue.AccessByModule.Count);
    }

    [Fact]
    public void DefinesOneLevelPerRole()
    {
        Assert.Equal(Roles.All.Count, DefaultAccessLevels.All.Count);

        Assert.Equal(
            Roles.All.OrderBy(role => role),
            DefaultAccessLevels.All.Select(level => level.Role).OrderBy(role => role));
    }

    [Fact]
    public void SuperAdminReachesEveryModuleWithEveryAction()
    {
        IReadOnlyList<ModulePermission> permissions =
            DefaultAccessLevels.PermissionsFor(Role.SuperAdmin);

        Assert.All(permissions, permission =>
        {
            Assert.True(permission.Access, permission.Module);
            Assert.Equal(6, permission.Actions.Count);
        });
    }

    [Fact]
    public void ReadOnlyGetsViewAndNothingElse()
    {
        IReadOnlyList<ModulePermission> permissions =
            DefaultAccessLevels.PermissionsFor(Role.ReadOnly);

        Assert.All(permissions.Where(permission => permission.Access), permission =>
            Assert.Equal([PermissionAction.View], permission.Actions));
    }

    // A revoked module must not carry actions, or PermissionEvaluator's access flag
    // would be the only thing standing between a user and the operation.
    [Fact]
    public void ARevokedModuleCarriesNoActions()
    {
        foreach (AccessLevelDefinition level in DefaultAccessLevels.All)
        {
            Assert.All(
                level.Permissions.Where(permission => !permission.Access),
                permission => Assert.Empty(permission.Actions));
        }
    }

    [Theory]
    [InlineData("employee.medical", Role.HrAdmin, true)]
    [InlineData("employee.medical", Role.HrManager, false)]
    [InlineData("employee.medical", Role.LineManager, false)]
    [InlineData("employee.disciplinary", Role.LineManager, false)]
    public void SensitiveEmployeeSectionsStayWithHrAdmin(string module, Role role, bool expected)
    {
        Assert.Equal(expected, HasAccess(role, module));
    }

    [Theory]
    [InlineData(Role.SuperAdmin, true)]
    [InlineData(Role.HrAdmin, true)]
    [InlineData(Role.ItAdmin, true)]
    [InlineData(Role.HrManager, false)]
    [InlineData(Role.Auditor, false)]
    public void SettingsIsAdminOnly(Role role, bool expected)
    {
        Assert.Equal(expected, HasAccess(role, "admin.settings"));
    }

    // The Read-only description promises "no Audit Trail or Settings".
    [Fact]
    public void ReadOnlyIsKeptOutOfAuditTrailAndSettings()
    {
        Assert.False(HasAccess(Role.ReadOnly, "admin.audit-trail"));
        Assert.False(HasAccess(Role.ReadOnly, "admin.settings"));
    }

    [Fact]
    public void AuditorSeesTheAuditTrailButCannotChangeAnything()
    {
        Assert.True(HasAccess(Role.Auditor, "admin.audit-trail"));

        Assert.Equal(
            [PermissionAction.View, PermissionAction.Export],
            Permission(Role.Auditor, "admin.audit-trail")!.Actions);
    }

    [Fact]
    public void LineManagerCanApproveLeaveButNotAdministerIt()
    {
        ModulePermission? leave = Permission(Role.LineManager, "time-payroll.leave");

        Assert.NotNull(leave);
        Assert.True(leave.Access);
        Assert.Contains(PermissionAction.Approve, leave.Actions);
        Assert.DoesNotContain(PermissionAction.Delete, leave.Actions);
    }

    // The generated matrix has to satisfy the evaluator that enforces it.
    [Fact]
    public void TheEvaluatorAgreesWithTheGeneratedMatrix()
    {
        AccessLevelPermissions hrAdmin = AccessLevels.For(Role.HrAdmin);
        AccessLevelPermissions readOnly = AccessLevels.For(Role.ReadOnly);

        Assert.NotEqual(PermissionScope.None, ScopeBreadth(hrAdmin, "organization.employees", PermissionAction.Delete));
        Assert.Equal(PermissionScope.None, ScopeBreadth(readOnly, "organization.employees", PermissionAction.Delete));
        Assert.NotEqual(PermissionScope.None, ScopeBreadth(readOnly, "organization.employees", PermissionAction.View));
    }

    private static PermissionScope ScopeBreadth(
        AccessLevelPermissions? level,
        string module,
        PermissionAction action) =>
        PermissionEvaluator.ScopeFor(level, module, action).Breadth;

    private static ModulePermission? Permission(Role role, string module) =>
        DefaultAccessLevels.PermissionsFor(role)
            .FirstOrDefault(permission => permission.Module == module);

    private static bool HasAccess(Role role, string module) =>
        Permission(role, module)?.Access ?? false;
}
