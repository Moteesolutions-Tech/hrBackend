using Motee.Domain.Identity;

namespace Motee.Domain.Authorization;


public static class AccessLevels
{
    public static DataScope ScopeFor(Role role) => role switch
    {
        Role.ReadOnly => DataScope.Everything,
        Role.LineManager => new DataScope { Kind = DataScopeKind.DirectReports },
        _ => DataScope.Everything,
    };

    public static AccessLevelPermissions For(Role role) => new()
    {
        Modules = DefaultAccessLevels.PermissionsFor(role),
        Scope = ScopeFor(role),
    };

    // Deliberately no overload taking a slug or a claim.
    //
    // There was one, and it read the JWT's role claim. Once permissions became
    // tenant-owned access levels that claim stopped being a template name, so the
    // lookup silently returned an empty matrix — twice: /auth/me hid every action from
    // every user, and the medical gate refused everyone including the account owner.
    // Neither failed loudly, because "grants nothing" is a valid answer.
    //
    // A user's permissions come from IUserPermissions, which reads what they hold.
    // This class only builds the templates a new tenant is seeded from.
    public static AccessLevelPermissions ForTemplate(string? slug) =>
        Roles.TryParse(slug, out Role role) ? For(role) : AccessLevelPermissions.Nothing;
}
