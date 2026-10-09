using Motee.Application.Auth;
using Motee.Domain.Platform;

namespace Motee.Tests.Auth;

// The boundary between Motee's own staff and a company's. Both sides of it are enforced
// when a token is minted, because the tenant claim is what drives the EF query filter —
// so an invalid combination is not bad input, it is broken isolation.
public class PlatformBoundaryTests
{
    private static TokenSubject Subject(
        bool platformStaff = false,
        Guid? tenantId = null,
        PlatformRole? role = null) => new()
    {
        UserId = Guid.NewGuid(),
        Email = "ada@acme.com",
        TenantId = tenantId,
        EmployeeId = null,
        Role = "hrAdmin",
        IsPlatformStaff = platformStaff,
        PlatformRole = role,
    };

    [Fact]
    public void PlatformStaffMustNotCarryATenant()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            TokenClaimsBuilder.Build(Subject(
                platformStaff: true, tenantId: Guid.NewGuid(), role: PlatformRole.Admin)));

        Assert.Contains("tenant", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATenantUserMustCarryATenant()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TokenClaimsBuilder.Build(Subject(platformStaff: false, tenantId: null)));
    }

    // Staff without a role sit outside every tenant and are authorised for nothing: an
    // account that signs in successfully and can reach not one endpoint. That looks like a
    // permissions bug, and the usual fix for a permissions bug is to grant more.
    [Fact]
    public void PlatformStaffMustCarryARole()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TokenClaimsBuilder.Build(Subject(platformStaff: true, role: null)));
    }

    [Fact]
    public void ATenantUserMustNotCarryAPlatformRole()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TokenClaimsBuilder.Build(Subject(
                tenantId: Guid.NewGuid(), role: PlatformRole.Support)));
    }

    [Fact]
    public void PlatformStaffGetBothClaimsAndNoTenant()
    {
        IReadOnlyList<System.Security.Claims.Claim> claims = TokenClaimsBuilder.Build(
            Subject(platformStaff: true, role: PlatformRole.Support));

        Assert.Equal("true", claims.Single(c => c.Type == MoteeClaimTypes.IsPlatformStaff).Value);
        Assert.Equal("Support", claims.Single(c => c.Type == MoteeClaimTypes.PlatformRole).Value);
        Assert.DoesNotContain(claims, c => c.Type == MoteeClaimTypes.TenantId);
    }

    [Fact]
    public void ATenantUserGetsNeitherPlatformClaim()
    {
        IReadOnlyList<System.Security.Claims.Claim> claims =
            TokenClaimsBuilder.Build(Subject(tenantId: Guid.NewGuid()));

        Assert.DoesNotContain(claims, c => c.Type == MoteeClaimTypes.IsPlatformStaff);
        Assert.DoesNotContain(claims, c => c.Type == MoteeClaimTypes.PlatformRole);
    }

    // Read and write are separated throughout, because the common support task is looking
    // something up. A role that must be able to suspend a company in order to read its
    // name is one nobody can safely hold.
    [Theory]
    [InlineData(PlatformRole.Support, PlatformPermissions.Tenants, false, true)]
    [InlineData(PlatformRole.Support, PlatformPermissions.Tenants, true, false)]
    [InlineData(PlatformRole.Support, PlatformPermissions.Audit, false, true)]
    [InlineData(PlatformRole.Support, PlatformPermissions.Staff, false, false)]
    [InlineData(PlatformRole.Support, PlatformPermissions.Billing, false, false)]
    [InlineData(PlatformRole.Finance, PlatformPermissions.Billing, true, true)]
    [InlineData(PlatformRole.Finance, PlatformPermissions.Tenants, false, true)]
    [InlineData(PlatformRole.Finance, PlatformPermissions.Tenants, true, false)]
    [InlineData(PlatformRole.Finance, PlatformPermissions.Staff, false, false)]
    [InlineData(PlatformRole.Admin, PlatformPermissions.Staff, true, true)]
    [InlineData(PlatformRole.Admin, PlatformPermissions.Billing, true, true)]
    public void RolesAllowOnlyWhatTheyShould(
        PlatformRole role, string permission, bool write, bool expected) =>
        Assert.Equal(expected, PlatformPermissions.Allows(role, permission, write));

    // Support is the default for operators, so what it cannot do matters more than what it
    // can. It must not be able to promote anybody — including itself.
    [Fact]
    public void SupportCannotGrantPlatformRoles()
    {
        Assert.False(PlatformPermissions.Allows(PlatformRole.Support, PlatformPermissions.Staff, true));
        Assert.False(PlatformPermissions.Allows(PlatformRole.Finance, PlatformPermissions.Staff, true));
    }
}
