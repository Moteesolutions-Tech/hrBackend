using Microsoft.EntityFrameworkCore;
using Motee.Application.Attendance;
using Motee.Application.Dashboard;
using Motee.Application.Tenancy;
using Motee.Domain.Attendance;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Domain.Offboarding;
using Motee.Domain.Organisation;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Dashboard;

// The HR dashboard's figures.
//
// Counting, not storing. Nothing here has a table: every number is derived from
// employees, attendance and leave at the moment it is asked for. That is the
// right trade for a screen somebody opens a few times a day — a snapshot table
// would need something to write it, and a month nobody wrote would be a hole in
// the chart rather than a recomputation.
//
// Attendance statuses come back through IAttendanceService rather than being
// read off the rows, because the status is derived. Counting "late" here would
// mean a second definition of late, and the two would part company the first
// time a grace period changed.
internal sealed class DashboardService(
    MoteeDbContext dbContext,
    IAttendanceService attendance,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IDashboardService
{
    // How far back the headcount chart and the leave tiles look.
    private const int TrendMonths = 12;

    // Birthdays inside this many days are "upcoming". A week is long enough to
    // organise a card and short enough that the list stays a list.
    private const int BirthdayWindowDays = 7;

    private const int DailySeriesDays = 30;

    private const int WeeklySeriesWeeks = 3;

    // What the dashboard treats as in post. Onboarded and Pending are people who
    // have not started, and counting them as headcount would inflate every
    // figure on the screen from the day an offer is accepted.
    private static readonly EmployeeStatus[] InPost =
    [
        EmployeeStatus.Active,
        EmployeeStatus.Probation,
        EmployeeStatus.OnLeave,
    ];

    // Everything the people figures need, which is a long way short of an
    // employee row — no addresses, no bank details, no next of kin. A dashboard
    // that loads whole employees is a dashboard that leaks them.
    private sealed record Head(
        Guid Id,
        string Name,
        EmployeeStatus Status,
        Guid? DepartmentId,
        EmploymentType? EmploymentType,
        WorkMode? WorkMode,
        string? Gender,
        DateOnly? StartDate,
        DateOnly? DateOfLeaving,
        DateOnly? DateOfBirth);

    public async Task<PeopleDashboardDto> PeopleAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);

        List<Head> everybody = await HeadsAsync(cancellationToken);

        List<Head> inPost = [.. everybody.Where(head => InPost.Contains(head.Status))];

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        return new PeopleDashboardDto
        {
            AsOf = today,
            Headcount = new HeadcountDto
            {
                Total = inPost.Count,

                // Counted off the start date rather than the record's creation
                // date: somebody entered in March to start in June is a June
                // hire, and the card next to it says "this month".
                NewHiresThisMonth = inPost.Count(head => InMonth(head.StartDate, today)),

                // Over everybody, not just those in post — the whole point of a
                // leaver is that they are not.
                LeaversThisMonth = everybody.Count(head => InMonth(head.DateOfLeaving, today)),

                Remote = inPost.Count(head => head.WorkMode == Domain.Employees.WorkMode.Remote),
            },
            UpcomingBirthdays = Birthdays(inPost, today),
            ByDepartment = Tally(
                inPost,
                head => head.DepartmentId is Guid id ? departments.GetValueOrDefault(id) : null),
            ByEmploymentType = Tally(inPost, head => head.EmploymentType?.ToString()),

            // Unrecorded is reported, not folded into "other". A gap in the data
            // is not a statement about anybody.
            ByGender = Tally(inPost, head => head.Gender, unknown: "Not recorded"),

            HeadcountTrend = Trend(everybody, today),
            Turnover = await TurnoverAsync(everybody, today, cancellationToken),
        };
    }

    public async Task<AttendanceDashboardDto> AttendanceAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);
        DateOnly from = today.AddDays(-(DailySeriesDays - 1));

        IReadOnlyList<AttendanceDayDto> day = await attendance.CompanyDayAsync(
            today, cancellationToken);

        IReadOnlyList<AttendanceDayDto> range = await attendance.CompanyBetweenAsync(
            from, today, cancellationToken);

        return new AttendanceDashboardDto
        {
            AsOf = today,
            Today = new AttendanceTodayDto
            {
                Present = Count(day, AttendanceStatus.Present),

                // Early departures sit with present. Somebody who left at four
                // did turn up, and a dashboard that drops them from the count
                // makes the day look emptier than it was.
                Late = Count(day, AttendanceStatus.Late),
                Absent = Count(day, AttendanceStatus.Absent),
                OnLeave = Count(day, AttendanceStatus.OnLeave),
                NotClockedIn = Count(day, AttendanceStatus.NotClockedIn),
                Total = day.Count,
            },
            Absent = People(day, AttendanceStatus.Absent),
            Late = People(day, AttendanceStatus.Late),
            OnLeave = People(day, AttendanceStatus.OnLeave),
            Daily = Daily(range, from, today),
            Weekly = Weekly(range, today),
            Leave = await LeaveAsync(today, cancellationToken),
        };
    }

    // ── people ──────────────────────────────────────────────────────────────

    private async Task<List<Head>> HeadsAsync(CancellationToken cancellationToken) =>
        await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Status != EmployeeStatus.Deleted)
            .Select(employee => new Head(
                employee.Id,
                employee.FirstName + " " + employee.LastName,
                employee.Status,
                employee.DepartmentId,
                employee.EmploymentType,
                employee.WorkMode,
                employee.Gender,
                employee.StartDate,
                employee.DateOfLeaving,
                employee.DateOfBirth))
            .ToListAsync(cancellationToken);

    private static bool InMonth(DateOnly? date, DateOnly today) =>
        date is DateOnly value && value.Year == today.Year && value.Month == today.Month;

    // Birthdays as a countdown rather than a date comparison.
    //
    // Comparing month and day as numbers is what breaks every December: on the
    // 28th, "within seven days" reaches into January, and 1 < 12 says otherwise.
    // Projecting each birthday onto the next time it actually occurs sidesteps
    // the year boundary instead of special-casing it.
    private static IReadOnlyList<BirthdayDto> Birthdays(
        IReadOnlyList<Head> inPost,
        DateOnly today)
    {
        List<BirthdayDto> upcoming = [];

        foreach (Head head in inPost)
        {
            if (head.DateOfBirth is not DateOnly born)
            {
                continue;
            }

            DateOnly next = AnnualDates.Next(born, today);
            int days = next.DayNumber - today.DayNumber;

            if (days <= BirthdayWindowDays)
            {
                upcoming.Add(new BirthdayDto
                {
                    EmployeeId = head.Id,
                    Name = head.Name,
                    Date = next,
                    DaysUntil = days,
                });
            }
        }

        return [.. upcoming.OrderBy(birthday => birthday.DaysUntil)];
    }

    // Counts by category, biggest first, with empty categories left out.
    //
    // A department reading zero looks like a fault in the chart rather than a
    // department nobody is in, so it is absent instead.
    private static IReadOnlyList<CategoryCountDto> Tally(
        IReadOnlyList<Head> heads,
        Func<Head, string?> categorise,
        string? unknown = null) =>
        [
            .. heads
                .Select(head => categorise(head) is string label && !string.IsNullOrWhiteSpace(label)
                    ? label
                    : unknown)
                .OfType<string>()
                .GroupBy(label => label, StringComparer.Ordinal)
                .Select(group => new CategoryCountDto { Label = group.Key, Count = group.Count() })
                .OrderByDescending(row => row.Count)
                .ThenBy(row => row.Label, StringComparer.Ordinal),
        ];

    // Twelve months of headcount, rebuilt from when people started and left.
    //
    // No snapshot table. A snapshot has to be written by something, and the
    // month that something did not run becomes a hole in the chart that nobody
    // can fill afterwards. Start and leaving dates are already correct, already
    // maintained, and can be recounted for any month at any time.
    private static IReadOnlyList<HeadcountPointDto> Trend(
        IReadOnlyList<Head> everybody,
        DateOnly today)
    {
        DateOnly first = new DateOnly(today.Year, today.Month, 1).AddMonths(-(TrendMonths - 1));

        List<HeadcountPointDto> points = [];

        for (int offset = 0; offset < TrendMonths; offset++)
        {
            DateOnly month = first.AddMonths(offset);
            DateOnly end = month.AddMonths(1).AddDays(-1);

            points.Add(new HeadcountPointDto
            {
                Month = month,

                // In post at the end of the month: started on or before it, and
                // either still here or gone after it.
                Headcount = everybody.Count(head =>
                    head.StartDate is DateOnly start
                        && start <= end
                        && (head.DateOfLeaving is not DateOnly left || left > end)),

                Joiners = everybody.Count(head => Within(head.StartDate, month, end)),
                Leavers = everybody.Count(head => Within(head.DateOfLeaving, month, end)),
            });
        }

        return points;
    }

    private static bool Within(DateOnly? date, DateOnly from, DateOnly to) =>
        date is DateOnly value && value >= from && value <= to;

    // Turnover over the last rolling quarter, against the one before it.
    //
    // Rolling rather than calendar quarters, so the figure means something on
    // the second of April. A calendar quarter one day old is one day of leavers
    // over three months of headcount, which reads as a company nobody leaves.
    private async Task<TurnoverDto> TurnoverAsync(
        IReadOnlyList<Head> everybody,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        DateOnly currentFrom = today.AddMonths(-3);
        DateOnly priorFrom = today.AddMonths(-6);

        List<Head> leavers =
        [
            .. everybody.Where(head => Within(head.DateOfLeaving, currentFrom, today)),
        ];

        Dictionary<Guid, ExitReason> reasons = await dbContext.OffboardingRecords
            .AsNoTracking()
            .Where(record => leavers.Select(head => head.Id).Contains(record.EmployeeId))
            .GroupBy(record => record.EmployeeId)

            // The most recent exit, for somebody who left, came back and left
            // again. An older record is not what the latest departure was for.
            .Select(group => group
                .OrderByDescending(record => record.InitiatedAt)
                .First())
            .ToDictionaryAsync(
                record => record.EmployeeId, record => record.ExitReason, cancellationToken);

        int voluntary = 0;
        int involuntary = 0;
        int unclassified = 0;

        foreach (Head leaver in leavers)
        {
            if (!reasons.TryGetValue(leaver.Id, out ExitReason reason))
            {
                // An exit nobody recorded a reason for. Said so rather than
                // bundled into involuntary, which would be the system inventing
                // a reason for somebody's departure.
                unclassified++;
                continue;
            }

            switch (reason)
            {
                case ExitReason.Resignation or ExitReason.Retirement:
                    voluntary++;
                    break;

                case ExitReason.Termination or ExitReason.Redundancy or ExitReason.ContractEnd:
                    involuntary++;
                    break;

                default:
                    unclassified++;
                    break;
            }
        }

        decimal current = RateFor(everybody, currentFrom, today);
        decimal prior = RateFor(everybody, priorFrom, currentFrom);

        return new TurnoverDto
        {
            Rate = current,
            Delta = Math.Round(current - prior, 1),
            Voluntary = voluntary,
            Involuntary = involuntary,
            Unclassified = unclassified,
        };
    }

    // Leavers over the average of the headcount at each end of the period, which
    // is the standard way of putting it: dividing by the closing headcount alone
    // flatters a shrinking company and penalises a growing one.
    private static decimal RateFor(IReadOnlyList<Head> everybody, DateOnly from, DateOnly to)
    {
        int opening = InPostOn(everybody, from);
        int closing = InPostOn(everybody, to);

        decimal average = (opening + closing) / 2m;

        if (average == 0)
        {
            return 0m;
        }

        int leavers = everybody.Count(head => Within(head.DateOfLeaving, from, to));

        return Math.Round(leavers * 100m / average, 1);
    }

    private static int InPostOn(IReadOnlyList<Head> everybody, DateOnly day) =>
        everybody.Count(head =>
            head.StartDate is DateOnly start
                && start <= day
                && (head.DateOfLeaving is not DateOnly left || left > day));

    // ── attendance ──────────────────────────────────────────────────────────

    private static int Count(IReadOnlyList<AttendanceDayDto> days, AttendanceStatus status) =>
        days.Count(day => day.Status == status);

    private static IReadOnlyList<AttendancePersonDto> People(
        IReadOnlyList<AttendanceDayDto> days,
        AttendanceStatus status) =>
        [
            .. days
                .Where(day => day.Status == status)
                .Select(day => new AttendancePersonDto
                {
                    EmployeeId = day.EmployeeId,
                    Name = day.EmployeeName,
                    Department = day.DepartmentName,
                    ClockInAt = day.ClockInAt,
                })
                .OrderBy(person => person.Name, StringComparer.Ordinal),
        ];

    // Every day in the window, including the ones nothing happened on.
    //
    // Built from the dates rather than from what came back, so a quiet week is
    // a flat line rather than a gap the chart closes up — which would make the
    // days either side look adjacent.
    private static IReadOnlyList<AttendancePointDto> Daily(
        IReadOnlyList<AttendanceDayDto> range,
        DateOnly from,
        DateOnly to)
    {
        Dictionary<DateOnly, List<AttendanceDayDto>> byDate = range
            .GroupBy(day => day.Date)
            .ToDictionary(group => group.Key, group => group.ToList());

        List<AttendancePointDto> points = [];

        for (DateOnly date = from; date <= to; date = date.AddDays(1))
        {
            List<AttendanceDayDto> onDay = byDate.GetValueOrDefault(date) ?? [];

            points.Add(new AttendancePointDto
            {
                Date = date,
                Present = Count(onDay, AttendanceStatus.Present),
                Late = Count(onDay, AttendanceStatus.Late),
                Absent = Count(onDay, AttendanceStatus.Absent),
            });
        }

        return points;
    }

    // The last few weeks as an average per day with anything recorded.
    //
    // Averaged, not totalled. A week containing a bank holiday has four days in
    // it, and a total would draw that as attendance collapsing by a fifth.
    // Divided by the days that have records rather than by seven, for the same
    // reason — weekends would otherwise drag every week down.
    private static IReadOnlyList<WeeklyAttendancePointDto> Weekly(
        IReadOnlyList<AttendanceDayDto> range,
        DateOnly today)
    {
        DateOnly thisWeek = TenantClock.WeekStart(today);

        List<WeeklyAttendancePointDto> points = [];

        for (int back = WeeklySeriesWeeks - 1; back >= 0; back--)
        {
            DateOnly start = thisWeek.AddDays(-7 * back);
            DateOnly end = start.AddDays(6);

            List<AttendanceDayDto> inWeek =
            [
                .. range.Where(day => day.Date >= start && day.Date <= end),
            ];

            int days = inWeek.Select(day => day.Date).Distinct().Count();

            points.Add(new WeeklyAttendancePointDto
            {
                WeekStart = start,
                Present = Average(inWeek, AttendanceStatus.Present, days),
                Late = Average(inWeek, AttendanceStatus.Late, days),
                Absent = Average(inWeek, AttendanceStatus.Absent, days),
            });
        }

        return points;
    }

    private static int Average(
        IReadOnlyList<AttendanceDayDto> days, AttendanceStatus status, int over) =>
        over == 0 ? 0 : (int)Math.Round((double)Count(days, status) / over);

    // ── leave ───────────────────────────────────────────────────────────────

    // Twelve rolling months, split the way the tiles split it.
    //
    // By the type's code, not its name. The code exists so a renamed type is
    // still recognisable, and a company calling annual leave "Holiday" must not
    // find the annual tile reading zero.
    private async Task<LeaveBreakdownDto> LeaveAsync(
        DateOnly today,
        CancellationToken cancellationToken)
    {
        DateOnly from = today.AddMonths(-TrendMonths);

        Dictionary<Guid, string?> codes = await dbContext.LeaveTypes
            .AsNoTracking()
            .ToDictionaryAsync(type => type.Id, type => type.Code, cancellationToken);

        List<LeaveRequest> requests = await dbContext.LeaveRequests
            .AsNoTracking()
            .Where(request => request.StartDate >= from
                && request.StartDate <= today
                && (request.Status == LeaveRequestStatus.Approved
                    || request.Status == LeaveRequestStatus.Pending))
            .ToListAsync(cancellationToken);

        return new LeaveBreakdownDto
        {
            Annual = Kind(requests, codes, "annual"),
            Sick = Kind(requests, codes, "sick"),
            Other = Kind(requests, codes, null),
        };
    }

    // A null code means everything that is neither annual nor sick — the types a
    // company invented, which cannot be enumerated here because the company can
    // add one tomorrow.
    private static LeaveKindDto Kind(
        IReadOnlyList<LeaveRequest> requests,
        IReadOnlyDictionary<Guid, string?> codes,
        string? code)
    {
        List<LeaveRequest> matching =
        [
            .. requests.Where(request =>
            {
                string? actual = codes.GetValueOrDefault(request.LeaveTypeId);

                return code is null
                    ? actual is not "annual" and not "sick"
                    : string.Equals(actual, code, StringComparison.Ordinal);
            }),
        ];

        return new LeaveKindDto
        {
            Requests = matching.Count,
            Days = matching.Sum(request => request.TotalDays),
            Pending = matching.Count(
                request => request.Status == LeaveRequestStatus.Pending),
        };
    }

    // ── the company's calendar ──────────────────────────────────────────────

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        TenantClock.DayOf(timeProvider.GetUtcNow(), await ZoneAsync(cancellationToken));

    // Scoped by id, because tenants are not themselves tenant-scoped, and the
    // whole Settings object rather than a field of it, because Settings is jsonb
    // behind a value converter and selecting into it does not reach the column.
    // Both of those have been bugs here before.
    private async Task<string?> ZoneAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        return settings?.TimeZoneId;
    }
}
