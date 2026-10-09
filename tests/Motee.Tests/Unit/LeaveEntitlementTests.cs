using Motee.Domain.Leave;

namespace Motee.Tests.Unit;

// The arithmetic behind every balance on screen. Errors here are the kind people notice
// at the end of the year and cannot reconstruct.
public class LeaveEntitlementTests
{
    private static readonly LeaveYear Year2026 =
        LeaveYear.Containing(new DateOnly(2026, 6, 1), 1, 1);

    [Fact]
    public void AFullYearAccruesTheWholeEntitlement()
    {
        Assert.Equal(
            25m,
            LeaveEntitlement.AccruedBy(25m, Year2026, new DateOnly(2026, 12, 31)));
    }

    // Half the year earns half the days. Monthly rather than daily, because staff think
    // in months and payroll runs in months.
    [Fact]
    public void HalfAYearAccruesHalfTheEntitlement()
    {
        Assert.Equal(
            12m,
            LeaveEntitlement.AccruedBy(24m, Year2026, new DateOnly(2026, 6, 30)));
    }

    // A March joiner has not earned January and February. Accruing from the company's
    // year start would credit them for time before they existed.
    [Fact]
    public void AMidYearJoinerEarnsOnlyFromTheirStartDate()
    {
        decimal accrued = LeaveEntitlement.AccruedBy(
            24m, Year2026, new DateOnly(2026, 4, 30), joinedOn: new DateOnly(2026, 3, 1));

        // Ten months of eligibility, two of them earned: 24 × 10/12 × 2/10 = 4.
        Assert.Equal(4m, accrued);
    }

    [Fact]
    public void AMidYearJoinerNeverExceedsTheirProRataEntitlement()
    {
        decimal accrued = LeaveEntitlement.AccruedBy(
            24m, Year2026, new DateOnly(2026, 12, 31), joinedOn: new DateOnly(2026, 7, 1));

        // Six months of the year, so half the days — not the full 24.
        Assert.Equal(12m, accrued);
    }

    [Fact]
    public void NothingIsAccruedBeforeTheYearStarts()
    {
        Assert.Equal(
            0m,
            LeaveEntitlement.AccruedBy(25m, Year2026, new DateOnly(2025, 12, 31)));
    }

    // The one that bites: carried days lapse partway through the year, and offering them
    // afterwards lets somebody book October leave against days that ran out in March.
    [Fact]
    public void CarriedDaysLapseOnceTheirWindowHasPassed()
    {
        Assert.Equal(
            5m,
            LeaveEntitlement.CarriedOverAvailable(5m, Year2026, 3, new DateOnly(2026, 3, 1)));

        Assert.Equal(
            0m,
            LeaveEntitlement.CarriedOverAvailable(5m, Year2026, 3, new DateOnly(2026, 5, 1)));
    }

    [Fact]
    public void CarriedDaysWithNoExpiryLastTheWholeYear()
    {
        Assert.Equal(
            5m,
            LeaveEntitlement.CarriedOverAvailable(5m, Year2026, 0, new DateOnly(2026, 12, 1)));
    }

    // Pending requests reserve days. Without this somebody with ten days left and eight
    // awaiting a decision appears to have ten, books them again, and puts the
    // contradiction in front of an approver rather than in front of themselves.
    [Fact]
    public void PendingRequestsReserveDays()
    {
        Assert.Equal(
            2m,
            LeaveEntitlement.Available(
                entitled: 20m, carriedOverAvailable: 0m, used: 10m, pending: 8m, adjustments: 0m));
    }

    [Fact]
    public void AdjustmentsMoveTheBalanceInBothDirections()
    {
        Assert.Equal(
            22m,
            LeaveEntitlement.Available(20m, 0m, 0m, 0m, adjustments: 2m));

        Assert.Equal(
            18m,
            LeaveEntitlement.Available(20m, 0m, 0m, 0m, adjustments: -2m));
    }

