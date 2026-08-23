using System.Security.Claims;
using Motee.Application.Auth;
using Motee.Domain.Identity;

namespace Motee.Tests.Auth;

public class TokenClaimsBuilderTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EmployeeId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static TokenSubject TenantUser() => new()
    {
        UserId = UserId,
        Email = "ada@acme.com",
        TenantId = TenantId,
        EmployeeId = EmployeeId,
        Role = Roles.ToSlug(Role.HrAdmin),
        IsPlatformStaff = false,
    };

    private static TokenSubject PlatformStaff() => new()
    {
        UserId = UserId,
        Email = "ops@motee.app",
        TenantId = null,
        EmployeeId = null,
        Role = Roles.ToSlug(Role.SuperAdmin),
        IsPlatformStaff = true,
    };

    private static string? Value(IReadOnlyList<Claim> claims, string type) =>
        claims.FirstOrDefault(claim => claim.Type == type)?.Value;

    [Fact]
    public void EmitsIdentityClaimsForATenantUser()
    {
        IReadOnlyList<Claim> claims = TokenClaimsBuilder.Build(TenantUser());

        Assert.Equal(UserId.ToString(), Value(claims, MoteeClaimTypes.Subject));
        Assert.Equal("ada@acme.com", Value(claims, MoteeClaimTypes.Email));
        Assert.Equal(TenantId.ToString(), Value(claims, MoteeClaimTypes.TenantId));
        Assert.Equal(EmployeeId.ToString(), Value(claims, MoteeClaimTypes.EmployeeId));
        Assert.Equal(Roles.ToSlug(Role.HrAdmin), Value(claims, MoteeClaimTypes.Role));
    }

    // The tenant claim is what the EF query filter keys on. A platform token that
    // carried one would silently scope Motee staff to a single tenant; one that
    // carried the wrong one would be a cross-tenant data leak.
    [Fact]
    public void PlatformStaffCarryNoTenantClaim()
    {
        IReadOnlyList<Claim> claims = TokenClaimsBuilder.Build(PlatformStaff());

        Assert.Null(Value(claims, MoteeClaimTypes.TenantId));
        Assert.Equal("true", Value(claims, MoteeClaimTypes.IsPlatformStaff));
    }

    [Fact]
    public void TenantUsersAreNotMarkedAsPlatformStaff()
    {
        IReadOnlyList<Claim> claims = TokenClaimsBuilder.Build(TenantUser());

        Assert.Null(Value(claims, MoteeClaimTypes.IsPlatformStaff));
    }

    // A tenant user with no tenant would bypass the query filter entirely.
    [Fact]
    public void RejectsATenantUserWithoutATenant()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TokenClaimsBuilder.Build(TenantUser() with { TenantId = null }));
    }

    [Fact]
    public void RejectsPlatformStaffCarryingATenant()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TokenClaimsBuilder.Build(PlatformStaff() with { TenantId = TenantId }));
    }

    // Null must mean the claim is absent, never an empty or "00000000-..." value —
    // a parser reading Guid.Empty as a real id would match nothing or everything.
    [Fact]
    public void OmitsOptionalClaimsRatherThanEmittingEmptyValues()
    {
        IReadOnlyList<Claim> claims = TokenClaimsBuilder.Build(
            TenantUser() with { EmployeeId = null });

        Assert.Null(Value(claims, MoteeClaimTypes.EmployeeId));
        Assert.DoesNotContain(claims, claim => string.IsNullOrWhiteSpace(claim.Value));
    }

    [Fact]
    public void GivesEveryTokenItsOwnIdentifier()
    {
        string? first = Value(TokenClaimsBuilder.Build(TenantUser()), MoteeClaimTypes.TokenId);
        string? second = Value(TokenClaimsBuilder.Build(TenantUser()), MoteeClaimTypes.TokenId);

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void EmitsEachClaimTypeOnce()
    {
        IReadOnlyList<Claim> claims = TokenClaimsBuilder.Build(TenantUser());

        Assert.DoesNotContain(claims.GroupBy(claim => claim.Type), group => group.Count() > 1);
    }

    // Reserved for impersonation, which is not built yet.
    [Fact]
    public void DoesNotYetEmitImpersonationClaims()
    {
        IReadOnlyList<Claim> claims = TokenClaimsBuilder.Build(PlatformStaff());

        Assert.Null(Value(claims, MoteeClaimTypes.ActAs));
        Assert.Null(Value(claims, MoteeClaimTypes.ImpersonatedBy));
    }
}
