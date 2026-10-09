namespace Motee.Domain.Authorization;

// How far a granted action reaches. The permission matrix says what you may do; this
// says to whose records. Ordered narrowest to broadest so breadth can be compared.
//
// None is 0 deliberately: a scope deserialised from older jsonb with no value lands
// here and is denied, rather than defaulting to organisation-wide.
// Declaration order is narrowest first, and Intersect relies on it: between two kinds it
// cannot reconcile, the lower value wins as the safer answer.
//
// Reordering is safe. These persist by name — the access level's scope is jsonb written
// with a JsonStringEnumConverter — so "department" stays "department" whatever its
// integer value becomes. Only the comparison moves.
public enum DataScopeKind
{
    None = 0,

    // Only the acting user's own record.
    Self,

    // The acting user's direct reports, and themselves — a manager needs their own
    // row in the list they work from.
    DirectReports,

    // Whichever department the holder is in. At most one, and it follows them: a
    // manager who transfers takes the scope with them.
    //
    // A kind of its own rather than "Department with an empty list", because the two are
    // different rules that behave differently when data changes, and because an empty
    // list has to keep meaning nothing. A bug that clears a list should close access, not
    // silently re-point it at whoever is holding the level.
    //
    // Resolved into Department before any query sees it — see DataScopeResolver.
    OwnDepartment,

    // Named departments. Not "the one they happen to be in": an access level pins the
    // departments it covers, so moving someone between departments does not silently
    // move what they can see. OwnDepartment above is how to ask for the other thing.
    Department,

    // Whichever site the holder is posted to. The same reasoning as OwnDepartment, and
    // the case for keeping the two apart is stronger here: people relocate, and a site
    // scope that silently followed them would change what a whole team can see.
    OwnBranch,

    // Named sites. The axis a site manager works along: "everyone at the Lagos office"
    // crosses every department there, which neither of the others can express.
    Branch,

    // Named business units, which sit above departments.
    //
    // There is deliberately no OwnBusinessUnit. No employee field corresponds to one —
    // an employee belongs to a department, and the department to a unit — so there is
    // nothing to resolve "their own" against. Adding it would mean inventing a lookup
    // through the department, which is not the same claim and would be wrong whenever
    // somebody has no department.
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

    public IReadOnlyList<Guid> BranchIds { get; init; } = [];

    public static DataScope Departments(params Guid[] ids) =>
        new() { Kind = DataScopeKind.Department, DepartmentIds = ids };

    public static DataScope BusinessUnits(params Guid[] ids) =>
        new() { Kind = DataScopeKind.BusinessUnit, BusinessUnitIds = ids };

    public static DataScope Branches(params Guid[] ids) =>
        new() { Kind = DataScopeKind.Branch, BranchIds = ids };

    public static readonly DataScope TheirDepartment =
        new() { Kind = DataScopeKind.OwnDepartment };

    public static readonly DataScope TheirBranch = new() { Kind = DataScopeKind.OwnBranch };

    // Whether this still needs the holder's own record before it means anything. A
    // self-relative scope that reached a query unresolved would match nothing, silently,
    // so DataScopeResolver turns them into their named equivalents first.
    public bool NeedsHolder => Kind is DataScopeKind.OwnDepartment or DataScopeKind.OwnBranch;

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

                DataScopeKind.Branch => new DataScope
                {
                    Kind = DataScopeKind.Branch,
                    BranchIds = [.. left.BranchIds.Intersect(right.BranchIds)],
                },

                // Two self-relative scopes of the same kind are the same scope: both
                // resolve against the same person.
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

        // Different named kinds. Enum order is narrowest first, so the smaller value is
        // the safer answer.
        //
        // Between Department, BusinessUnit and Branch that ordering is a convention, not
        // a fact: a branch may hold five people or five thousand, and nothing says a
        // business unit contains it or is contained by it. The three are genuinely
        // incomparable, and the only answer guaranteed never to widen would be Nothing.
        //
        // That is not what this returns, because it would mean two sensible levels
        // combining into one that grants nothing, with no way to see why. The exposure is
        // contained instead by Breadth below, which denies for every named kind until the
        // query layer carries the ids — so an incomparable pair narrows to a kind the
        // caller cannot act on rather than to the wrong rows.
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
        DataScopeKind.Branch => BranchIds.Count == 0,

        // Unresolved, so there is nothing to judge yet. A holder with no department
        // resolves to Department([]) and reaches nothing, but that is a fact about the
        // person rather than about the level — and the editor is describing the level.
        DataScopeKind.OwnDepartment or DataScopeKind.OwnBranch => false,
        _ => false,
    };
}
