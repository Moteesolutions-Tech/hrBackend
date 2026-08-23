using Motee.Domain.Authorization;
using Motee.Domain.Identity;

namespace Motee.Tests.Authorization;

public class AccessDecisionTests
{
    private const string Employees = "organization.employees";
    private const string Leave = "time-payroll.leave";
    private const string Settings = "admin.settings";
    private const string Medical = "employee.medical";

    private static AccessLevelPermissions Level(Role role) => AccessLevels.For(role);

    // Resolve now returns a DataScope — a kind plus, for named kinds, the departments
    // or business units it covers. These tests are about breadth, so they compare the
    // breadth and leave the named cases to DataScopeTests.
    private static PermissionScope ResolveBreadth(
        AccessLevelPermissions? level,
        string module,
        PermissionAction action) =>
        AccessDecision.Resolve(level, module, action).Breadth;

    private static PermissionScope ScopeBreadth(
        AccessLevelPermissions? level,
        string module,
        PermissionAction action) =>
        PermissionEvaluator.ScopeFor(level, module, action).Breadth;

    // ---- scope ----

    [Fact]
    public void HrAdminReachesEveryRecord()
    {
        Assert.Equal(
            PermissionScope.All,
            ScopeBreadth(Level(Role.HrAdmin), Employees, PermissionAction.Edit));
    }

    // The whole point of scope: editing employees must not mean editing everyone.
    [Fact]
    public void LineManagerReachesOnlyTheirTeamForPersonRecords()
    {
        AccessLevelPermissions level = Level(Role.LineManager);

        Assert.Equal(PermissionScope.Team,
            ScopeBreadth(level, Employees, PermissionAction.Edit));
        Assert.Equal(PermissionScope.Team,
            ScopeBreadth(level, Leave, PermissionAction.Approve));
    }

    // Scope is one answer for a whole level now, not one per module, so a Line
    // Manager reaches their direct reports everywhere they have access — announcements
    // included. That is the expressiveness given up when scope moved to the level, and
    // it is the price of being able to intersect scopes across several held levels.
    //
    // A company that wants organisation-wide announcements for managers assigns them a
    // second level scoped to All: the union of permissions with the narrower reach is
    // exactly what that produces.
    [Fact]
    public void ALevelsScopeAppliesToEveryModuleItOpens()
    {
        Assert.Equal(
            PermissionScope.Team,
            ScopeBreadth(
                Level(Role.LineManager), "workspace.announcements", PermissionAction.View));

        Assert.Equal(
            PermissionScope.Team,
            ScopeBreadth(
                Level(Role.LineManager), "organization.employees", PermissionAction.View));
    }

    [Fact]
    public void ADeniedModuleHasNoScope()
    {
        Assert.Equal(
            PermissionScope.None,
            ScopeBreadth(Level(Role.LineManager), Settings, PermissionAction.View));
    }

    // A permission deserialised from jsonb written before scope existed lands on
    // None, and must be refused rather than defaulting to organisation-wide.
    [Fact]
    public void APermissionWithoutScopeIsDenied()
    {
        AccessLevelPermissions level = new()
        {
            Modules =
            [
                new ModulePermission
                {
                    Module = Employees,
                    Access = true,
                    Actions = [PermissionAction.View],
                },
            ],

            // The level grants the module but reaches nothing. Scope now sits here
            // rather than on the module, and an unset one still denies.
            Scope = DataScope.Nothing,
        };

        Assert.Equal(PermissionScope.None, ScopeBreadth(level, Employees, PermissionAction.View));
    }

    [Fact]
    public void ScopeWidensFromSelfThroughToAll()
    {
        Assert.True(PermissionScope.Self < PermissionScope.Team);
        Assert.True(PermissionScope.Team < PermissionScope.Department);
        Assert.True(PermissionScope.Department < PermissionScope.All);
        Assert.True(PermissionScope.None < PermissionScope.Self);
    }

    // ---- self-service ----

    // An employee with no access level at all still reaches their own record.
    [Fact]
    public void AnEmployeeWithNoAccessLevelStillReachesTheirOwnRecord()
    {
        Assert.Equal(
            PermissionScope.Self,
            ResolveBreadth(null, Employees, PermissionAction.View));

        Assert.Equal(
            PermissionScope.Self,
            ResolveBreadth(null, Employees, PermissionAction.Edit));
    }

    [Fact]
    public void AnEmployeeWithNoAccessLevelCanRequestLeave()
    {
        Assert.Equal(
            PermissionScope.Self,
            ResolveBreadth(null, Leave, PermissionAction.Create));
    }

    // Self-service must not become a back door into administration.
    [Theory]
    [InlineData(Settings, PermissionAction.View)]
    [InlineData("admin.audit-trail", PermissionAction.View)]
    [InlineData("admin.access-levels", PermissionAction.Edit)]
    [InlineData("employee.medical", PermissionAction.View)]
    [InlineData("employee.disciplinary", PermissionAction.View)]
    [InlineData("operations.reports", PermissionAction.Export)]
    public void SelfServiceGrantsNothingOnAdminSurfaces(string module, PermissionAction action)
    {
        Assert.Equal(PermissionScope.None, ResolveBreadth(null, module, action));
    }

    [Theory]
    [InlineData(PermissionAction.Delete)]
    [InlineData(PermissionAction.Approve)]
    [InlineData(PermissionAction.Export)]
    public void SelfServiceNeverGrantsDestructiveOrApprovingActions(PermissionAction action)
    {
        Assert.Equal(PermissionScope.None, ResolveBreadth(null, Employees, action));
        Assert.Equal(PermissionScope.None, ResolveBreadth(null, Leave, action));
    }

    // Nobody approves their own leave request.
    [Fact]
    public void AnEmployeeCannotApproveTheirOwnRequest()
    {
        Assert.False(SelfServicePolicy.Grants(Leave, PermissionAction.Approve));
    }

    // The wizard posts medical details inside the employee form, so the employees
    // permission alone would let anyone who can add a person record their health
    // data. This is the gap EmployeesController checks separately for.
    [Fact]
    public void CreatingEmployeesDoesNotCarryTheRightToRecordMedicalDetails()
    {
        Assert.NotEqual(
            PermissionScope.None,
            ResolveBreadth(Level(Role.HrManager), Employees, PermissionAction.Create));

        Assert.Equal(
            PermissionScope.None,
            ResolveBreadth(Level(Role.HrManager), Medical, PermissionAction.Edit));

        Assert.Equal(
            PermissionScope.None,
            ResolveBreadth(Level(Role.LineManager), Medical, PermissionAction.View));
    }

    [Fact]
    public void OnlyHrAdminAndAboveReachMedicalDetails()
    {
        Assert.NotEqual(
            PermissionScope.None,
            ResolveBreadth(Level(Role.HrAdmin), Medical, PermissionAction.Edit));

        Assert.NotEqual(
            PermissionScope.None,
            ResolveBreadth(Level(Role.SuperAdmin), Medical, PermissionAction.Edit));
    }

    // Self-service is a floor, not a ceiling — it must not narrow an admin.
    [Fact]
    public void SelfServiceDoesNotNarrowSomeoneWhoAlreadyHasMore()
    {
        Assert.Equal(
            PermissionScope.All,
            ResolveBreadth(Level(Role.HrAdmin), Employees, PermissionAction.Edit));

        Assert.Equal(
            PermissionScope.Team,
            ResolveBreadth(Level(Role.LineManager), Employees, PermissionAction.Edit));
    }

}
