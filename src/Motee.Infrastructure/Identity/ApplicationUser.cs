using Microsoft.AspNetCore.Identity;

namespace Motee.Infrastructure.Identity;

public class ApplicationUser : IdentityUser<Guid>
{
    // Null for Motee platform staff, who operate across every tenant and are
    // excluded from the tenant query filter.
    public Guid? TenantId { get; set; }

    public bool IsPlatformStaff { get; set; }

    // The account owner for this tenant — whoever registered it, plus anyone they
    // later promote. Bypasses the permission check entirely rather than holding a
    // level that grants everything.
    //
    // Two reasons it is a flag and not an access level. Access levels are tenant-owned
    // now, so a company that deactivates the wrong one would otherwise have no way
    // back in short of us editing their database. And a level enumerating every
    // module goes stale the moment a new module ships, whereas a bypass does not.
    //
    // The bypass skips the check, never the audit: an owner's actions are recorded
    // like anyone else's, and the Access Levels screen lists who holds this.
    public bool IsOwner { get; set; }

    // Set once the user is linked to an employee record; null for the admin
    // created during registration, before any employees exist.
    public Guid? EmployeeId { get; set; }


    public required string FirstName { get; set; }

    public string? MiddleName { get; set; }

    public required string LastName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}
