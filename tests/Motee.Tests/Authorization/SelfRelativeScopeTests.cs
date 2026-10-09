using Motee.Domain.Authorization;

namespace Motee.Tests.Authorization;

// The properties that make self-relative scopes safe to add: an empty list still means
// nothing, merging still narrows, and the two kinds are distinguishable from the named
// ones they resolve into.
public class SelfRelativeScopeTests
{
    [Fact]
    public void OnlyTheSelfRelativeKindsNeedTheHolder()
    {
        Assert.True(DataScope.TheirDepartment.NeedsHolder);
        Assert.True(DataScope.TheirBranch.NeedsHolder);

        Assert.False(DataScope.Departments(Guid.NewGuid()).NeedsHolder);
        Assert.False(DataScope.Branches(Guid.NewGuid()).NeedsHolder);
        Assert.False(DataScope.Everything.NeedsHolder);
        Assert.False(DataScope.Nothing.NeedsHolder);
    }

    // The whole point of not overloading the empty list. A bug that clears a named list
    // closes access; it does not silently re-point the level at whoever holds it.
    [Fact]
    public void AnEmptyNamedListStillReachesNothing()
    {
        Assert.True(DataScope.Departments().ReachesNothing);
        Assert.True(DataScope.Branches().ReachesNothing);
    }

    // Unresolved, so there is nothing to judge yet: a holder with no department resolves
    // to an empty Department, but that is a fact about the person, and the editor
    // describing the level should not call the level empty.
    [Fact]
    public void ASelfRelativeScopeIsNotItselfEmpty()
    {
        Assert.False(DataScope.TheirDepartment.ReachesNothing);
        Assert.False(DataScope.TheirBranch.ReachesNothing);
    }

    // Two levels that both say "their own department" are the same scope: both resolve
    // against the same person.
    [Fact]
    public void TwoSelfRelativeScopesOfTheSameKindMerge()
    {
        DataScope merged = DataScope.Intersect(
            DataScope.TheirDepartment, DataScope.TheirDepartment);

        Assert.Equal(DataScopeKind.OwnDepartment, merged.Kind);
    }

    // Narrowest-first ordering: "their own department" is at most one department, so it
    // wins against a level naming several.
    [Fact]
    public void TheirOwnDepartmentIsNarrowerThanNamedDepartments()
    {
        DataScope merged = DataScope.Intersect(
            DataScope.Departments(Guid.NewGuid(), Guid.NewGuid()),
            DataScope.TheirDepartment);

        Assert.Equal(DataScopeKind.OwnDepartment, merged.Kind);
    }

    // All is the identity, so adding an unrestricted level must not widen a narrow one.
    [Fact]
    public void MergingWithEverythingLeavesASelfRelativeScopeAlone()
    {
        Assert.Equal(
            DataScopeKind.OwnBranch,
            DataScope.Intersect(DataScope.TheirBranch, DataScope.Everything).Kind);

        Assert.Equal(
            DataScopeKind.OwnBranch,
            DataScope.Intersect(DataScope.Everything, DataScope.TheirBranch).Kind);
    }

    [Fact]
    public void MergingWithNothingStillReachesNothing()
    {
        Assert.Equal(
            DataScopeKind.None,
            DataScope.Intersect(DataScope.TheirDepartment, DataScope.Nothing).Kind);
    }

    // Until a query layer carries the ids, every named kind denies — which is what
    // contains the fact that the three named axes are not really comparable.
    [Fact]
    public void SelfRelativeScopesGrantNoBreadthOfTheirOwn()
    {
        Assert.Equal(PermissionScope.None, DataScope.TheirDepartment.Breadth);
        Assert.Equal(PermissionScope.None, DataScope.TheirBranch.Breadth);
    }

    // Business units have no self-relative form, and deliberately: no employee field
    // corresponds to one, so there would be nothing to resolve against.
    [Fact]
    public void ThereIsNoSelfRelativeBusinessUnit()
    {
        Assert.DoesNotContain(
            Enum.GetNames<DataScopeKind>(),
            name => name.Contains("BusinessUnit", StringComparison.Ordinal)
                && name.StartsWith("Own", StringComparison.Ordinal));
    }
}
