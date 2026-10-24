using Motee.Domain.Common;

namespace Motee.Domain.Attendance;

// The hours somebody is contracted to work, as a named pattern a company reuses.
//
// Attendance has no opinions of its own. "Late", "overtime", "absent" and whether a
// statutory break was taken are all answers relative to a schedule, and judging a
// four-day week or a 09:00–17:30 shift against a hardcoded nine-to-five produces
// confident nonsense. Everything in the module reads the day from here.
//
// Named and shared rather than written onto each employee, because a company has a
// handful of patterns and hundreds of people on them. Changing the office hours should
// be one edit, not a migration across the directory — and "who is on lates?" should be
// a question with an answer.
public class WorkSchedule : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The company's own words: "Standard", "Night Shift", "Part-time Tuesdays".
    public required string Name { get; set; }

    // Which days are working days, and the hours on each. A day absent from the map is
    // not worked — which is how a compressed week is expressed, rather than by a
    // separate flag that could disagree with the hours beside it.
    public required Dictionary<DayOfWeek, WorkingHours> Days { get; set; }

    // Unpaid, and the same on every working day. Subtracted from the span, so
    // 09:00–17:30 with sixty minutes is seven and a half paid hours rather than eight
    // and a half — the figure that has to reconcile against a contract.
    //
    // One number rather than per-day because no company has yet asked for a longer
    // lunch on Wednesdays, and the shape is easy to widen later and awkward to narrow.
    public int BreakMinutes { get; set; }

    // How late somebody can be before it is recorded as lateness.
    //
    // Here rather than in the deduction policy because it decides what the attendance
    // record says, which matters whether or not money is attached. A thirty-second
    // overrun becoming a disciplinary data point is how a system loses the room.
    public int GraceMinutes { get; set; } = 10;

    // Given to anybody hired without a choice being made. Exactly one per tenant.
    //
    // Without a default, every new employee would have no schedule, and attendance
    // would record punches it could not judge — present, but late or not? The honest
    // answer would be "unknown", on every row, until somebody noticed.
    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// The hours of one working day. Local times, deliberately: a shift starts at nine in
// the morning where the person is, and storing an instant would move it whenever the
// clocks changed.
public readonly record struct WorkingHours(TimeOnly Start, TimeOnly End);

public static class WorkSchedules
{
    // Monday to Friday, nine to five, an hour for lunch.
    //
    // Seeded for a new tenant, unlike departments, which are left empty on purpose.
    // The difference is that a department cannot be guessed — only the company knows
    // which ones exist — while a standard working week has one obvious answer that is
    // right for most and wrong harmlessly for the rest, and is one edit away. Without
    // it the module cannot judge a single punch on day one.
    public static Dictionary<DayOfWeek, WorkingHours> MondayToFriday(
        TimeOnly start,
        TimeOnly end) =>
        new()
        {
            [DayOfWeek.Monday] = new(start, end),
            [DayOfWeek.Tuesday] = new(start, end),
            [DayOfWeek.Wednesday] = new(start, end),
            [DayOfWeek.Thursday] = new(start, end),
            [DayOfWeek.Friday] = new(start, end),
        };

    // Paid hours in one day: the span, less the unpaid break. Zero on a day that is
    // not worked, which is the same answer as "there is no schedule for it".
    public static decimal PaidHoursOn(WorkSchedule schedule, DayOfWeek day) =>
        schedule.Days.TryGetValue(day, out WorkingHours hours)
            ? PaidHours(hours, schedule.BreakMinutes)
            : 0m;

    public static decimal PaidHours(WorkingHours hours, int breakMinutes)
    {
        decimal span = (decimal)(hours.End - hours.Start).TotalMinutes;

        // A shift crossing midnight ends before it starts on the clock. Wrapped rather
        // than clamped to zero, so a 22:00–06:00 night shift is eight hours and not an
        // error somebody has to discover.
        if (span < 0)
        {
            span += (decimal)TimeSpan.FromDays(1).TotalMinutes;
        }

        return Math.Max(0m, Math.Round((span - breakMinutes) / 60m, 2));
    }

    // What the contract adds up to across the week. Derived rather than stored beside
    // the days, which would let the two disagree the first time somebody edits one.
    public static decimal WeeklyHours(WorkSchedule schedule) =>
        schedule.Days.Keys.Sum(day => PaidHoursOn(schedule, day));

    public static bool IsWorkingDay(WorkSchedule schedule, DayOfWeek day) =>
        schedule.Days.ContainsKey(day);
}
