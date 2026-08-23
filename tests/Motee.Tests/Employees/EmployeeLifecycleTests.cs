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

    [Fact]
    public void OffboardingLeadsOnlyToLeavingOrBeingDeleted()
    {
        Assert.Equal(
            [EmployeeStatus.Inactive, EmployeeStatus.Deleted],
            EmployeeLifecycle.NextFrom(EmployeeStatus.Offboarding));
    }

    [Fact]
    public void TheAllowedMovesAreDiscoverable()
    {
        Assert.Contains(EmployeeStatus.Onboarded, EmployeeLifecycle.NextFrom(EmployeeStatus.Pending));
        Assert.DoesNotContain(EmployeeStatus.Active, EmployeeLifecycle.NextFrom(EmployeeStatus.Inactive));
    }
}
