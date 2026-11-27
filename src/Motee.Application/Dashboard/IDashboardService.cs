using Motee.Domain.Attendance;

namespace Motee.Application.Dashboard;

// The HR dashboard.
//
// Figures, not records. The screen it serves currently computes everything in
// the browser from a bundle holding every employee — which means shipping the
// whole roster, dates of birth and gender included, to anybody who can open the
// dashboard, and recomputing turnover on every render. The counting belongs
// here, where it is done once and only the answers leave.
//
// Two reads rather than one, matching the two tabs this can honestly fill. The
// dashboard has seven; the other five need sickness, recruitment, surveys,
// occupational health and training, none of which exist yet. Returning empty
// sections for them would look like a system with no data rather than a system
// without the feature.
public interface IDashboardService
{
    Task<PeopleDashboardDto> PeopleAsync(CancellationToken cancellationToken = default);

    Task<AttendanceDashboardDto> AttendanceAsync(CancellationToken cancellationToken = default);

    // The lifecycle overview: how many sit at each stage from workforce planning
    // to offboarding.
    //
    // Spans five modules, which is why it is one read rather than five. The
    // screen's eight stage cards were each reading a browser-side store, so the
    // page showed whatever that session happened to have loaded — and two stages
    // had no source at all.
    Task<LifecycleDashboardDto> LifecycleAsync(CancellationToken cancellationToken = default);

    // There is deliberately no EventsAsync here.
    //
    // The dashboard's Events tab wants birthdays, anniversaries, closures,
    // starters, leavers and whatever is on the company calendar, in date order.
    // GET /calendar/upcoming?days=30 already returns exactly that: the agenda
    // composes holidays, calendar events, leave, sickness, celebrations,
    // interviews and reviews, sorted by date.
    //
    // A dashboard copy would be a second implementation of the same derivations
    // and a worse one — the agenda applies each event's own audience rules, so a
    // one-to-one somebody is not party to stays out of it. A feed assembled here
    // would have to repeat that or leak. Starters and leavers were the only
    // genuine gap and were added to the agenda's celebrations instead, which
    // also puts them on the calendar screen where "Ada starts Monday" belongs.

    // Who is carrying leave they are about to lose.
    //
    // "About to lose" rather than "has a lot left": the threshold is the
    // carry-over cap from the policy, so the list answers the question the card
    // is actually for. A flat number of days would flag somebody with 12 days
    // and unlimited carry-over, who loses nothing.
    Task<UntakenLeaveDashboardDto> UntakenLeaveAsync(
        CancellationToken cancellationToken = default);

    // The employee's own front page: where their clock is, whether they are off
    // today, what is waiting on them, and who on their team is away.
    //
    // One call for a page that would otherwise make five, and composed by
    // delegating — the clock comes back from IAttendanceService and the task
    // counts from ITaskService, not from a second reading of the same tables.
    // That is the difference between this and an events feed assembled here:
    // delegation cannot drift from what the other screens show, a
    // reimplementation can.
    Task<MyDashboardDto> MineAsync(CancellationToken cancellationToken = default);
}

// ── self-service ────────────────────────────────────────────────────────────

public sealed record MyDashboardDto
{
    public required DateOnly AsOf { get; init; }

    public required MyDayDto Today { get; init; }

    // Things still waiting on this person, newest obligation first. Distinct
    // from their notifications, which say what happened: a notification about a
    // course was sent once, and the course stays outstanding until it is done.
    public required IReadOnlyList<MyPendingItemDto> Pending { get; init; }

    public required IReadOnlyList<TeamLeaveDto> TeamOnLeave { get; init; }
}

public sealed record MyDayDto
{
    // From the attendance service, so the dashboard's button and the attendance
    // screen's button cannot disagree about whether somebody is clocked in.
    public required ClockState Clock { get; init; }

    public DateTimeOffset? ClockedInAt { get; init; }

    public required decimal WorkedHours { get; init; }

    // The leave type's name when they are off today, null when they are not.
    // Named rather than a boolean: "on leave" and "on leave — compassionate"
    // are different things to show a colleague opening the page.
    public string? OnLeave { get; init; }

