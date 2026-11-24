using Motee.Domain.Common;

namespace Motee.Domain.Attendance;

// Where a rostered shift has got to.
//
// Derived, not stored. The screen keeps a status on the assignment, so a shift
// reads "scheduled" for ever after the day has passed — and "completed" is
// something the attendance record already knows.
public enum ShiftState
{
    // Rostered, still ahead.
    Scheduled,

    // The day has passed and somebody clocked in.
    Worked,

    // The day has passed and nobody did. The figure a rota exists to surface,
    // and one the screen has no way to show.
    Missed,

    // Called off. A decision somebody recorded, which is why this one is
    // stored and the others are not.
    Cancelled,
}

// One person on one shift pattern on one day.
//
// The pattern itself is a WorkSchedule. The screen models a ShiftTemplate with
// a name, a start and end time, break minutes and working days — which is
// work_schedules exactly, and that table already carries grace minutes, a
// default flag and an active flag on top. A second table of shift patterns
// would be a second answer to "what hours does the late shift run", and the
// attendance module judges lateness against the work schedule, so the roster
// would be measuring against a pattern nothing else reads.
public class ShiftAssignment : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    // The pattern being worked. A work schedule, not a shift template.
    public Guid WorkScheduleId { get; set; }

    public DateOnly Date { get; set; }

    // Set when the shift is called off. The reason it is a date rather than a
    // status is that cancelling is the only part of a shift's state somebody
    // decides — the rest follows from the calendar and the attendance record.
    public DateOnly? CancelledOn { get; set; }

    public string? Note { get; set; }

    public Guid? AssignedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ShiftRefusal
{
    None,
    EmployeeNotFound,
    ScheduleNotFound,
    ScheduleInactive,
    NotAWorkingDay,
    DateInThePast,
    AlreadyAssigned,
    RangeTooWide,
    RangeBackwards,
}

public static class Shifts
{
    // How much roster a single request may ask for. Three months: long enough
    // to plan a quarter, short enough that one call cannot try to render a
    // year of grid for every employee at once.
    public const int MaximumRosterDays = 93;

    // Where a shift stands, given the day and whether anybody clocked in.
    //
    // worked is passed in rather than looked up here, because whether somebody
    // attended is the attendance module's fact and this rule must not become a
    // second opinion about it.
    public static ShiftState StateOf(ShiftAssignment assignment, DateOnly today, bool worked)
    {
        if (assignment.CancelledOn is not null)
        {
            return ShiftState.Cancelled;
        }

        if (assignment.Date >= today)
        {
            return ShiftState.Scheduled;
        }

        return worked ? ShiftState.Worked : ShiftState.Missed;
    }

    public static ShiftRefusal Check(
        ShiftAssignment assignment,
        WorkSchedule schedule,
        DateOnly today,
        bool alreadyAssigned)
    {
        if (!schedule.IsActive)
        {
            return ShiftRefusal.ScheduleInactive;
        }

        // Rostering somebody onto a pattern that does not run that day is a
        // shift nobody can work, and the attendance module would judge the
        // whole day against hours the pattern says do not exist.
        if (!WorkSchedules.IsWorkingDay(schedule, assignment.Date.DayOfWeek))
        {
            return ShiftRefusal.NotAWorkingDay;
        }

        if (assignment.Date < today)
        {
            return ShiftRefusal.DateInThePast;
        }

        return alreadyAssigned ? ShiftRefusal.AlreadyAssigned : ShiftRefusal.None;
    }

    public static ShiftRefusal CheckRange(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            return ShiftRefusal.RangeBackwards;
        }

        return to.DayNumber - from.DayNumber >= MaximumRosterDays
            ? ShiftRefusal.RangeTooWide
            : ShiftRefusal.None;
    }
}

// A day on the roster for one person.
//
// Unrostered is a real answer and the important one. The client fills an empty
// day with defaultTemplateForEmployee "so the schedule always reads as
// populated instead of empty" — so the roster shows people on shifts nobody
// put them on, and a manager reads coverage that does not exist. A gap in a
// rota is the thing a rota is for.
public sealed record RosterDay
{
    public required DateOnly Date { get; init; }

    public Guid? AssignmentId { get; init; }

    public Guid? WorkScheduleId { get; init; }

    public string? ShiftName { get; init; }

    public TimeOnly? StartTime { get; init; }

    public TimeOnly? EndTime { get; init; }

    public int BreakMinutes { get; init; }

    public decimal PaidHours { get; init; }

    public string? Note { get; init; }

    // Null where nobody is rostered. Not a default shift, and not Scheduled:
    // an unrostered day has no state because there is no shift to be in one.
    public ShiftState? State { get; init; }

    public bool IsRostered => AssignmentId is not null;
}

public sealed record RosterRow
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? Initials { get; init; }

    public string? Department { get; init; }

    public required IReadOnlyList<RosterDay> Days { get; init; }

    // Days in the range with nobody rostered. The figure a manager checking
    // cover actually needs, and the one the client's invented defaults hide.
    public int UnrosteredDays => Days.Count(day => !day.IsRostered);

    public decimal RosteredHours =>
        Days.Where(day => day.State != ShiftState.Cancelled).Sum(day => day.PaidHours);
}
