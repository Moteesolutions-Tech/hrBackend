using Motee.Domain.Health;

namespace Motee.Tests.Health;

// Workplace adjustments, and the confidentiality line.
//
// An adjustment has its own lifecycle because it is a commitment somebody has
// to deliver: a phased return has an owner, a start date and a review date, and
// "agreed" is a different thing from "in place". The screen's earlier shape was
// a bare list of strings, which could record the recommendation and nothing
// about whether it happened.
//
// Written before the rules.
public class OhAdjustmentTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static OhAdjustment At(
        AdjustmentStage stage,
        string description = "Phased return over four weeks",
        Guid? owner = null,
        DateOnly? effective = null,
        DateOnly? review = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ReferralId = Guid.NewGuid(),
            Description = description,
            RecommendedOn = Today.AddDays(-7),
            Stage = stage,
            ImplementationOwnerEmployeeId = owner,
            EffectiveDate = effective,
            ReviewDate = review,
        };

    // ── the lifecycle ──────────────────────────────────────────────────────

    [Fact]
    public void AnAdjustmentMovesForward()
    {
        Assert.True(Adjustments.CanMove(AdjustmentStage.Proposed, AdjustmentStage.Agreed));
        Assert.True(Adjustments.CanMove(AdjustmentStage.Agreed, AdjustmentStage.Implemented));
        Assert.True(Adjustments.CanMove(AdjustmentStage.Implemented, AdjustmentStage.Reviewed));
    }

    [Fact]
    public void AnAdjustmentDoesNotGoBackwards()
    {
        Assert.False(Adjustments.CanMove(AdjustmentStage.Implemented, AdjustmentStage.Agreed));
    }

    // An adjustment can be withdrawn before it is ever put in place, and one
    // that cannot be closed from Proposed sits on the list indefinitely.
    [Theory]
    [InlineData(AdjustmentStage.Proposed)]
    [InlineData(AdjustmentStage.Agreed)]
    [InlineData(AdjustmentStage.Implemented)]
    public void AnAdjustmentCanBeClosedFromAnywhere(AdjustmentStage from)
    {
        Assert.True(Adjustments.CanMove(from, AdjustmentStage.Closed));
    }

    [Fact]
    public void TheOfferedStagesAreOnesTheRuleAllows()
    {
        foreach (AdjustmentStage from in Enum.GetValues<AdjustmentStage>())
        {
            Assert.All(
                Adjustments.NextFrom(from),
                to => Assert.True(Adjustments.CanMove(from, to)));
        }
    }

    // ── what each step needs ───────────────────────────────────────────────

    [Fact]
    public void AnAdjustmentNeedsADescription()
    {
        Assert.Equal(
            OhRefusal.DescriptionRequired,
            Adjustments.Check(
                At(AdjustmentStage.Proposed, description: "   "), AdjustmentStage.Agreed));
    }

    // An agreed adjustment with no owner is the commitment that quietly never
    // happens.
    [Fact]
    public void AgreeingNeedsSomebodyToOwnIt()
    {
        Assert.Equal(
            OhRefusal.OwnerNotFound,
            Adjustments.Check(At(AdjustmentStage.Proposed), AdjustmentStage.Agreed));
    }

    [Fact]
    public void AgreeingWithAnOwnerIsAccepted()
    {
        Assert.Equal(
            OhRefusal.None,
            Adjustments.Check(
                At(AdjustmentStage.Proposed, owner: Guid.NewGuid()), AdjustmentStage.Agreed));
    }

    // An adjustment in place without a date it started is one nobody can review
    // or evidence.
    [Fact]
    public void ImplementingNeedsAStartDate()
    {
        Assert.Equal(
            OhRefusal.ReferralDateRequired,
            Adjustments.Check(
                At(AdjustmentStage.Agreed, owner: Guid.NewGuid()),
                AdjustmentStage.Implemented));
    }

    [Fact]
    public void ImplementingWithAStartDateIsAccepted()
    {
        Assert.Equal(
            OhRefusal.None,
            Adjustments.Check(
                At(AdjustmentStage.Agreed, owner: Guid.NewGuid(), effective: Today),
                AdjustmentStage.Implemented));
    }

    [Fact]
    public void ClosingNeedsNothingButADescription()
    {
        Assert.Equal(
            OhRefusal.None,
            Adjustments.Check(At(AdjustmentStage.Proposed), AdjustmentStage.Closed));
    }

    // ── what is actually helping ───────────────────────────────────────────

    [Fact]
    public void OnlyAnImplementedOrReviewedAdjustmentIsInPlace()
    {
        Assert.True(Adjustments.IsInPlace(At(AdjustmentStage.Implemented)));
        Assert.True(Adjustments.IsInPlace(At(AdjustmentStage.Reviewed)));
        Assert.False(Adjustments.IsInPlace(At(AdjustmentStage.Proposed)));
        Assert.False(Adjustments.IsInPlace(At(AdjustmentStage.Agreed)));
        Assert.False(Adjustments.IsInPlace(At(AdjustmentStage.Closed)));
    }

    [Fact]
    public void AnAdjustmentReviewDateReachedIsDue()
    {
        Assert.True(Adjustments.ReviewIsDue(
            At(AdjustmentStage.Implemented, review: Today), Today));
    }

    [Fact]
    public void AClosedAdjustmentNeedsNoReview()
    {
        Assert.False(Adjustments.ReviewIsDue(
            At(AdjustmentStage.Closed, review: Today.AddDays(-30)), Today));
    }

    // ── the manager view ───────────────────────────────────────────────────

    // A shape with nowhere to put the confidential parts, rather than a full
    // record with fields stripped on the way out: somebody forgetting to strip
    // a field is the likeliest way this leaks, so there is no field to forget.
    [Fact]
    public void TheManagerViewHasNowhereToPutCaseNotes()
    {
        IEnumerable<string> properties = typeof(OhManagerView)
            .GetProperties()
            .Select(property => property.Name.ToLowerInvariant());

        Assert.DoesNotContain("casenotes", properties);
        Assert.DoesNotContain("notes", properties);
    }

    // There is deliberately no diagnosis anywhere in the module. The frontend
    // carries that as a comment saying one "must never be added"; here it is
    // structural.
    [Fact]
    public void NothingInTheModuleCanHoldADiagnosis()
    {
        foreach (Type type in (Type[])
            [typeof(OhReferral), typeof(OhAdjustment), typeof(OhManagerView)])
        {
            IEnumerable<string> properties = type
                .GetProperties()
                .Select(property => property.Name.ToLowerInvariant());

            Assert.DoesNotContain("diagnosis", properties);
            Assert.DoesNotContain("condition", properties);
            Assert.DoesNotContain("medicaldetail", properties);
        }
    }

    // A manager cannot manage somebody back to work without knowing what they
    // can do and what was agreed.
    [Fact]
    public void TheManagerViewStillCarriesFitnessAndTheAdjustments()
    {
        IEnumerable<string> properties = typeof(OhManagerView)
            .GetProperties()
            .Select(property => property.Name);

        Assert.Contains("Fitness", properties);
        Assert.Contains("Adjustments", properties);
    }

    // ── the board's tabs ───────────────────────────────────────────────────

    // Two definitions of "action required" is two different counts on the same
    // screen, so the server owns the predicate.
    [Fact]
    public void EveryTabTakesEverythingOnAll()
    {
        OhReferral referral = Referral(OhStage.Referred);

        Assert.True(OhBoard.Matches(referral, adjustmentCount: 0, OhBoardTab.All, Today));
    }

    [Fact]
    public void AwaitingTheProviderIsReferredOrAssessed()
    {
        Assert.True(OhBoard.Matches(
            Referral(OhStage.Referred), 0, OhBoardTab.AwaitingProvider, Today));
        Assert.True(OhBoard.Matches(
            Referral(OhStage.AssessmentCompleted), 0, OhBoardTab.AwaitingProvider, Today));
        Assert.False(OhBoard.Matches(
            Referral(OhStage.HrReview), 0, OhBoardTab.AwaitingProvider, Today));
    }

    // The tab cares whether adjustments exist, not what stage the case is at.
    [Fact]
    public void TheAdjustmentsTabNeedsAdjustments()
    {
        Assert.True(OhBoard.Matches(
            Referral(OhStage.HrReview), adjustmentCount: 1, OhBoardTab.Adjustments, Today));
        Assert.False(OhBoard.Matches(
            Referral(OhStage.HrReview), adjustmentCount: 0, OhBoardTab.Adjustments, Today));
    }

    [Fact]
    public void AClosedCaseIsNotOnTheAdjustmentsTab()
    {
        Assert.False(OhBoard.Matches(
            Referral(OhStage.Closed), adjustmentCount: 3, OhBoardTab.Adjustments, Today));
    }

    [Fact]
    public void FollowUpIsTheStagesAfterTheAdjustmentsAreIn()
    {
        Assert.True(OhBoard.Matches(
            Referral(OhStage.AdjustmentsImplemented), 1, OhBoardTab.FollowUp, Today));
        Assert.True(OhBoard.Matches(Referral(OhStage.Returned), 1, OhBoardTab.FollowUp, Today));
        Assert.True(OhBoard.Matches(
            Referral(OhStage.RtwCompleted), 1, OhBoardTab.FollowUp, Today));
        Assert.False(OhBoard.Matches(Referral(OhStage.Referred), 1, OhBoardTab.FollowUp, Today));
    }

    [Fact]
    public void AClosedCaseIsOnlyOnTheClosedTab()
    {
        OhReferral closed = Referral(OhStage.Closed);

        Assert.True(OhBoard.Matches(closed, 0, OhBoardTab.Closed, Today));
        Assert.False(OhBoard.Matches(closed, 0, OhBoardTab.ActionRequired, Today));
    }

    private static OhReferral Referral(OhStage stage) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        EmployeeId = Guid.NewGuid(),
        ReferenceNumber = "OH-0001",
        Stage = stage,
        AbsenceStartDate = Today.AddDays(-40),
    };
}
