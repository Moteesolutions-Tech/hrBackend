using Motee.Domain.Common;

namespace Motee.Domain.Attendance;

public enum WorkLocation
{
    Office,
    Remote,
    ClientSite,
}

// How the punch reached us. Kept because the three are not equally trustworthy: a
// biometric reader is a claim about a body at a door, and a web punch is a claim about
// a browser. Anybody auditing a disputed day needs to know which they are looking at.
public enum PunchSource
{
    Web,
    Mobile,
    Biometric,

    // Typed in afterwards by HR, for somebody who forgot or whose reader was down.
    // Not a punch at all, and labelled so it can never be mistaken for one.
    Manual,
}

// One person, one day.
//
// Stores the instants it was told about and nothing derived from them. Worked hours,
// overtime, lateness and status are all recomputed on read, because attendance is
// edited after the fact constantly — a forgotten clock-out, a correction, leave
// approved retroactively — and a stored total is a number that was true once.
public class AttendanceRecord : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    // The company's calendar day, not UTC's. A punch at half past midnight in Lagos
    // belongs to the day it is there, and deciding that in UTC files the first half
    // hour of every night shift under yesterday.
    public DateOnly Date { get; set; }

    public DateTimeOffset? ClockInAt { get; set; }

    public DateTimeOffset? ClockOutAt { get; set; }

    // Every break taken, open-ended while one is running. A list rather than a total
    // for the same reason as everything else here: the total is derivable and the
    // instants are not.
    public List<BreakInterval> Breaks { get; set; } = [];

    public WorkLocation Location { get; set; }

    // The desk, site or office in the company's own words. Free text because it is
    // theirs to name and nothing branches on it.
    public string? LocationName { get; set; }

    // "lat,lng" as the device reported it, if it reported anything. Captured at the
    // moment of punching rather than looked up later, which would record where the
    // person is now instead of where they were then.
    public string? ClockInCoordinates { get; set; }

    public string? ClockOutCoordinates { get; set; }

    public PunchSource Source { get; set; }

    public string? Note { get; set; }

    // Who typed it, when it was typed rather than punched. Null for a real punch.
    //
    // A user rather than an employee: the person correcting a record is acting as an
    // administrator, and the owner — who is no employee — may legitimately be the one
    // doing it at a company that has not appointed an HR admin yet.
    public Guid? RecordedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One break. Open — no end — while it is being taken.
public sealed record BreakInterval
{
    public required DateTimeOffset Start { get; init; }

    public DateTimeOffset? End { get; init; }
}

// Where a day has got to, read from its punches rather than stored beside them.
//
// Stored state is a second answer to a question the instants already settle, and the
// two disagree the first time a correction lands.
public enum ClockState
{
    NotStarted,
    ClockedIn,
    OnBreak,
    ClockedOut,
}

public static class AttendanceRecords
{
    public static ClockState StateOf(AttendanceRecord record)
    {
        if (record.ClockInAt is null)
        {
            return ClockState.NotStarted;
        }

        if (record.ClockOutAt is not null)
        {
            return ClockState.ClockedOut;
        }

        return record.Breaks.Any(interval => interval.End is null)
            ? ClockState.OnBreak
            : ClockState.ClockedIn;
    }

    // Break minutes taken so far. An open break counts up to `asAt` — or to clock-out
    // if the day is closed, so a day that ended mid-break does not accrue break time
    // for ever.
    public static int BreakMinutes(AttendanceRecord record, DateTimeOffset asAt)
    {
        DateTimeOffset ceiling = record.ClockOutAt ?? asAt;

        double minutes = record.Breaks.Sum(interval =>
            Math.Max(0, ((interval.End ?? ceiling) - interval.Start).TotalMinutes));

        return (int)Math.Round(minutes);
    }

    // Hours worked so far: clock-in to clock-out — or to now, while the day is still
    // running — less every break taken.
    public static decimal WorkedHours(AttendanceRecord record, DateTimeOffset asAt)
    {
        if (record.ClockInAt is not DateTimeOffset start)
        {
            return 0m;
        }

        return AttendanceDay.WorkedHours(
            start,
            record.ClockOutAt ?? asAt,
            BreakMinutes(record, asAt));
    }
}
