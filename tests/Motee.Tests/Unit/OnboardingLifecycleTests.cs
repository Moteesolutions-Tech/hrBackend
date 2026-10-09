using Motee.Domain.Onboarding;

namespace Motee.Tests.Unit;

// The onboarding machine is deliberately looser than the offboarding one, and these say
// where the looseness stops.
public class OnboardingLifecycleTests
{
    // Companies that do not run sixty and ninety day check-ins should not have to walk
    // every joiner through them, so skipping ahead is allowed.
    [Fact]
    public void StagesCanBeSkipped()
    {
        Assert.True(OnboardingLifecycle.CanMove(OnboardingStage.PreBoarding, OnboardingStage.ThirtyDay));
        Assert.True(OnboardingLifecycle.CanMove(OnboardingStage.DayOne, OnboardingStage.NinetyDay));
    }

    // Somebody who advances a record by mistake can put it back. Nothing irreversible
    // happened, so refusing would be ceremony.
    [Fact]
    public void StagesCanGoBack()
    {
        Assert.True(OnboardingLifecycle.CanMove(OnboardingStage.ThirtyDay, OnboardingStage.DayOne));
    }

    // Completing goes through CanComplete and its review check, so it must not be
    // reachable by the unguarded path that everything else uses.
    [Fact]
    public void CompletingIsNotAnOrdinaryMove()
    {
        Assert.False(OnboardingLifecycle.CanMove(OnboardingStage.NinetyDay, OnboardingStage.Completed));
    }

    [Fact]
    public void ACompletedRecordDoesNotMove()
    {
        Assert.False(OnboardingLifecycle.CanMove(OnboardingStage.Completed, OnboardingStage.ThirtyDay));
        Assert.True(OnboardingLifecycle.IsFinal(OnboardingStage.Completed));
    }

    // The rule the whole machine exists for. Closing a record whose pack nobody looked at
    // makes "onboarding complete" mean nothing.
    [Theory]
    [InlineData(OnboardingSubmission.NotStarted, true, false)]
    [InlineData(OnboardingSubmission.InProgress, true, false)]
    [InlineData(OnboardingSubmission.Submitted, false, false)]
    [InlineData(OnboardingSubmission.Submitted, true, true)]
    public void CompletingNeedsBothASubmissionAndAnApproval(
        OnboardingSubmission submission,
        bool reviewApproved,
        bool expected) =>
        Assert.Equal(expected, OnboardingLifecycle.CanComplete(submission, reviewApproved));

    [Fact]
    public void APackCanOnlyBeHandedInOnce()
    {
        Assert.True(OnboardingLifecycle.CanSubmit(OnboardingSubmission.NotStarted));
        Assert.True(OnboardingLifecycle.CanSubmit(OnboardingSubmission.InProgress));
        Assert.False(OnboardingLifecycle.CanSubmit(OnboardingSubmission.Submitted));
    }

    // Returned means it is theirs again, which has to include being able to resubmit —
    // leaving it Submitted would lock the joiner out of fixing what HR asked about.
    [Fact]
    public void AReturnedPackBecomesADraftAgain()
    {
        OnboardingSubmission afterReturn = OnboardingLifecycle.OnReturned();

        Assert.Equal(OnboardingSubmission.InProgress, afterReturn);
        Assert.True(OnboardingLifecycle.CanSubmit(afterReturn));
    }
}
