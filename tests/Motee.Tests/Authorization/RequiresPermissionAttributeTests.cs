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

        Assert.Equal("motee.permission:organization.employees:Delete", attribute.Policy);
    }

    [Theory]
    [InlineData("organization.employees", PermissionAction.View)]
    [InlineData("time-payroll.leave", PermissionAction.Approve)]
    [InlineData("admin.settings", PermissionAction.Edit)]
    public void RoundTripsThroughThePolicyName(string module, PermissionAction action)
    {
        RequiresPermissionAttribute attribute = new(module, action);

        Assert.True(RequiresPermissionAttribute.TryParse(
            attribute.Policy!, out string parsedModule, out PermissionAction parsedAction));

        Assert.Equal(module, parsedModule);
        Assert.Equal(action, parsedAction);
    }

    // Anything that is not one of ours must fall through to the default provider
    // rather than being silently treated as a permission policy.
    [Theory]
    [InlineData("SomeOtherPolicy")]
    [InlineData("motee.permission:")]
    [InlineData("motee.permission:only-one-part")]
    [InlineData("motee.permission:module:NotAnAction")]
    [InlineData("motee.permission::View")]
    public void RejectsPolicyNamesItDoesNotOwn(string policyName)
    {
        Assert.False(RequiresPermissionAttribute.TryParse(policyName, out _, out _));
    }

    [Fact]
    public void ParsesActionCaseInsensitively()
    {
        Assert.True(RequiresPermissionAttribute.TryParse(
            "motee.permission:organization.employees:delete", out _, out PermissionAction action));

        Assert.Equal(PermissionAction.Delete, action);
    }
}