    public required int TasksOutstanding { get; init; }

    public required int TasksOverdue { get; init; }

    // Pending.Count, carried here so the tile can show a number without the
    // client having to hold the list it does not otherwise display.
    public required int PendingActions { get; init; }
}

public sealed record MyPendingItemDto
{
    // leave-request, course, acknowledgement, approval. The screen keys its
    // icon off this; the wording below is for reading, not for switching on.
    public required string Kind { get; init; }

    public required string Label { get; init; }

    public string? Detail { get; init; }

    public string? Href { get; init; }

    // Past its date, or with nothing between it and a deadline. Drives the
    // card's emphasis rather than a severity the server has invented.
    public required bool Urgent { get; init; }
}

public sealed record TeamLeaveDto
{
    public required Guid EmployeeId { get; init; }

    public required string Name { get; init; }

    public required string LeaveType { get; init; }

    // First working day back, so the card reads "back Thursday" rather than
    // making somebody add a day to the end date themselves.
    public DateOnly? ReturnsOn { get; init; }
}

// ── untaken leave ───────────────────────────────────────────────────────────

public sealed record UntakenLeaveDashboardDto
{
    public required DateOnly AsOf { get; init; }

    // When the balances below stop being carried. What makes the list urgent.
    public DateOnly? LeaveYearEnds { get; init; }

    public required IReadOnlyList<UntakenLeaveRowDto> AtRisk { get; init; }
}

public sealed record UntakenLeaveRowDto
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? Department { get; init; }

    // Days still available to book.
    public required decimal Remaining { get; init; }

    // Of those, how many cannot be carried and would simply go. The figure the
    // card is really about: 20 remaining with 5 carry-over allowed is 15 lost.
    public required decimal AtRiskDays { get; init; }
}

// ── lifecycle ───────────────────────────────────────────────────────────────

public sealed record LifecycleDashboardDto
{
    public required DateOnly AsOf { get; init; }

    public required IReadOnlyList<LifecycleStageDto> Stages { get; init; }
}

public sealed record LifecycleStageDto
{
    // The screen's own stage id — "workforce-planning", "attract", "offboard".
    // Matching their keys rather than inventing ours means the card lookup is a
    // dictionary hit and nobody maintains a translation table.
    public required string Key { get; init; }

    public required int Count { get; init; }
}

// No label. The screen already words each card — "3 new starters onboarding",
// "2 leaving" — with its own pluralisation, and sending a second copy from here
// would mean two places to change the wording and one of them losing.
//
// No names either, though the request asked for "the newest few people" per
// stage. The cards display an integer, and the names behind these counts are
// candidates, joiners and leavers across five modules: a lot of personal data
// shipped to render a number. Worth revisiting if a card ever grows a list.

// ── people ──────────────────────────────────────────────────────────────────

public sealed record PeopleDashboardDto
{
    // The day everything here was counted against, in the tenant's own zone. On
    // the response because "new hires this month" is meaningless without it, and
    // a client in another timezone would otherwise label it wrongly.
    public required DateOnly AsOf { get; init; }

    public required HeadcountDto Headcount { get; init; }

    public required IReadOnlyList<BirthdayDto> UpcomingBirthdays { get; init; }

    public required IReadOnlyList<CategoryCountDto> ByDepartment { get; init; }

    public required IReadOnlyList<CategoryCountDto> ByEmploymentType { get; init; }

    public required IReadOnlyList<CategoryCountDto> ByGender { get; init; }

    public required IReadOnlyList<HeadcountPointDto> HeadcountTrend { get; init; }

    public required TurnoverDto Turnover { get; init; }
}

public sealed record HeadcountDto
{
    public required int Total { get; init; }

    public required int NewHiresThisMonth { get; init; }

    public required int LeaversThisMonth { get; init; }

    // Whose work mode is remote. Not "remote today" in the sense of a rota —
    // there is no rota — so the label on the card should say what this counts.
    public required int Remote { get; init; }
}

public sealed record BirthdayDto
{
    public required Guid EmployeeId { get; init; }

    public required string Name { get; init; }

    // This year's occurrence, not the year they were born. The card shows a date
    // and a countdown, and neither wants 1991.
    public required DateOnly Date { get; init; }

