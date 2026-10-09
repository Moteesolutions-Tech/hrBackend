namespace Motee.Domain.Platform;

// What a member of Motee's own staff may do.
//
// A separate axis from tenant access levels, not an extension of them. A tenant's access
// level answers "what may this person do inside their company"; this answers "what may
// this person do to the companies". The two never mix: a platform operator holds no
// module permissions, and no tenant access level grants anything here.
//
// Deliberately a fixed enum rather than a configurable set, unlike AccessLevel. Tenants
// customise their own roles because every company organises differently; Motee has one
// support process, and a configurable platform role is a way for us to grant ourselves
// access to customer data by editing a row.
public enum PlatformRole
{
    // Reads tenant records and the platform audit trail. The default for support staff:
    // enough to answer "is their account active, when did they last log in", without
    // reaching into anybody's HR data.
    Support,

    // Subscriptions, plans and invoices, once those exist. Nothing operational.
    Finance,

    // Everything here, including promoting and demoting other platform staff.
    Admin,
}

public static class PlatformPermissions
{
    // Platform capabilities, named like tenant modules so one vocabulary covers both.
    // Prefixed because a tenant module key and a platform one must never collide — a
    // "settings" that meant both would be a hole rather than a convenience.
    public const string Tenants = "platform.tenants";
    public const string Staff = "platform.staff";
    public const string Billing = "platform.billing";
    public const string Audit = "platform.audit";

    public static readonly IReadOnlyList<string> All = [Tenants, Staff, Billing, Audit];

    // Read is separated from write throughout: the common support task is looking
    // something up, and a role that must be able to suspend a company in order to read
    // its name is one nobody can safely hold.
    public static bool Allows(PlatformRole role, string permission, bool write) => role switch
    {
        PlatformRole.Admin => true,

        // Reads tenants and the audit trail; changes neither.
        PlatformRole.Support => !write && permission is Tenants or Audit,

        PlatformRole.Finance => permission == Billing || (!write && permission == Tenants),

        _ => false,
    };
}
