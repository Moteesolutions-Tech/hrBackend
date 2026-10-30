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
}

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
