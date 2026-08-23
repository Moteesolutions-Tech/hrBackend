using Motee.Domain.Authorization;

namespace Motee.Application.Auth;

// What a signed-in user may do, resolved from the access levels they hold.
//
// Deliberately not carried in the JWT. A merged permission set across 42 modules is
// several kilobytes, and worse, it would be frozen at sign-in — an admin correcting
// a level would not take effect until everyone's token expired. Resolving it per
// request, from a cache, costs less than that is worth.
public interface IUserPermissions
{
    Task<ResolvedPermissions> ForAsync(Guid userId, CancellationToken cancellationToken = default);

    // Called when a level is edited, assigned or withdrawn, so the next request sees
    // the change rather than waiting for the cache to lapse.
    void Forget(Guid userId);
}

public sealed record ResolvedPermissions
{
    public static readonly ResolvedPermissions None = new()
    {
        IsOwner = false,
        Permissions = AccessLevelPermissions.Nothing,
        LevelNames = [],
    };

    // The account owner bypasses the permission check entirely. Not "holds a level
    // granting everything": a level enumerating every module goes stale the moment a
    // new one ships, and a tenant that deactivated the wrong level would have no way
    // back in.
    public required bool IsOwner { get; init; }

    // Union of what the held levels permit, reaching only as far as the narrowest
    // allows.
    public required AccessLevelPermissions Permissions { get; init; }

    // The levels behind that, so an admin asking why someone can delete records gets
    // an answer instead of a merged matrix.
    public required IReadOnlyList<string> LevelNames { get; init; }
}
