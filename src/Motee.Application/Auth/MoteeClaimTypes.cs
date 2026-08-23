namespace Motee.Application.Auth;

// Short, JWT-native names rather than the WS-Federation schema URIs. The token
// handler must run with MapInboundClaims disabled or "sub" and "role" are silently
// rewritten to those URIs on the way in.
public static class MoteeClaimTypes
{
    public const string Subject = "sub";
    public const string TokenId = "jti";
    public const string Email = "email";
    public const string Role = "role";
    public const string TenantId = "tenant_id";
    public const string EmployeeId = "employee_id";
    public const string IsPlatformStaff = "is_platform_staff";

    // Reserved for impersonation; not emitted yet.
    public const string ActAs = "act_as";
    public const string ImpersonatedBy = "impersonated_by";
}
