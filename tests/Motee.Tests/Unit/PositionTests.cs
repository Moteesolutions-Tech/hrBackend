using Motee.Domain.Organisation;

namespace Motee.Tests.Unit;

// The establishment: the posts a company has, as distinct from the people in
// them.
//
// A position's status is derived from who holds it and what is being recruited
// into it, never stored. Stored, it is a label somebody set in March that says
// "vacant" over a post that was filled in April — and the establishment is
// precisely the thing finance and HR argue from, so it has to agree with the
// roster underneath it.
public class PositionTests
{
    // ── status ──────────────────────────────────────────────────────────────

    [Fact]
    public void ARetiredPostIsInactiveWhateverElseIsTrue() =>
        Assert.Equal(
            PositionStatus.Inactive,
            Positions.Status(isActive: false, assigned: 3, recruiting: 2, headcount: 3));

    [Fact]
    public void APostWithItsFullComplementIsFilled() =>
        Assert.Equal(
            PositionStatus.Filled,
            Positions.Status(isActive: true, assigned: 3, recruiting: 0, headcount: 3));

    // Over establishment is still filled. It is a real situation — a handover
    // overlap, a transfer in — and reporting it as vacant would be absurd.
    [Fact]
    public void APostCarryingMorePeopleThanItShouldIsStillFilled() =>
        Assert.Equal(
            PositionStatus.Filled,
            Positions.Status(isActive: true, assigned: 4, recruiting: 0, headcount: 3));

    [Fact]
    public void APostBeingRecruitedIntoSaysSo() =>
        Assert.Equal(
            PositionStatus.Recruiting,
            Positions.Status(isActive: true, assigned: 0, recruiting: 1, headcount: 1));

    // Three posts, one person, hiring for the rest: recruiting, not filled.
    // The distinction is the whole point of holding a headcount per post.
    [Fact]
    public void APartlyStaffedPostBeingRecruitedIntoIsRecruiting() =>
        Assert.Equal(
            PositionStatus.Recruiting,
            Positions.Status(isActive: true, assigned: 1, recruiting: 2, headcount: 3));

    [Fact]
    public void APartlyStaffedPostWithNoRecruitmentIsVacant() =>
        Assert.Equal(
            PositionStatus.Vacant,
            Positions.Status(isActive: true, assigned: 1, recruiting: 0, headcount: 3));

    [Fact]
    public void AnEmptyPostNobodyIsHiringForIsVacant() =>
        Assert.Equal(
            PositionStatus.Vacant,
            Positions.Status(isActive: true, assigned: 0, recruiting: 0, headcount: 1));

    // ── how many are still to fill ──────────────────────────────────────────

    [Theory]
    [InlineData(3, 1, 2)]
    [InlineData(3, 3, 0)]
    [InlineData(1, 0, 1)]
    public void TheOpeningsLeftAreTheComplementLessThePeopleInIt(
        int headcount, int assigned, int expected) =>
        Assert.Equal(expected, Positions.OpeningsLeft(headcount, assigned));

    // Never negative. An overlapping handover does not create a debt of
    // minus one opening.
    [Fact]
    public void BeingOverStaffedLeavesNoOpeningsRatherThanNegativeOnes() =>
        Assert.Equal(0, Positions.OpeningsLeft(headcount: 2, assigned: 5));

    // ── validation ──────────────────────────────────────────────────────────

    private static Position Post(
        string title = "Backend engineer",
        int headcount = 1) => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Headcount = headcount,
        };

    [Fact]
    public void APostWithATitleAndAComplementIsAccepted() =>
        Assert.Equal(PositionRefusal.None, Positions.Refusal(Post()));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void APostWithoutATitleIsRefused(string title) =>
        Assert.Equal(PositionRefusal.TitleRequired, Positions.Refusal(Post(title: title)));

    // A post for nobody is not a post. Retiring it is what "no longer
    // needed" means, and that is a different field.
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void APostForNobodyIsRefused(int headcount) =>
        Assert.Equal(
            PositionRefusal.HeadcountNotPositive, Positions.Refusal(Post(headcount: headcount)));

    // Almost always a mistyped figure. A thousand of one post is a company
    // restructure, not a form submission.
    [Fact]
    public void AnImplausibleComplementIsRefused() =>
        Assert.Equal(
            PositionRefusal.HeadcountTooLarge, Positions.Refusal(Post(headcount: 5_000)));
}
