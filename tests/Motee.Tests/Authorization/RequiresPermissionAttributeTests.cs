using Motee.Api.Authorization;
using Motee.Domain.Authorization;

namespace Motee.Tests.Authorization;

public class RequiresPermissionAttributeTests
{
    [Fact]
    public void EncodesModuleAndActionIntoThePolicyName()
    {
        RequiresPermissionAttribute attribute =
            new("organization.employees", PermissionAction.Delete);

        Assert.Equal("motee.permission:organization.employees:Delete:None", attribute.Policy);
    }

    // The minimum is a named argument, so it is applied after the constructor has
    // already written a policy name. If it did not rewrite that name, the requirement
    // the provider builds would silently lose it — and the endpoint would go back to
    // accepting Self scope while still reading as though it did not.
    [Fact]
    public void EncodesAMinimumScopeIntoThePolicyName()
    {
        RequiresPermissionAttribute attribute =
            new("operations.contracts", PermissionAction.View)
            {
                MinimumScope = DataScopeKind.DirectReports,
            };

        Assert.Equal(
            "motee.permission:operations.contracts:View:DirectReports", attribute.Policy);
    }

    [Fact]
    public void AskingForNoMinimumIsTheDefault()
    {
        RequiresPermissionAttribute attribute =
            new("organization.employees", PermissionAction.View);

        Assert.Equal(DataScopeKind.None, attribute.MinimumScope);
    }

    [Theory]
    [InlineData("organization.employees", PermissionAction.View, DataScopeKind.None)]
    [InlineData("time-payroll.leave", PermissionAction.Approve, DataScopeKind.DirectReports)]
    [InlineData("admin.settings", PermissionAction.Edit, DataScopeKind.All)]
    public void RoundTripsThroughThePolicyName(
        string module,
        PermissionAction action,
        DataScopeKind minimumScope)
    {
        RequiresPermissionAttribute attribute = new(module, action)
        {
            MinimumScope = minimumScope,
        };

        Assert.True(RequiresPermissionAttribute.TryParse(
            attribute.Policy!,
            out string parsedModule,
            out PermissionAction parsedAction,
            out DataScopeKind parsedMinimum));

        Assert.Equal(module, parsedModule);
        Assert.Equal(action, parsedAction);
        Assert.Equal(minimumScope, parsedMinimum);
    }

    // Anything that is not one of ours must fall through to the default provider
    // rather than being silently treated as a permission policy.
    [Theory]
    [InlineData("SomeOtherPolicy")]
    [InlineData("motee.permission:")]
    [InlineData("motee.permission:only-one-part")]
    [InlineData("motee.permission:module:View")]
    [InlineData("motee.permission:module:NotAnAction:None")]
    [InlineData("motee.permission:module:View:NotAScope")]
    [InlineData("motee.permission::View:None")]
    public void RejectsPolicyNamesItDoesNotOwn(string policyName)
    {
        Assert.False(RequiresPermissionAttribute.TryParse(
            policyName, out _, out _, out _));
    }

    [Fact]
    public void ParsesActionAndScopeCaseInsensitively()
    {
        Assert.True(RequiresPermissionAttribute.TryParse(
            "motee.permission:organization.employees:delete:directreports",
            out _,
            out PermissionAction action,
            out DataScopeKind minimumScope));

        Assert.Equal(PermissionAction.Delete, action);
        Assert.Equal(DataScopeKind.DirectReports, minimumScope);
    }
}
