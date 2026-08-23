namespace Motee.Domain.Identity;

// Fixed platform-wide set. Tenants customise nothing here — the access matrix each
// role carries is defined in DefaultAccessLevels.
public enum Role
{
    SuperAdmin,
    HrAdmin,
    HrManager,
    Finance,
    LineManager,
    Executive,
    Recruiter,
    ItAdmin,
    Auditor,
    ReadOnly,
}

public static class Roles
{
    public static IReadOnlyList<Role> All => [.. Enum.GetValues<Role>()];

    // camelCase, the same form every other enum takes on the wire — fullTime,
    // onLeave, remote. Identity matches on the normalised (upper-cased) name, so the
    // casing here is free to follow our convention rather than its own.
    //
    // Derived from the member name rather than listed separately: a parallel table of
    // slugs is a second place to forget when a member is added.
    public static string ToSlug(Role role) =>
        char.ToLowerInvariant(role.ToString()[0]) + role.ToString()[1..];

    // The role arrives as an untrusted claim, so parsing can fail and callers decide
    // what that means. Numeric input is refused: "1" must not resolve to a role.
    public static bool TryParse(string? slug, out Role role)
    {
        role = default;

        return !string.IsNullOrWhiteSpace(slug)
            && !int.TryParse(slug, out _)
            && Enum.TryParse(slug.Trim(), ignoreCase: true, out role)
            && Enum.IsDefined(role);
    }
}
