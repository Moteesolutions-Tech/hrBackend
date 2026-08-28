using Motee.Domain.Offboarding;

namespace Motee.Tests.Offboarding;

// The rules, tested without a database. An exit record is the one place where a wrong
// transition has consequences outside the app — someone's access, their final pay, and
// whether they are still employed.
public class OffboardingLifecycleTests
{
    [Theory]
    [InlineData(OffboardingStatus.Pending, OffboardingAction.Approve, OffboardingStatus.Approved)]
    [InlineData(OffboardingStatus.Pending, OffboardingAction.Disapprove, OffboardingStatus.Disapproved)]
    [InlineData(OffboardingStatus.Approved, OffboardingAction.StartClearance, OffboardingStatus.InProgress)]
    [InlineData(OffboardingStatus.Approved, OffboardingAction.Complete, OffboardingStatus.Completed)]
    [InlineData(OffboardingStatus.InProgress, OffboardingAction.Complete, OffboardingStatus.Completed)]
    public void TheHappyPathMovesForward(
        OffboardingStatus from,
        OffboardingAction action,
        OffboardingStatus expected)
    {
        Assert.True(OffboardingLifecycle.CanApply(from, action));
        Assert.Equal(expected, OffboardingLifecycle.Next(from, action));
    }

    // Notice can be withdrawn right up until the person actually goes.
    [Theory]
    [InlineData(OffboardingStatus.Pending)]
    [InlineData(OffboardingStatus.Approved)]
    [InlineData(OffboardingStatus.InProgress)]
    [InlineData(OffboardingStatus.Disapproved)]
    public void NoticeCanBeWithdrawnBeforeTheyLeave(OffboardingStatus from)
    {
        Assert.Equal(
            OffboardingStatus.Reactivated,
            OffboardingLifecycle.Next(from, OffboardingAction.Reactivate));
    }

    // The one that matters most. Once someone has left, a button must not undo it:
    // bringing them back is a rehire, and EmployeeLifecycle already refuses to move
    // Inactive anywhere. Allowing it here would let the two rules disagree.
    [Fact]
    public void ADepartureCannotBeUndone()
    {
        Assert.False(OffboardingLifecycle.CanApply(
            OffboardingStatus.Completed, OffboardingAction.Reactivate));

        Assert.True(OffboardingLifecycle.IsFinal(OffboardingStatus.Completed));
    }

    [Theory]
    [InlineData(OffboardingStatus.Completed, OffboardingAction.Approve)]
    [InlineData(OffboardingStatus.Completed, OffboardingAction.Complete)]
    [InlineData(OffboardingStatus.Disapproved, OffboardingAction.Approve)]
    [InlineData(OffboardingStatus.Disapproved, OffboardingAction.Complete)]
    [InlineData(OffboardingStatus.Reactivated, OffboardingAction.Approve)]
    [InlineData(OffboardingStatus.Pending, OffboardingAction.Complete)]
    [InlineData(OffboardingStatus.Pending, OffboardingAction.StartClearance)]
    public void EverythingElseIsRefused(OffboardingStatus from, OffboardingAction action)
    {
        Assert.False(OffboardingLifecycle.CanApply(from, action));
        Assert.Null(OffboardingLifecycle.Next(from, action));
    }

    // Approving twice is the mistake a plain status column invites: the second one looks
    // successful and quietly overwrites who decided and when.
    [Fact]
    public void ADecisionCannotBeTakenTwice()
    {
        Assert.False(OffboardingLifecycle.CanApply(
            OffboardingStatus.Approved, OffboardingAction.Approve));

        Assert.False(OffboardingLifecycle.CanApply(
            OffboardingStatus.Disapproved, OffboardingAction.Disapprove));
    }

    // "Open" is what stops a second exit being opened for someone already leaving.
    [Theory]
    [InlineData(OffboardingStatus.Pending, true)]
    [InlineData(OffboardingStatus.Approved, true)]
    [InlineData(OffboardingStatus.InProgress, true)]
    [InlineData(OffboardingStatus.Completed, false)]
    [InlineData(OffboardingStatus.Disapproved, false)]
    [InlineData(OffboardingStatus.Reactivated, false)]
    public void OnlyLiveExitsCountAsOpen(OffboardingStatus status, bool expected) =>
        Assert.Equal(expected, OffboardingLifecycle.IsOpen(status));

    // Reactivated is an ending too: the employee stayed, so this record has nothing
    // left to say. Starting again means a new record, which keeps the first one's
    // history intact.
    [Fact]
    public void WithdrawnAndRefusedExitsAreFinal()
    {
        Assert.True(OffboardingLifecycle.IsFinal(OffboardingStatus.Reactivated));

        // Disapproved is not final — it can still be withdrawn, which is how a refused
        // exit is cleared off the pipeline rather than sitting there for ever.
        Assert.False(OffboardingLifecycle.IsFinal(OffboardingStatus.Disapproved));
    }

    // The frontend's action matrix comes from here rather than being duplicated, so a
    // button cannot be enabled for something the backend then refuses.
    [Fact]
    public void TheAvailableActionsAreServedNotGuessed()
    {
        Assert.Equal(
            [OffboardingAction.Approve, OffboardingAction.Disapprove, OffboardingAction.Reactivate],
            OffboardingLifecycle.AvailableFrom(OffboardingStatus.Pending).Order());

        Assert.Empty(OffboardingLifecycle.AvailableFrom(OffboardingStatus.Completed));
    }
}
