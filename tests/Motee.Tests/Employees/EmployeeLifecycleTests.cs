using Motee.Domain.Employees;

namespace Motee.Tests.Employees;

public class EmployeeLifecycleTests
{
    [Theory]
    [InlineData(EmployeeStatus.Pending, EmployeeStatus.Onboarded)]
    [InlineData(EmployeeStatus.Onboarded, EmployeeStatus.Probation)]
    [InlineData(EmployeeStatus.Onboarded, EmployeeStatus.Active)]
    [InlineData(EmployeeStatus.Probation, EmployeeStatus.Active)]
    [InlineData(EmployeeStatus.Active, EmployeeStatus.OnLeave)]
    [InlineData(EmployeeStatus.OnLeave, EmployeeStatus.Active)]
    [InlineData(EmployeeStatus.Active, EmployeeStatus.Offboarding)]
    [InlineData(EmployeeStatus.Offboarding, EmployeeStatus.Inactive)]
    public void AllowsTheOrdinaryProgression(EmployeeStatus from, EmployeeStatus to)
    {
        Assert.True(EmployeeLifecycle.CanMove(from, to), $"{from} -> {to}");
    }

    // Someone who has left cannot quietly reappear on the payroll. Rehiring is a new
    // record, or an explicit reinstatement — not a dropdown change.
    [Theory]
    [InlineData(EmployeeStatus.Inactive, EmployeeStatus.Active)]
    [InlineData(EmployeeStatus.Inactive, EmployeeStatus.OnLeave)]
    [InlineData(EmployeeStatus.Inactive, EmployeeStatus.Probation)]
    public void ALeaverCannotBeMovedBackToWorking(EmployeeStatus from, EmployeeStatus to)
    {
        Assert.False(EmployeeLifecycle.CanMove(from, to), $"{from} -> {to}");
    }

    // A newly recorded person may already be established staff — importing an
    // existing workforce must not walk them through onboarding they finished years
    // ago.
    [Theory]
    [InlineData(EmployeeStatus.Active)]
    [InlineData(EmployeeStatus.Probation)]
    [InlineData(EmployeeStatus.Inactive)]
    public void PendingCanGoStraightToAnyLaterState(EmployeeStatus to)
    {
        Assert.True(EmployeeLifecycle.CanMove(EmployeeStatus.Pending, to));
    }

    // Probation is entered once, on joining.
    [Theory]
    [InlineData(EmployeeStatus.Active, EmployeeStatus.Probation)]
    [InlineData(EmployeeStatus.OnLeave, EmployeeStatus.Probation)]
    [InlineData(EmployeeStatus.Active, EmployeeStatus.Pending)]
    [InlineData(EmployeeStatus.Active, EmployeeStatus.Onboarded)]
    public void CannotGoBackwardsThroughOnboarding(EmployeeStatus from, EmployeeStatus to)
    {
        Assert.False(EmployeeLifecycle.CanMove(from, to), $"{from} -> {to}");
    }

    // The Deleted tab is a recycle bin, so this one is reversible.
    [Fact]
    public void DeletingIsSoftAndRecoverable()
    {
        Assert.True(EmployeeLifecycle.CanMove(EmployeeStatus.Active, EmployeeStatus.Deleted));
        Assert.True(EmployeeLifecycle.CanMove(EmployeeStatus.Deleted, EmployeeStatus.Active));
    }

    [Fact]
    public void AnythingCanBeDeleted()
    {
        Assert.All(Enum.GetValues<EmployeeStatus>(), status =>
        {
            if (status != EmployeeStatus.Deleted)
            {
                Assert.True(EmployeeLifecycle.CanMove(status, EmployeeStatus.Deleted), status.ToString());
            }
        });
    }

    // Saving a form without touching the status must not be rejected.
    [Fact]
    public void StayingPutIsAlwaysAllowed()
    {
        Assert.All(Enum.GetValues<EmployeeStatus>(), status =>
            Assert.True(EmployeeLifecycle.CanMove(status, status), status.ToString()));
    }

    [Fact]
    public void EveryStatusIsReachableFromSomewhere()
    {
        EmployeeStatus[] all = Enum.GetValues<EmployeeStatus>();

        Assert.All(all.Where(status => status != EmployeeStatus.Pending), target =>
            Assert.True(
                all.Any(from => from != target && EmployeeLifecycle.CanMove(from, target)),
                $"nothing reaches {target}"));
    }

    // Notice gets withdrawn: someone resigns, is talked out of it, and stays. That is
    // not the same as rehiring a leaver, which is still refused from Inactive below.
    //
    // This originally allowed only Inactive, and the effect was that reactivating an
    // offboarding left the exit record saying the departure was cancelled while the
    // employee row still said they were on their way out.
    [Fact]
    public void OffboardingLeadsToLeavingOrToStayingAfterAll()
    {
        Assert.Equal(
            [EmployeeStatus.Active, EmployeeStatus.Inactive, EmployeeStatus.Deleted],
            EmployeeLifecycle.NextFrom(EmployeeStatus.Offboarding));
    }

    // The rule that did not change. Once somebody has actually gone, bringing them back
    // is a rehire — a new record or an explicit reinstatement — not a status flip.
    [Fact]
    public void ALeaverStaysALeaver()
    {
        Assert.False(EmployeeLifecycle.CanMove(EmployeeStatus.Inactive, EmployeeStatus.Active));
    }

    [Fact]
    public void TheAllowedMovesAreDiscoverable()
    {
        Assert.Contains(EmployeeStatus.Onboarded, EmployeeLifecycle.NextFrom(EmployeeStatus.Pending));
        Assert.DoesNotContain(EmployeeStatus.Active, EmployeeLifecycle.NextFrom(EmployeeStatus.Inactive));
    }
}
