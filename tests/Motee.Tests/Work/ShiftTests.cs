using Motee.Domain.Attendance;

namespace Motee.Tests.Work;

// The roster: who is on which shift pattern on which day.
//
// Two things the screen gets wrong. It stores a status on the assignment, so a
// shift reads "scheduled" for ever after the day has passed — while whether
// somebody actually worked is something the attendance record already knows.
// And when no assignment exists it invents one, "so the schedule always reads
// as populated instead of empty" — so a manager checking cover reads coverage
// that does not exist. A gap in a rota is what a rota is for.
//
// Written before the rules.
public class ShiftTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static WorkSchedule Schedule(bool active = true, bool weekends = false)
    {
        Dictionary<DayOfWeek, WorkingHours> days = WorkSchedules.MondayToFriday(
            new TimeOnly(9, 0), new TimeOnly(17, 0));

        if (weekends)
        {
            days[DayOfWeek.Saturday] = new WorkingHours(new TimeOnly(9, 0), new TimeOnly(13, 0));
        }

        return new WorkSchedule
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "Day shift",
            Days = days,
            BreakMinutes = 60,
            IsActive = active,
        };
    }

    private static ShiftAssignment Assignment(
        Guid scheduleId,
        DateOnly? date = null,
        DateOnly? cancelled = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            EmployeeId = Guid.NewGuid(),
            WorkScheduleId = scheduleId,
            Date = date ?? Today.AddDays(1),
            CancelledOn = cancelled,
        };

    // ── where a shift stands ───────────────────────────────────────────────

    [Fact]
    public void AShiftStillAheadIsScheduled()
    {
        WorkSchedule schedule = Schedule();

        Assert.Equal(
            ShiftState.Scheduled,
            Shifts.StateOf(Assignment(schedule.Id, Today.AddDays(3)), Today, worked: false));
    }

    [Fact]
    public void TodaysShiftIsStillScheduled()
    {
        WorkSchedule schedule = Schedule();

        Assert.Equal(
            ShiftState.Scheduled,
            Shifts.StateOf(Assignment(schedule.Id, Today), Today, worked: false));
    }

    // Whether somebody attended is the attendance module's fact, passed in
    // rather than looked up here so this rule does not become a second opinion
    // about it.
    [Fact]
    public void APastShiftSomebodyWorkedIsWorked()
    {
        WorkSchedule schedule = Schedule();

        Assert.Equal(
            ShiftState.Worked,
            Shifts.StateOf(Assignment(schedule.Id, Today.AddDays(-1)), Today, worked: true));
    }

    // The figure a rota exists to surface, and one the screen has no way to
    // show: it would still read "scheduled".
    [Fact]
    public void APastShiftNobodyWorkedIsMissed()
    {
        WorkSchedule schedule = Schedule();

        Assert.Equal(
            ShiftState.Missed,
            Shifts.StateOf(Assignment(schedule.Id, Today.AddDays(-1)), Today, worked: false));
    }

    // Cancelling is the only part of a shift's state somebody decides, which is
    // why it is the only part stored.
    [Fact]
    public void ACancelledShiftStaysCancelledWhateverHappened()
    {
        WorkSchedule schedule = Schedule();

        Assert.Equal(
            ShiftState.Cancelled,
            Shifts.StateOf(
                Assignment(schedule.Id, Today.AddDays(-1), cancelled: Today.AddDays(-2)),
                Today,
                worked: true));
    }

    // ── what is refused ────────────────────────────────────────────────────

    // Rostering somebody onto a pattern that does not run that day is a shift
    // nobody can work, and attendance would judge the whole day against hours
    // the pattern says do not exist.
    [Fact]
    public void RosteringOntoADayThePatternDoesNotRunIsRefused()
    {
        WorkSchedule schedule = Schedule();

        // 26 September 2026 is a Saturday.
        Assert.Equal(
            ShiftRefusal.NotAWorkingDay,
            Shifts.Check(
                Assignment(schedule.Id, new DateOnly(2026, 9, 26)),
                schedule,
                Today,
                alreadyAssigned: false));
    }

    [Fact]
    public void APatternThatRunsWeekendsAcceptsASaturday()
    {
        WorkSchedule schedule = Schedule(weekends: true);

        Assert.Equal(
            ShiftRefusal.None,
            Shifts.Check(
                Assignment(schedule.Id, new DateOnly(2026, 9, 26)),
                schedule,
                Today,
                alreadyAssigned: false));
    }

    [Fact]
    public void AnInactivePatternCannotBeRostered()
    {
        WorkSchedule schedule = Schedule(active: false);

        Assert.Equal(
            ShiftRefusal.ScheduleInactive,
            Shifts.Check(Assignment(schedule.Id), schedule, Today, alreadyAssigned: false));
    }

    // Rostering somebody for last Tuesday changes nothing that already
    // happened, and attendance has already judged that day.
    [Fact]
    public void RosteringIntoThePastIsRefused()
    {
        WorkSchedule schedule = Schedule();

        // A week back, so it lands on a weekday: the working-day rule is
        // checked first, and a Saturday would be refused for being a Saturday
        // rather than for being in the past.
        Assert.Equal(
            ShiftRefusal.DateInThePast,
            Shifts.Check(
                Assignment(schedule.Id, Today.AddDays(-7)),
                schedule,
                Today,
                alreadyAssigned: false));
    }

    [Fact]
    public void TwoShiftsOnOneDayAreRefused()
    {
        WorkSchedule schedule = Schedule();

        Assert.Equal(
            ShiftRefusal.AlreadyAssigned,
            Shifts.Check(Assignment(schedule.Id), schedule, Today, alreadyAssigned: true));
    }

    [Fact]
    public void AGoodAssignmentIsAccepted()
    {
        WorkSchedule schedule = Schedule();

        Assert.Equal(
            ShiftRefusal.None,
            Shifts.Check(Assignment(schedule.Id), schedule, Today, alreadyAssigned: false));
    }

    // ── how much roster one request may ask for ────────────────────────────

    [Fact]
    public void ABackwardsRangeIsRefused()
    {
        Assert.Equal(
            ShiftRefusal.RangeBackwards,
            Shifts.CheckRange(Today, Today.AddDays(-1)));
    }

    // One call must not try to render a year of grid for every employee.
    [Fact]
    public void ARangeBeyondAQuarterIsRefused()
    {
        Assert.Equal(
            ShiftRefusal.RangeTooWide,
            Shifts.CheckRange(Today, Today.AddDays(Shifts.MaximumRosterDays)));
    }

    [Fact]
    public void AQuartersRangeIsFine()
    {
        Assert.Equal(
            ShiftRefusal.None,
            Shifts.CheckRange(Today, Today.AddDays(Shifts.MaximumRosterDays - 1)));
    }

    [Fact]
    public void ASingleDayIsFine()
    {
        Assert.Equal(ShiftRefusal.None, Shifts.CheckRange(Today, Today));
    }

    // ── an unrostered day ──────────────────────────────────────────────────

    // The defect: the client fills an empty day with a default so the schedule
    // "always reads as populated", and a manager reads coverage that does not
    // exist.
    [Fact]
    public void AnUnrosteredDayHasNoShiftAndNoState()
    {
        RosterDay day = new() { Date = Today };

        Assert.False(day.IsRostered);
        Assert.Null(day.State);
        Assert.Null(day.WorkScheduleId);
        Assert.Equal(0m, day.PaidHours);
    }

    [Fact]
    public void TheGapsInARowAreCounted()
    {
        RosterRow row = new()
        {
            EmployeeId = Guid.NewGuid(),
            EmployeeName = "Ada Okafor",
            Days =
            [
                new RosterDay { Date = Today, AssignmentId = Guid.NewGuid(), PaidHours = 7m },
                new RosterDay { Date = Today.AddDays(1) },
                new RosterDay { Date = Today.AddDays(2) },
            ],
        };

        Assert.Equal(2, row.UnrosteredDays);
        Assert.Equal(7m, row.RosteredHours);
    }

    // A cancelled shift is not hours anybody is working.
    [Fact]
    public void CancelledHoursAreNotRosteredHours()
    {
        RosterRow row = new()
        {
            EmployeeId = Guid.NewGuid(),
            EmployeeName = "Ada Okafor",
            Days =
            [
                new RosterDay
                {
                    Date = Today,
                    AssignmentId = Guid.NewGuid(),
                    PaidHours = 7m,
                    State = ShiftState.Scheduled,
                },
                new RosterDay
                {
                    Date = Today.AddDays(1),
                    AssignmentId = Guid.NewGuid(),
                    PaidHours = 7m,
                    State = ShiftState.Cancelled,
                },
            ],
        };

        Assert.Equal(7m, row.RosteredHours);

        // Still rostered, though — a cancelled shift is a decision somebody
        // made rather than a gap nobody filled.
        Assert.Equal(0, row.UnrosteredDays);
    }
}
