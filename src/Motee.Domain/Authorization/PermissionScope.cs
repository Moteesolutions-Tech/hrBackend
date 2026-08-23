namespace Motee.Domain.Authorization;

// How far a granted action reaches. The matrix says what you may do; this says to
// whose records. Ordered narrowest to broadest so breadth can be compared.
//
// None is 0 deliberately: a permission deserialised from older jsonb with no scope
// lands here and is denied, rather than defaulting to organisation-wide.
public enum PermissionScope
{
    None = 0,

    // Only the acting user's own record.
    Self,

    // The acting user's direct reports.
    Team,

    // Everyone in the acting user's department.
    Department,

    // Every record in the tenant.
    All,
}
