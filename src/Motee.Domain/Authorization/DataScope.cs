namespace Motee.Domain.Authorization;

// How far a granted action reaches. The permission matrix says what you may do; this
// says to whose records. Ordered narrowest to broadest so breadth can be compared.
//
// None is 0 deliberately: a scope deserialised from older jsonb with no value lands
// here and is denied, rather than defaulting to organisation-wide.
public enum DataScopeKind
{
    None = 0,

    // Only the acting user's own record.
    Self,

    // The acting user's direct reports, and themselves — a manager needs their own
    // row in the list they work from.
    DirectReports,

    // Named departments. Not "the one they happen to be in": an access level pins the
    // departments it covers, so moving someone between departments does not silently
    // move what they can see.
    Department,

    // Named business units, which sit above departments.
    BusinessUnit,

    // Every record in the tenant.
    All,
}

// One scope per access level, not one per module. That is what makes the merge rule
// for someone holding several levels expressible at all: permissions union, scopes
// intersect. Per-module scopes would leave "intersect" with no single thing to apply
// to.
public sealed record DataScope
{
    public static readonly DataScope Nothing = new() { Kind = DataScopeKind.None };

    public static readonly DataScope Everything = new() { Kind = DataScopeKind.All };

    public required DataScopeKind Kind { get; init; }

    // Which departments, when Kind is Department. Empty means the level names none,
    // which reaches nothing — an empty allowlist is not an open one.
    public IReadOnlyList<Guid> DepartmentIds { get; init; } = [];

    public IReadOnlyList<Guid> BusinessUnitIds { get; init; } = [];

    public static DataScope Departments(params Guid[] ids) =>
        new() { Kind = DataScopeKind.Department, DepartmentIds = ids };

    public static DataScope BusinessUnits(params Guid[] ids) =>
        new() { Kind = DataScopeKind.BusinessUnit, BusinessUnitIds = ids };

    // Someone holding two levels gets the narrower reach of the two, while their
    // permissions are unioned. Widening on merge is the mistake that turns "also give
    // them the Recruiter level" into organisation-wide access to salaries.
    //
    // Two scopes of the same named kind intersect by list: the departments both
    // levels agree on. Two different named kinds have no common ground that can be
    // computed without walking the org chart, so the narrower kind wins outright.
    public static DataScope Intersect(DataScope left, DataScope right)
    {
        if (left.Kind == DataScopeKind.None || right.Kind == DataScopeKind.None)
        {
            return Nothing;
        }

        if (left.Kind == right.Kind)
        {
            return left.Kind switch
            {
                DataScopeKind.Department => new DataScope
                {
                    Kind = DataScopeKind.Department,
                    DepartmentIds = [.. left.DepartmentIds.Intersect(right.DepartmentIds)],
                },

                DataScopeKind.BusinessUnit => new DataScope
                {
                    Kind = DataScopeKind.BusinessUnit,
                    BusinessUnitIds = [.. left.BusinessUnitIds.Intersect(right.BusinessUnitIds)],
                },

                _ => left,
            };
        }

        // All is the identity: intersecting anything with "everything" leaves it
        // untouched.
        if (left.Kind == DataScopeKind.All)
        {
            return right;
        }

        if (right.Kind == DataScopeKind.All)
        {
            return left;
        }

        // Different named kinds. Enum order is narrowest first, so the smaller value
        // is the safer answer.
        return left.Kind < right.Kind ? left : right;
    }

    // What the query layer narrows on today. Employee and asset queries still take
    // the older breadth enum, which cannot express "these named departments".
    //
    // Named kinds map to None on purpose. Mapping them to Department would narrow to
    // the *viewer's own* department instead of the ones the level names — which is
    // not merely different, it is wider whenever the viewer sits outside that list.
    // Denying until the query layer carries the ids is the safe direction, and a
    // level that grants nothing is visible in a way a level that grants the wrong
    // rows is not.
    public PermissionScope Breadth => Kind switch
    {
        DataScopeKind.All => PermissionScope.All,
        DataScopeKind.DirectReports => PermissionScope.Team,
        DataScopeKind.Self => PermissionScope.Self,
        _ => PermissionScope.None,
    };

    // Whether this scope reaches nothing at all, which is different from being unset.
    // A Department scope naming no departments is a level that grants access to no
    // records, and the editor should say so rather than let it look permissive.
    public bool ReachesNothing => Kind switch
    {
        DataScopeKind.None => true,
        DataScopeKind.Department => DepartmentIds.Count == 0,
        DataScopeKind.BusinessUnit => BusinessUnitIds.Count == 0,
        _ => false,
    };
}
