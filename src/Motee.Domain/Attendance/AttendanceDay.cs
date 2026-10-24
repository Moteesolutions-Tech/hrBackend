namespace Motee.Domain.Attendance;

public enum AttendanceStatus
{
    // Nothing recorded, and the day is one they were meant to work. Not a punch — the
    // absence of one, which is why it can only be concluded once the day is over.
    Absent,

    Present,

    // Arrived after the scheduled start plus the grace period.
    Late,

    // Left before the scheduled end. Recorded alongside Late rather than instead of
    // it being impossible — somebody can do both, and the status keeps the first.
    EarlyDeparture,

    // Approved leave covers the day, so there was nothing to turn up for. Distinct
    // from Absent, which is the whole point: one is a booking and the other is a
    // problem, and a system that confuses them generates accusations.
    OnLeave,

    // A working day, still in progress, with no clock-in yet. Becomes Absent when the
    // day closes, and is the honest answer before then — somebody who starts at ten
    // is not absent at nine.
    NotClockedIn,
}

// What one day's punches amount to, against the schedule that day was meant to follow.
//
// A pure function over instants, like WorkingDays. Every figure here is derived on
// read and none of it is stored: a running total drifts the moment anything is edited
// after the fact, and attendance is edited after the fact constantly — corrections,
// retroactive leave, a manager fixing a forgotten clock-out.
public static class AttendanceDay
{
    // Worked minutes: the span from first in to last out, less every break taken.
    //
    // Breaks are the ones recorded, not the ones the schedule assumes. A person who
    // worked through lunch worked through lunch, and docking them for a break they
    // did not take is the kind of quiet theft nobody admits to designing.
    public static decimal WorkedHours(
        DateTimeOffset clockIn,
        DateTimeOffset clockOut,
        int breakMinutesTaken)
    {
        decimal minutes = (decimal)(clockOut - clockIn).TotalMinutes - breakMinutesTaken;

        return Math.Max(0m, Math.Round(minutes / 60m, 2));
    }

    // Hours beyond the contract. Never negative: undertime is a different conversation
    // and folding it in here would let a short Monday cancel a long Tuesday.
    public static decimal Overtime(decimal workedHours, decimal scheduledHours) =>
        Math.Max(0m, Math.Round(workedHours - scheduledHours, 2));

    // Whether an arrival counts as late, with the grace period added to the scheduled
    // start rather than subtracted from the arrival. The two give the same answer and
    // only one of them survives being explained to the person it is about.
    public static bool IsLate(TimeOnly arrived, TimeOnly scheduledStart, int graceMinutes) =>
        arrived > scheduledStart.AddMinutes(graceMinutes);

    // Short of the statutory break. A requirement of zero is always met, which matters
    // because the break is optional on a schedule and absent means none is owed.
    public static int BreakShortfall(int takenMinutes, int requiredMinutes) =>
        Math.Max(0, requiredMinutes - takenMinutes);

    // The status of a day, from what was recorded against what was expected.
    //
    // Leave outranks everything: an approved absence is not lateness and not an
    // absence to answer for, whatever the punches say — and somebody who came in
    // briefly on a booked day off should not be marked late for it.
    public static AttendanceStatus Status(
        WorkSchedule schedule,
        DayOfWeek day,
        bool onApprovedLeave,
        TimeOnly? arrived,
        TimeOnly? left,
        bool dayIsOver)
    {
        if (onApprovedLeave)
        {
            return AttendanceStatus.OnLeave;
        }

        if (!schedule.Days.TryGetValue(day, out WorkingHours hours))
        {
            // Not a working day, so there is no start time to be late against and
            // nothing to have failed to turn up for. Somebody who came in anyway is
            // present, and all of it is overtime.
            //
            // With no punch it is NotClockedIn rather than Present, and never Absent.
            // This is reached every weekend — the clock widget asks for today whether
            // or not today is a working day — and reporting the whole company present
            // on a Saturday is as wrong as reporting them absent.
            return arrived is null ? AttendanceStatus.NotClockedIn : AttendanceStatus.Present;
        }

        if (arrived is null)
        {
            // The distinction that stops a 10am starter being reported absent at 9:01.
            return dayIsOver ? AttendanceStatus.Absent : AttendanceStatus.NotClockedIn;
        }

        if (IsLate(arrived.Value, hours.Start, schedule.GraceMinutes))
        {
            return AttendanceStatus.Late;
        }

        return left is TimeOnly departed && departed < hours.End
            ? AttendanceStatus.EarlyDeparture
            : AttendanceStatus.Present;
    }
}