    // Overspending is possible — HR grant leave in advance, and unpaid leave exists —
    // so the balance has to be able to go negative rather than clamping at zero and
    // hiding it.
    [Fact]
    public void ABalanceCanGoNegative()
    {
        Assert.Equal(
            -3m,
            LeaveEntitlement.Available(20m, 0m, 23m, 0m, 0m));
    }

    [Fact]
    public void CarryForwardIsCappedByPolicy()
    {
        Assert.Equal(5m, LeaveEntitlement.CarryForward(8m, carryOverAllowed: true, maxCarryOverDays: 5m));
        Assert.Equal(3m, LeaveEntitlement.CarryForward(3m, carryOverAllowed: true, maxCarryOverDays: 5m));
        Assert.Equal(0m, LeaveEntitlement.CarryForward(8m, carryOverAllowed: false, maxCarryOverDays: 5m));
    }

    // A negative balance is a debt, not something to carry forward as a credit.
    [Fact]
    public void NothingCarriesForwardFromANegativeBalance()
    {
        Assert.Equal(0m, LeaveEntitlement.CarryForward(-2m, carryOverAllowed: true, maxCarryOverDays: 5m));
    }

    // Half days are the smallest unit anybody books. Two decimals would produce 12.33
    // days, which is not a thing a person can take.
    [Fact]
    public void BalancesLandOnWholeOrHalfDays()
    {
        decimal accrued = LeaveEntitlement.AccruedBy(25m, Year2026, new DateOnly(2026, 5, 31));

        Assert.Equal(accrued * 2, Math.Truncate(accrued * 2));
    }
}

// The day count itself — what a request actually costs. The frontend counts Monday to
// Friday and ignores public holidays, which charges people for days the office was shut.
public class WorkingDaysTests
{
    private static readonly HashSet<DateOnly> None = [];

    private static readonly HashSet<DateOnly> Christmas2026 =
    [
        new(2026, 12, 25),
        new(2026, 12, 28),
    ];

    [Fact]
    public void WeekendsAreNotCharged()
    {
        // Friday to Monday inclusive: two working days.
        Assert.Equal(
            2m,
            WorkingDays.Between(new DateOnly(2026, 6, 5), new DateOnly(2026, 6, 8), None));
    }

    [Fact]
    public void PublicHolidaysAreNotCharged()
    {
        // 24th to 31st December 2026: Thursday to Thursday. Six weekdays, two of them
        // bank holidays.
        Assert.Equal(
            4m,
            WorkingDays.Between(
                new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 31), Christmas2026));
    }

    // A policy may say holidays inside a leave range still count. Rare, but it is a
    // policy question rather than a fact about calendars.
    [Fact]
    public void APolicyCanChooseToChargePublicHolidays()
    {
        Assert.Equal(
            6m,
            WorkingDays.Between(
                new DateOnly(2026, 12, 24),
                new DateOnly(2026, 12, 31),
                Christmas2026,
                excludePublicHolidays: false));
    }

    [Fact]
    public void AHalfDayCostsHalf()
    {
        Assert.Equal(
            0.5m,
            WorkingDays.ForRequest(
                new DateOnly(2026, 6, 5), new DateOnly(2026, 6, 5), isHalfDay: true, None));
    }

    // "Half of a fortnight" is not something anybody means, and honouring it would let
    // somebody book ten days and be charged five.
    [Fact]
    public void AHalfDayFlagIsIgnoredOnARange()
    {
        Assert.Equal(
            5m,
            WorkingDays.ForRequest(
                new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 5), isHalfDay: true, None));
    }

    // A single day that is itself a holiday costs nothing, so a half-day flag on it must
    // not conjure up half a day.
    [Fact]
    public void AHalfDayOnAClosedDayCostsNothing()
    {
        Assert.Equal(
            0m,
            WorkingDays.ForRequest(
                new DateOnly(2026, 12, 25),
                new DateOnly(2026, 12, 25),
                isHalfDay: true,
                Christmas2026));
    }

    [Fact]
    public void ABackwardsRangeCostsNothing()
    {
        Assert.Equal(
            0m,
            WorkingDays.Between(new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 1), None));
    }
}
