namespace Motee.Application.Authorization;

// Who has an account in this company, and what each of them holds.
//
// Access levels are the definitions - what a level called "HR Manager" may do. This is
// the operational other half: which people exist, whether they can sign in, and which
// levels are on them. Assigning a level already worked, but nothing could list the
// people to assign one to, so the holders endpoints had no source and the Users screen
// had nothing to render.
public interface ITenantUserService
{
    Task<IReadOnlyList<TenantUserDto>> ListAsync(CancellationToken cancellationToken = default);
}

// What the backend actually knows about an account, rather than what an admin might
// eventually want to do to one.
//
// The frontend's model also has "restricted" and "revoked", plus a reason and who
// changed it. None of that exists here: there is no status column on a user and no
// record of account actions, so reporting those states would mean inventing them.
// Adding them is a schema change and a product decision, not a mapping.
public enum UserAccountState
{
    // Verified and able to sign in.
    Active,

    // Identity's lockout is in effect, from failed sign-ins. Ends by itself.
    Locked,

    // Registered but never verified the emailed code. They cannot sign in, and they
    // are not locked out either - the distinction matters because the fix is different:
    // one waits, the other needs a new code.
    Pending,
}

public sealed record TenantUserDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Initials { get; init; }

    public required string Email { get; init; }

    // Bypasses every permission check, so the interface has to show it - otherwise
    // someone holding no levels appears to have no access while having all of it.
    public required bool IsOwner { get; init; }

    // Null for the admin created at registration, before any employees exist. Job title
    // and department come from that record, so they are null together.
    public Guid? EmployeeId { get; init; }

    public string? JobTitle { get; init; }

    public string? DepartmentName { get; init; }

    // Every level held, not just one. Someone can hold several and the reach is their
    // union of permissions with the narrower scope, so showing one would misrepresent it.
    public required IReadOnlyList<HeldAccessLevelDto> AccessLevels { get; init; }

    public required UserAccountState State { get; init; }

    // When the lockout ends, so the interface can say how long rather than only that
    // they are locked.
    public DateTimeOffset? LockedUntil { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastLoginAt { get; init; }
}

public sealed record HeldAccessLevelDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    // A level can be deactivated while still assigned. It then grants nothing, and an
    // admin looking at why someone lost access needs to see that here rather than
    // discovering it on another screen.
    public required bool IsActive { get; init; }
}
