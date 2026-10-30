using Motee.Application.Common;
using Motee.Domain.Attendance;

namespace Motee.Application.Attendance;

// Punches, and the day they add up to.
//
// Split in two by who is acting rather than by resource. The clock methods are the
// employee's own — they take no employee id, because the only day anybody can punch
// is their own, and an id in the signature is an id somebody eventually passes
// somebody else's. The rest is administrative and names whose day it is reading.
public interface IAttendanceService
{
    // ── the employee's own day ──────────────────────────────────────────────

    Task<AttendanceDayDto?> MineAsync(DateOnly? date = null, CancellationToken cancellationToken = default);

    // A stretch of the caller's own days — the month calendar, the week strip.
    //
    // Every day in the range, not only the ones with punches. A calendar with gaps
    // where somebody was absent is a calendar that hides absences, and the screen
    // cannot tell "no row" from "no data" without asking again per square.
    Task<IReadOnlyList<AttendanceDayDto>> MineBetweenAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    // The same stretch for somebody else, for whoever is reviewing it. Shared with
    // timesheets rather than reimplemented there, so a week and the calendar it came
    // from cannot derive a day differently.
    Task<IReadOnlyList<AttendanceDayDto>> DaysForAsync(
        Guid employeeId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    // The figures above the calendar: hours, overtime, and how the days fell out.
    // Computed from the same derivation as the rows, so the total and the list it
    // sits above cannot disagree.
    Task<AttendanceSummaryDto> MineSummaryAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    // The same figures for the company, or a slice of it.
    Task<AttendanceSummaryDto> SummaryAsync(
        AttendanceSummaryQuery query,
        CancellationToken cancellationToken = default);

    Task<AttendanceResult> ClockInAsync(
        ClockInRequest request,
        CancellationToken cancellationToken = default);

    Task<AttendanceResult> StartBreakAsync(CancellationToken cancellationToken = default);

    Task<AttendanceResult> EndBreakAsync(CancellationToken cancellationToken = default);

    Task<AttendanceResult> ClockOutAsync(
        ClockOutRequest request,
        CancellationToken cancellationToken = default);

    // Moving between office and home mid-day. A change to the day in progress, not a
    // new punch — somebody who went home after lunch did not arrive twice.
    Task<AttendanceResult> SetLocationAsync(
        SetWorkLocationRequest request,
        CancellationToken cancellationToken = default);

    // ── the company's day ───────────────────────────────────────────────────

    Task<PagedResult<AttendanceDayDto>> ListAsync(
        AttendanceQuery query,
        CancellationToken cancellationToken = default);

    // HR typing a day in, for somebody who forgot or whose reader was down. Creates
    // the day or replaces it, and is always recorded as Manual with the author
    // attached — a typed day and a punched one must never be indistinguishable.
    Task<AttendanceResult> LogAsync(
        LogAttendanceRequest request,
        CancellationToken cancellationToken = default);

    // ── reads for aggregates ────────────────────────────────────────────────
    //
    // Both live here rather than in whatever is counting, because status is
    // derived. A caller that queried the records itself would have to decide
    // again what counts as late, and two definitions of late is one too many.

    // Today for everybody in post, including those with nothing recorded. The
    // gaps are the point: a day with no punch is what "absent" and "not clocked
    // in" are made of, and they do not exist as rows.
    Task<IReadOnlyList<AttendanceDayDto>> CompanyDayAsync(
        DateOnly? date = null,
        CancellationToken cancellationToken = default);

    // Recorded days across a range, company-wide. No gap filling, so the cost
    // is proportional to what actually happened rather than to people times
    // days — a trend line is drawn from the days that exist.
    Task<IReadOnlyList<AttendanceDayDto>> CompanyBetweenAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);
}

public sealed record BreakIntervalDto
{
    public required DateTimeOffset Start { get; init; }

    // Null while the break is still being taken.
    public DateTimeOffset? End { get; init; }
}

