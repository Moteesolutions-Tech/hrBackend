using Motee.Domain.Approvals;

namespace Motee.Tests.Approvals;

// Two machines: what a person may ask for (ApprovalLifecycle) and what a decision on one
// step does to the run (ApprovalChainRules). Ten features will sit on these, so a wrong
// rule here is wrong ten times over.
public class ApprovalLifecycleTests
{
    [Theory]
    [InlineData(ApprovalStatus.Draft, ApprovalAction.Submit, ApprovalStatus.InProgress)]
    [InlineData(ApprovalStatus.InProgress, ApprovalAction.Approve, ApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.InProgress, ApprovalAction.Reject, ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.InProgress, ApprovalAction.Return, ApprovalStatus.Returned)]
    [InlineData(ApprovalStatus.Returned, ApprovalAction.Resubmit, ApprovalStatus.InProgress)]
    public void TheChainMovesAsDescribed(
        ApprovalStatus from,
        ApprovalAction action,
        ApprovalStatus expected)
    {
        Assert.True(ApprovalLifecycle.CanApply(from, action));
        Assert.Equal(expected, ApprovalLifecycle.Next(from, action));
    }

    // A decided approval is the evidence it was decided. Reopening one would let the
    // record disagree with what the people who signed it actually saw.
    [Theory]
    [InlineData(ApprovalStatus.Approved)]
    [InlineData(ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.Cancelled)]
    public void ADecidedApprovalIsClosedForGood(ApprovalStatus status)
    {
        Assert.True(ApprovalLifecycle.IsFinal(status));
        Assert.False(ApprovalLifecycle.CanApply(status, ApprovalAction.Approve));
        Assert.False(ApprovalLifecycle.CanApply(status, ApprovalAction.Resubmit));
    }

    // Cancellable while it is still anybody's to act on, and not after.
    [Theory]
    [InlineData(ApprovalStatus.Draft, true)]
    [InlineData(ApprovalStatus.InProgress, true)]
    [InlineData(ApprovalStatus.Returned, true)]
    [InlineData(ApprovalStatus.Approved, false)]
    public void OnlyLiveApprovalsCanBeCancelled(ApprovalStatus status, bool expected) =>
        Assert.Equal(expected, ApprovalLifecycle.CanApply(status, ApprovalAction.Cancel));

    [Theory]
    [InlineData(ApprovalStatus.Draft, true)]
    [InlineData(ApprovalStatus.InProgress, true)]
    [InlineData(ApprovalStatus.Returned, true)]
    [InlineData(ApprovalStatus.Approved, false)]
    [InlineData(ApprovalStatus.Rejected, false)]
    [InlineData(ApprovalStatus.Cancelled, false)]
    public void OpenMeansSomebodyStillOwnsIt(ApprovalStatus status, bool expected) =>
        Assert.Equal(expected, ApprovalLifecycle.IsOpen(status));

    // A draft has not been sent, so there is nothing to approve yet.
    [Fact]
    public void ADraftCannotBeApproved()
    {
        Assert.False(ApprovalLifecycle.CanApply(ApprovalStatus.Draft, ApprovalAction.Approve));
        Assert.False(ApprovalLifecycle.CanApply(ApprovalStatus.Draft, ApprovalAction.Return));
    }

    // Only the step in front of an approver may be decided, or somebody further down the
    // chain could approve early and the order the template describes would mean nothing.
    [Theory]
    [InlineData(ApprovalStepStatus.Pending, ApprovalStatus.InProgress, true)]
    [InlineData(ApprovalStepStatus.Approved, ApprovalStatus.InProgress, false)]
    [InlineData(ApprovalStepStatus.Pending, ApprovalStatus.Returned, false)]
    [InlineData(ApprovalStepStatus.Pending, ApprovalStatus.Approved, false)]
    public void OnlyTheCurrentStepOfALiveRunCanBeDecided(
        ApprovalStepStatus step,
        ApprovalStatus instance,
        bool expected) =>
        Assert.Equal(expected, ApprovalChainRules.CanDecide(step, instance));

    // Approving a middle step leaves the run going; approving the last one finishes it.
    [Fact]
    public void ApprovingTheFinalStepApprovesTheChain()
    {
        Assert.Null(ApprovalChainRules.Outcome(ApprovalStepStatus.Approved, isLastStep: false));

        Assert.Equal(
            ApprovalStatus.Approved,
            ApprovalChainRules.Outcome(ApprovalStepStatus.Approved, isLastStep: true));
    }

    // There is no sense asking the next approver about something the last one sent back,
    // so both end the round wherever they happen.
    [Theory]
    [InlineData(ApprovalStepStatus.Rejected, ApprovalStatus.Rejected)]
    [InlineData(ApprovalStepStatus.Returned, ApprovalStatus.Returned)]
    public void ARejectionOrReturnEndsTheRoundImmediately(
        ApprovalStepStatus decision,
        ApprovalStatus expected)
    {
        Assert.Equal(expected, ApprovalChainRules.Outcome(decision, isLastStep: false));
        Assert.Equal(expected, ApprovalChainRules.Outcome(decision, isLastStep: true));
    }

    // A skipped step must not stall the chain — but skipping the last one still finishes
    // it, or an approval whose final step had nobody to ask would hang for ever.
    [Fact]
    public void ASkippedStepDoesNotStallTheChain()
    {
        Assert.Null(ApprovalChainRules.Outcome(ApprovalStepStatus.Skipped, isLastStep: false));

        Assert.Equal(
            ApprovalStatus.Approved,
            ApprovalChainRules.Outcome(ApprovalStepStatus.Skipped, isLastStep: true));
    }

    // The rules are served, so the client never keeps its own copy and cannot offer a
    // button the backend then refuses.
    [Fact]
    public void TheAvailableActionsAreDiscoverable()
    {
        Assert.Equal(
            [ApprovalAction.Submit, ApprovalAction.Cancel],
            ApprovalLifecycle.AvailableFrom(ApprovalStatus.Draft).Order());

        Assert.Empty(ApprovalLifecycle.AvailableFrom(ApprovalStatus.Approved));
    }
}
