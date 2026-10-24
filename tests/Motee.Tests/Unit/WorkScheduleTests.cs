using Motee.Domain.Attendance;

namespace Motee.Tests.Unit;

// The arithmetic every attendance figure is built on.
//
// Pure, so these are the cheap tests — and the ones worth having, because the errors
// here are the quiet kind: an hour too many on every timesheet, or a night shift
// recorded as a negative day, found at the end of the month by somebody checking pay.
public class WorkScheduleTests
{
    private static WorkSchedule Schedule(
        TimeOnly start,
        TimeOnly end,
        int breakMinutes = 60,
        int graceMinutes = 10) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Standard",
        Days = WorkSchedules.MondayToFriday(start, end),
        BreakMinutes = breakMinutes,
        GraceMinutes = graceMinutes,
    };

    // The number that has to reconcile against a contract: 09:00–17:30 with an hour
    // for lunch is seven and a half paid hours, not eight and a half.
    [Fact]
    public void PaidHoursExcludeTheUnpaidBreak()
    {
        Assert.Equal(
            7.5m,
            WorkSchedules.PaidHoursOn(
                Schedule(new TimeOnly(9, 0), new TimeOnly(17, 30)),
                DayOfWeek.Monday));
    }

    // A shift crossing midnight ends before it starts on the clock. Wrapped rather
    // than clamped, so nights are eight hours and not an error somebody discovers.
    [Fact]
    public void ANightShiftWrapsPastMidnight()
    {
        Assert.Equal(
            7.5m,
            WorkSchedules.PaidHoursOn(
                Schedule(new TimeOnly(22, 0), new TimeOnly(6, 0), breakMinutes: 30),
                DayOfWeek.Monday));
    }

    [Fact]
    public void ADayOffTheScheduleIsNotAWorkingDay()
    {
        WorkSchedule schedule = Schedule(new TimeOnly(9, 0), new TimeOnly(17, 0));

        Assert.False(WorkSchedules.IsWorkingDay(schedule, DayOfWeek.Saturday));
        Assert.Equal(0m, WorkSchedules.PaidHoursOn(schedule, DayOfWeek.Saturday));
    }

    // Derived from the days rather than stored beside them, so the two cannot drift.
    [Fact]
    public void WeeklyHoursAddUpTheWorkingDays()
    {
        Assert.Equal(
            37.5m,
            WorkSchedules.WeeklyHours(Schedule(new TimeOnly(9, 0), new TimeOnly(17, 30))));
    }

    // A compressed week is expressed by leaving days out, not by a separate flag.
    [Fact]
    public void AFourDayWeekIsFourDaysOfHours()
    {
        WorkSchedule schedule = Schedule(new TimeOnly(9, 0), new TimeOnly(17, 0));
        schedule.Days.Remove(DayOfWeek.Friday);

        Assert.Equal(28m, WorkSchedules.WeeklyHours(schedule));
    }

    // The grace period is added to the scheduled start rather than subtracted from the
    // arrival. Same answer; only one of them survives being explained to the person.
    [Theory]
    [InlineData(9, 0, false)]
    [InlineData(9, 10, false)]
    [InlineData(9, 11, true)]
    public void LatenessIsJudgedAgainstTheStartPlusGrace(int hour, int minute, bool late)
    {
        Assert.Equal(
            late,
            AttendanceDay.IsLate(new TimeOnly(hour, minute), new TimeOnly(9, 0), 10));
    }

    // Breaks taken, not breaks assumed. Somebody who worked through lunch worked
    // through lunch, and docking them for it is quiet theft.
    [Fact]
    public void WorkedHoursSubtractOnlyTheBreaksActuallyTaken()
    {
        DateTimeOffset start = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(8m, AttendanceDay.WorkedHours(start, start.AddHours(8), 0));
        Assert.Equal(7m, AttendanceDay.WorkedHours(start, start.AddHours(8), 60));
    }

    // Undertime is a different conversation. Folding it in here would let a short
    // Monday cancel a long Tuesday.
    [Fact]
    public void UndertimeIsNotNegativeOvertime()
    {
        Assert.Equal(0m, AttendanceDay.Overtime(6m, 7.5m));
        Assert.Equal(1.5m, AttendanceDay.Overtime(9m, 7.5m));
    }

    [Fact]
    public void ABreakRequirementOfNoneIsAlwaysMet()
    {
        Assert.Equal(0, AttendanceDay.BreakShortfall(0, 0));
        Assert.Equal(20, AttendanceDay.BreakShortfall(40, 60));
    }

    // Leave outranks everything. Somebody who looked in briefly on a booked day off
    // must not be marked late for it.
    [Fact]
    public void ApprovedLeaveOutranksThePunches()
    {
        Assert.Equal(
            AttendanceStatus.OnLeave,
            AttendanceDay.Status(
                Schedule(new TimeOnly(9, 0), new TimeOnly(17, 0)),
                DayOfWeek.Monday,
                onApprovedLeave: true,
                arrived: new TimeOnly(11, 30),
                left: new TimeOnly(12, 0),
                dayIsOver: true));
    }

    // The distinction that stops a ten o'clock starter being reported absent at 9:01.
    [Theory]
    [InlineData(false, AttendanceStatus.NotClockedIn)]
    [InlineData(true, AttendanceStatus.Absent)]
    public void NoArrivalIsOnlyAbsenceOnceTheDayIsOver(bool dayIsOver, AttendanceStatus expected)
    {
        Assert.Equal(
            expected,
            AttendanceDay.Status(
                Schedule(new TimeOnly(9, 0), new TimeOnly(17, 0)),
                DayOfWeek.Monday,
                onApprovedLeave: false,
                arrived: null,
                left: null,
                dayIsOver));
    }

    // Nobody is absent on a day they were never due to work.
    [Fact]
    public void ANonWorkingDayIsNeverAbsence()
    {
        Assert.Equal(
            AttendanceStatus.Present,
            AttendanceDay.Status(
                Schedule(new TimeOnly(9, 0), new TimeOnly(17, 0)),
                DayOfWeek.Sunday,
                onApprovedLeave: false,
                arrived: new TimeOnly(10, 0),
                left: new TimeOnly(14, 0),
                dayIsOver: true));
    }

    [Fact]
    public void LeavingBeforeTheScheduledEndIsAnEarlyDeparture()
    {
        Assert.Equal(
            AttendanceStatus.EarlyDeparture,
            AttendanceDay.Status(
                Schedule(new TimeOnly(9, 0), new TimeOnly(17, 0)),
                DayOfWeek.Monday,
                onApprovedLeave: false,
                arrived: new TimeOnly(9, 0),
                left: new TimeOnly(15, 0),
                dayIsOver: true));
    }

    // Somebody can arrive late and leave early. The status keeps the first, because
    // one label cannot hold both and lateness is the one with a start time behind it.
    [Fact]
    public void LatenessIsKeptOverAnEarlyDeparture()
    {
        Assert.Equal(
            AttendanceStatus.Late,
            AttendanceDay.Status(
                Schedule(new TimeOnly(9, 0), new TimeOnly(17, 0)),
                DayOfWeek.Monday,
                onApprovedLeave: false,
                arrived: new TimeOnly(10, 0),
                left: new TimeOnly(15, 0),
                dayIsOver: true));
    }
}