    public required int DaysUntil { get; init; }
}

public sealed record CategoryCountDto
{
    public required string Label { get; init; }

    public required int Count { get; init; }
}

public sealed record HeadcountPointDto
{
    // First of the month.
    public required DateOnly Month { get; init; }

    // How many were in post at the end of it. Reconstructed from start and
    // leaving dates rather than read from a snapshot table: a snapshot has to be
    // written by something, and a month nobody wrote is a hole in the chart.
    public required int Headcount { get; init; }

    public required int Joiners { get; init; }

    public required int Leavers { get; init; }

    public int Net => Joiners - Leavers;
}

public sealed record TurnoverDto
{
    // Leavers in the latest quarter over the average headcount across it, as a
    // percentage to one decimal place.
    public required decimal Rate { get; init; }

    // Against the quarter before. Positive means turnover rose, which the card
    // colours as bad — the only KPI here where up is the wrong direction.
    public required decimal Delta { get; init; }

    // Resignation and retirement. People who chose to go.
    public required int Voluntary { get; init; }

    // Termination, redundancy and contract end.
    public required int Involuntary { get; init; }

    // Exits with no offboarding record, or one giving no usable reason. Counted
    // rather than quietly folded into involuntary: a dashboard that invents a
    // reason for somebody's departure is worse than one admitting it does not
    // know.
    public required int Unclassified { get; init; }
}

// ── attendance ──────────────────────────────────────────────────────────────

public sealed record AttendanceDashboardDto
{
    public required DateOnly AsOf { get; init; }

    public required AttendanceTodayDto Today { get; init; }

    // Named, not just counted. The point of the panel is that somebody can ring
    // the people on it.
    public required IReadOnlyList<AttendancePersonDto> Absent { get; init; }

    public required IReadOnlyList<AttendancePersonDto> Late { get; init; }

    public required IReadOnlyList<AttendancePersonDto> OnLeave { get; init; }

    // Thirty days, oldest first.
    public required IReadOnlyList<AttendancePointDto> Daily { get; init; }

    // Three weeks, averaged per working day rather than totalled, so a short
    // week does not read as a collapse in attendance.
    public required IReadOnlyList<WeeklyAttendancePointDto> Weekly { get; init; }

    public required LeaveBreakdownDto Leave { get; init; }
}

public sealed record AttendanceTodayDto
{
    public required int Present { get; init; }

    public required int Late { get; init; }

    public required int Absent { get; init; }

    public required int OnLeave { get; init; }

    // A working day in progress with no clock-in yet. Deliberately separate from
    // absent: somebody who starts at ten is not absent at nine, and a dashboard
    // that says otherwise generates accusations before lunch.
    public required int NotClockedIn { get; init; }

    public required int Total { get; init; }
}

public sealed record AttendancePersonDto
{
    public required Guid EmployeeId { get; init; }

    public required string Name { get; init; }

    public string? Department { get; init; }

    public DateTimeOffset? ClockInAt { get; init; }
}

public sealed record AttendancePointDto
{
    public required DateOnly Date { get; init; }

    public required int Present { get; init; }

    public required int Late { get; init; }

    public required int Absent { get; init; }
}

public sealed record WeeklyAttendancePointDto
{
    public required DateOnly WeekStart { get; init; }

    public required int Present { get; init; }

    public required int Late { get; init; }

    public required int Absent { get; init; }
}

// Leave over a rolling twelve months, split the way the tiles split it.
public sealed record LeaveBreakdownDto
{
    public required LeaveKindDto Annual { get; init; }

    public required LeaveKindDto Sick { get; init; }

    // Everything else a company offers — study leave, compassionate, unpaid.
    // Tenants define their own types, so this cannot be an enumerated list.
    public required LeaveKindDto Other { get; init; }
}

public sealed record LeaveKindDto
{
    // Bookings. A request and a day are not the same thing — one booking can be
    // ten days — so both are given rather than leaving the reader to guess which
    // the tile means.
    public required int Requests { get; init; }

    public required decimal Days { get; init; }

    public required int Pending { get; init; }
}
