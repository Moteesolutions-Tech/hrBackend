using System.Security.Claims;

namespace Motee.Application.Auth;

public static class TokenClaimsBuilder
{
    public static IReadOnlyList<Claim> Build(TokenSubject subject)
    {
        Guard(subject);

        List<Claim> claims =
        [
            new(MoteeClaimTypes.Subject, subject.UserId.ToString()),
            new(MoteeClaimTypes.TokenId, Guid.NewGuid().ToString()),
            new(MoteeClaimTypes.Email, subject.Email),
            new(MoteeClaimTypes.Role, subject.Role),
        ];

        if (subject.TenantId is Guid tenantId)
        {
            claims.Add(new Claim(MoteeClaimTypes.TenantId, tenantId.ToString()));
        }

        if (subject.EmployeeId is Guid employeeId)
        {
            claims.Add(new Claim(MoteeClaimTypes.EmployeeId, employeeId.ToString()));
        }

        {
        }

        if (subject.IsPlatformStaff)
        {
            claims.Add(new Claim(MoteeClaimTypes.IsPlatformStaff, "true"));
        }

        return claims;
    }

    // The tenant claim drives the EF query filter, so these two states are not
    // merely invalid input — either one silently breaks tenant isolation.
    private static void Guard(TokenSubject subject)
    {
        if (subject.IsPlatformStaff && subject.TenantId is not null)
        {
            throw new InvalidOperationException(
                "Platform staff must not carry a tenant claim; it would scope them to one tenant.");
        }

        if (!subject.IsPlatformStaff && subject.TenantId is null)
        {
            throw new InvalidOperationException(
                "A tenant user must have a tenant; a token without one bypasses the tenant filter.");
        }
    }
}
