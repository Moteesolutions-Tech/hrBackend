namespace Motee.Application.Auth;

public sealed record TokenSubject
{
    public required Guid UserId { get; init; }

    public required string Email { get; init; }

    // Null only for Motee platform staff.
    public required Guid? TenantId { get; init; }

    public required Guid? EmployeeId { get; init; }

    // Kept for display only. Authorization no longer reads it: permissions come from
    // the access levels a user holds, resolved per request, so a token cannot carry
    // stale access after an admin edits a level.
    //
    // "owner" for the account owner, otherwise the levels they hold, comma-separated.
    // Empty for someone with none, who still reaches their own record through the
    // self-service floor.
    public required string Role { get; init; }

    public required bool IsPlatformStaff { get; init; }
}