public sealed record AttendanceDayDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? JobTitle { get; init; }

    public string? DepartmentName { get; init; }

    public required DateOnly Date { get; init; }

    public DateTimeOffset? ClockInAt { get; init; }

    public DateTimeOffset? ClockOutAt { get; init; }

    public required IReadOnlyList<BreakIntervalDto> Breaks { get; init; }

    public required int BreakMinutes { get; init; }

    // Where the clock has got to, read from the punches rather than stored. A client
    // renders its button from this instead of working the state out again and
    // reaching a different answer.
    public required ClockState State { get; init; }

    public required AttendanceStatus Status { get; init; }

    // Worked so far, counting up while the day is open. Recomputed on every read,
    // which is why a tab left open overnight does not report a twenty-hour shift the
    // moment somebody looks at it.
    public required decimal WorkedHours { get; init; }

    // What the schedule said they owed today. Zero on a day they were not due to work.
    public required decimal ScheduledHours { get; init; }

    public required decimal OvertimeHours { get; init; }

    // Short of the statutory break, in minutes. Zero when none is owed or it was
    // taken.
    public required int BreakShortfallMinutes { get; init; }

    public required WorkLocation Location { get; init; }

    public string? LocationName { get; init; }

    public string? ClockInCoordinates { get; init; }

    public string? ClockOutCoordinates { get; init; }

    public required PunchSource Source { get; init; }

    public string? Note { get; init; }

    // True when HR typed this rather than the person punching it. Surfaced rather than
    // left to be inferred from Source, because it is the first question asked about
    // any disputed day.
    public required bool RecordedByHand { get; init; }

    // The schedule this day was judged against, by name — "Standard", "Night Shift".
    // Without it, "late" is a verdict with no visible basis.
    public string? ScheduleName { get; init; }
}

public sealed record ClockInRequest
{
    public WorkLocation Location { get; init; } = WorkLocation.Office;

    public string? LocationName { get; init; }

    // "lat,lng" from the device, when it offered one. Optional on purpose: a browser
    // that refuses location must not stop somebody starting work.
    public string? Coordinates { get; init; }

    public PunchSource Source { get; init; } = PunchSource.Web;
}

public sealed record ClockOutRequest
{
    public string? Coordinates { get; init; }

    public string? Note { get; init; }
}

public sealed record SetWorkLocationRequest
{
    public required WorkLocation Location { get; init; }

    public string? LocationName { get; init; }
}

public sealed record LogAttendanceRequest
{
    public required Guid EmployeeId { get; init; }

    public required DateOnly Date { get; init; }

    public DateTimeOffset? ClockInAt { get; init; }

    public DateTimeOffset? ClockOutAt { get; init; }

    public int BreakMinutes { get; init; }

    public WorkLocation Location { get; init; } = WorkLocation.Office;

    public string? LocationName { get; init; }

    public string? Note { get; init; }
}

public sealed record AttendanceQuery : PagedQuery
{
    // One day at a time. Defaults to today in the company's own timezone, which is
    // what the overview screen opens on.
    public DateOnly? Date { get; init; }

    public Guid? EmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }

    public AttendanceStatus? Status { get; init; }
}

public sealed record AttendanceSummaryQuery
{
    public required DateOnly From { get; init; }

    public required DateOnly To { get; init; }

    public Guid? EmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }
}

public sealed record AttendanceSummaryDto
{
    public required DateOnly From { get; init; }

    public required DateOnly To { get; init; }

    public required decimal TotalHours { get; init; }

    public required decimal OvertimeHours { get; init; }

    public required int DaysPresent { get; init; }

    public required int DaysLate { get; init; }

    public required int DaysAbsent { get; init; }

    public required int DaysOnLeave { get; init; }

    // Share of attended days arrived on time, as a whole percentage.
    //
    // 100 when nothing counts rather than zero. A new joiner with no days yet has not
    // been late; showing them at nought per cent punctual is a number somebody would
    // have to explain, and would appear on their first screen.
    public required int PunctualityRate { get; init; }
}

public enum AttendanceOutcome
{
    Succeeded,

    // The clock will not make that move from where the day is. The message says which
    // — "you are already clocked in" rather than a state name.
    NotAllowed,

    NotFound,
    EmployeeNotFound,

    // No working hours could be resolved, so nothing can be judged. Only reachable if
    // a company has somehow deleted its default schedule.
    NoSchedule,

    // Clock-out before clock-in, or a break outside the day. Rejected rather than
    // stored as a negative shift.
    OutOfOrder,

    // A day recorded in the future. Almost always a device clock that is wrong, and
    // storing it quietly leaves a timesheet nobody can reconcile.
    InTheFuture,
}

public sealed record AttendanceResult
{
    public required AttendanceOutcome Outcome { get; init; }

    public AttendanceDayDto? Day { get; init; }

    // Carried from the lifecycle so the caller passes on the specific sentence rather
    // than the category.
    public string? Reason { get; init; }

    public bool Succeeded => Outcome == AttendanceOutcome.Succeeded;

    public static AttendanceResult Failed(AttendanceOutcome outcome, string? reason = null) =>
        new() { Outcome = outcome, Reason = reason };

    public static AttendanceResult Ok(AttendanceDayDto day) =>
        new() { Outcome = AttendanceOutcome.Succeeded, Day = day };
}
