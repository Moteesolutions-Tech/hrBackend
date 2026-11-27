using Microsoft.EntityFrameworkCore;
using Motee.Application.Attendance;
using Motee.Application.Common;
using Motee.Application.Dashboard;
using Motee.Application.Leave;
using Motee.Application.Tenancy;
using Motee.Application.Work;
using Motee.Domain.Approvals;
using Motee.Domain.Attendance;
using Motee.Domain.Common;
using Motee.Domain.Documents;
using Motee.Domain.Employees;
using Motee.Domain.Health;
using Motee.Domain.Learning;
using Motee.Domain.Leave;
using Motee.Domain.Offboarding;
using Motee.Domain.Onboarding;
using Motee.Domain.Organisation;
using Motee.Domain.Performance;
using Motee.Domain.Recruitment;
using Motee.Domain.Relations;
using Motee.Domain.Work;
using Motee.Domain.Workforce;
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
    ILeaveBalanceService leaveBalances,
    ITaskService taskList,
    ICurrentEmployee currentEmployee,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IDashboardService
{
    // The seeded annual type's code. Sick leave is reported rather than booked,
    // so nobody is at risk of failing to use it.
    private const string AnnualLeaveCode = "annual";

    // A pending leave request inside this many days is urgent: starting before
    // it has been decided is the thing nobody can plan around.
    private const int LeaveDecisionWarningDays = 7;

    // A course is worth surfacing before it is late, not only after.
    private const int CourseWarningDays = 7;

    // Inside a week is a warning; past is critical. One threshold for every
    // dated action, so two rows the same number of days late do not come back
    // with different severities.
    private const int UrgentDays = 7;

    // How far ahead an expiry is worth listing at all.
    private const int SoonDays = 60;

    // An ER case open longer than this has stopped being in progress.
    private const int CaseAgeLimitDays = 30;

    // Per category. The true count travels beside the list, so a company with
    // four hundred incomplete records is told four hundred without four hundred
    // rows crossing the wire.
    private const int ActionsPerCategory = 10;

    // How far back the headcount chart and the leave tiles look.
    private const int TrendMonths = 12;

    // Birthdays inside this many days are "upcoming". A week is long enough to
    // organise a card and short enough that the list stays a list.
    private const int BirthdayWindowDays = 7;

    private const int DailySeriesDays = 30;

    private const int WeeklySeriesWeeks = 3;

    // Who the dashboard treats as in post.
    //
    // EmployeeLifecycle.InPost, not a copy. This had its own list, which left
    // Offboarding out — so the front page reported a smaller workforce than
    // learning, benefits, surveys, checklists, shifts and community, all of
    // which use the shared set. Somebody serving notice is still employed: on
    // payroll, accruing leave, holding a laptop. The duplicate was exactly the
    // drift EmployeeLifecycle.InPost's own comment warns about, and nothing
    // caught it because no test seeded a leaver mid-notice.
    private static EmployeeStatus[] InPost => EmployeeLifecycle.InPost;

    // Accepted but not yet at their desk. The complement of InPost at the front
    // end of the lifecycle, which the pre-employment stage is entirely about.
    private static readonly EmployeeStatus[] NotYetStarted =
    [
        EmployeeStatus.Pending,
        EmployeeStatus.Onboarded,
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

    // ── lifecycle ───────────────────────────────────────────────────────────

    public async Task<LifecycleDashboardDto> LifecycleAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);

        // Counted in the database, one query per stage, rather than loading five
        // modules' rows to length them here. Each is a COUNT over an indexed
        // status column; the whole page is cheaper than the single employee
        // query the people tab already runs.
        // A workforce request has no status column — WorkforceRequests.Derive
        // works it out from the approval chain, so a request cannot read
        // "pending" after its chain was approved. So the rows and their chains
        // are both read and the domain rule applied, rather than a column that
        // does not exist being counted.
        //
        // Whole rows, unlike the employee query above: a tenant has a handful
        // of open workforce requests, and Derive takes the entity.
        List<WorkforceRequest> requests = await dbContext.WorkforceRequests
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        Dictionary<Guid, ApprovalStatus> chains = await ChainStatusesAsync(
            requests.Select(request => request.ApprovalInstanceId), cancellationToken);

        // Converted is the end of this stage, not a member of it: the request
        // became a requisition and is counted there. Counting both would make
        // the funnel wider than the work going through it. Cancelled and
        // rejected have left the funnel altogether.
        int planning = requests.Count(request =>
            WorkforceRequests.Derive(request, Chain(chains, request.ApprovalInstanceId))
            is not (WorkforceRequestStatus.Converted
                or WorkforceRequestStatus.Cancelled
                or WorkforceRequestStatus.Rejected));

        // Requisitions are two stages. Before they are out they are a request
        // being approved; once recruiting they are the advert.
        List<RequisitionStatus> requisitionStatuses = await dbContext.JobRequisitions
            .AsNoTracking()
            .Select(requisition => requisition.Status)
            .ToListAsync(cancellationToken);

        int requisitions = requisitionStatuses.Count(status =>
            status is RequisitionStatus.Draft
                or RequisitionStatus.PendingApproval
                or RequisitionStatus.Approved);

        // Through the domain's own rule rather than a list repeated here, or the
        // dashboard and the recruitment board would disagree about what "open"
        // means the first time one of them changed.
        int attracting = requisitionStatuses.Count(Requisitions.IsRecruiting);

        // Still in play, and not yet hired — a hired candidate has left
        // selection and belongs to pre-employment.
        int selecting = await dbContext.Candidates
            .AsNoTracking()
            .CountAsync(
                candidate => candidate.Status == CandidateStatus.Active
                    && candidate.Stage != CandidateStage.Hired,
                cancellationToken);

        // Between "we want them" and "they have started", which is two
        // populations: a hired candidate nobody has turned into an employee
        // yet, and a joiner whose onboarding has not begun.
        int awaitingConversion = await dbContext.Candidates
            .AsNoTracking()
            .CountAsync(
                candidate => candidate.Status == CandidateStatus.Active
                    && candidate.Stage == CandidateStage.Hired
                    && candidate.CreatedEmployeeId == null,
                cancellationToken);

        // Pre-boarding, but only for somebody who has not started.
        //
        // The employee-status join is load-bearing. Every new hire gets an
        // onboarding record at PreBoarding whichever route they came in by —
        // deliberately, so nobody is invisible to the pipeline — and that
        // includes staff imported into a new tenant who have worked there for
        // years. Counting the stage alone would tell a company that migrated
        // 200 people it has 200 in pre-employment.
        int preBoarding = await dbContext.OnboardingRecords
            .AsNoTracking()
            .CountAsync(
                record => record.Stage == OnboardingStage.PreBoarding
                    && dbContext.Employees.Any(employee =>
                        employee.Id == record.EmployeeId
                        && NotYetStarted.Contains(employee.Status)),
                cancellationToken);

        // Underway but not finished. Pre-boarding has not started and is
        // counted by the stage before; completed is no longer onboarding.
        int onboarding = await dbContext.OnboardingRecords
            .AsNoTracking()
            .CountAsync(
                record => record.Stage != OnboardingStage.PreBoarding
                    && record.Stage != OnboardingStage.Completed,
                cancellationToken);

        int inPost = await dbContext.Employees
            .AsNoTracking()
            .CountAsync(employee => InPost.Contains(employee.Status), cancellationToken);

        // Through OffboardingLifecycle.IsOpen, the same rule the offboarding
        // board uses. Evaluated in memory because it is a domain method and not
        // translatable; a tenant has a handful of leavers in flight at once.
        List<OffboardingStatus> leaving = await dbContext.OffboardingRecords
            .AsNoTracking()
            .Select(record => record.Status)
            .ToListAsync(cancellationToken);

        return new LifecycleDashboardDto
        {
            AsOf = today,

            // In model order, and every stage present even at zero: the screen
            // draws eight cards regardless, and a missing one would read as a
            // broken page rather than a quiet month.
            Stages =
            [
                Stage("workforce-planning", planning),
                Stage("requisition", requisitions),
                Stage("attract", attracting),
                Stage("select", selecting),
                Stage("pre-employment", awaitingConversion + preBoarding),
                Stage("onboard", onboarding),
                Stage("develop-perform-retain", inPost),
                Stage("offboard", leaving.Count(OffboardingLifecycle.IsOpen)),
            ],
        };
    }

    private static LifecycleStageDto Stage(string key, int count) =>
        new() { Key = key, Count = count };

    // ── HR actions ──────────────────────────────────────────────────────────

    public async Task<HrActionsDashboardDto> HrActionsAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);

        Dictionary<Guid, string> who = await dbContext.Employees
            .AsNoTracking()
            .ToDictionaryAsync(
                employee => employee.Id,
                employee => employee.FirstName + " " + employee.LastName,
                cancellationToken);

        List<HrActionCategoryDto> categories =
        [
            Category("right_to_work", await ExpiringDocumentsAsync(today, cancellationToken)),
            Category("compliance", await IncompleteRecordsAsync(cancellationToken)),

            // Deliberately empty, and present rather than absent so the screen
            // draws the category and says nothing is outstanding. There is no
            // payroll in this product — no pay run, no payslip, no bank
            // details — so every row the demo bundle shows here is a row
            // nothing could ever compute. An empty category is honest; one
            // filled with invented figures is not.
            Category("payroll", []),

            Category("recruitment", await StaleVacanciesAsync(today, cancellationToken)),
            Category("relations", await OpenCasesAsync(today, who, cancellationToken)),
            Category("performance", await OverdueReviewsAsync(today, who, cancellationToken)),
            Category("leave", await LeaveAndAttendanceAsync(today, who, cancellationToken)),
            Category("learning", await LearningAsync(today, who, cancellationToken)),
            Category("assets", await AssetsWithLeaversAsync(who, cancellationToken)),
            Category("management", await StuckApprovalsAsync(cancellationToken)),
            Category("executive", await RiskAsync(today, who, cancellationToken)),
        ];

        List<HrActionDto> everything = [.. categories.SelectMany(category => category.Actions)];

        return new HrActionsDashboardDto
        {
            AsOf = today,
            Total = categories.Sum(category => category.Count),
            Critical = everything.Count(a => a.Severity == HrActionSeverity.Critical),
            Warning = everything.Count(a => a.Severity == HrActionSeverity.Warning),
            Info = everything.Count(a => a.Severity == HrActionSeverity.Info),
            Categories = categories,
        };
    }

    // Capped, with the true count beside it. A company with four hundred
    // incomplete records needs to know that; it does not need four hundred rows
    // in a dashboard payload, and the category links out to the full screen.
    private static HrActionCategoryDto Category(string key, IReadOnlyList<HrActionDto> actions) =>
        new()
        {
            Key = key,
            Count = actions.Count,
            Actions = [.. actions
                .OrderBy(action => action.Severity)
                .ThenBy(action => action.DueInDays ?? int.MaxValue)
                .Take(ActionsPerCategory)],
        };

    private static HrActionSeverity ByDate(int days) => days switch
    {
        < 0 => HrActionSeverity.Critical,
        <= UrgentDays => HrActionSeverity.Warning,
        _ => HrActionSeverity.Info,
    };

    // Documents with an expiry: right to work, visas, share codes, DBS.
    private async Task<IReadOnlyList<HrActionDto>> ExpiringDocumentsAsync(
        DateOnly today,
        CancellationToken cancellationToken)
    {
        List<Expiring> rows = await dbContext.HrDocuments
            .AsNoTracking()
            .Where(document => document.ExpiryDate != null
                && !document.IsArchived
                && document.TrashedAt == null
                && document.ExpiryDate <= today.AddDays(SoonDays))
            .Select(document => new Expiring(
                document.Id, document.Name, document.ExpiryDate!.Value))
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => Action(
                $"document-expiry:{row.Id:D}",
                row.ExpiresOn < today ? "Document expired" : "Document expiring",
                row.Name,
                row.ExpiresOn.DayNumber - today.DayNumber,
                "/organization/documents")),
        ];
    }

    // In post and missing the details somebody needs in an emergency. Info
    // rather than warning: nothing breaks today, and marking tidying as urgent
    // is how a list of twenty critical items gets ignored.
    private async Task<IReadOnlyList<HrActionDto>> IncompleteRecordsAsync(
        CancellationToken cancellationToken)
    {
        List<Named> rows = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => InPost.Contains(employee.Status)
                && (employee.EmergencyContactName == null
                    || employee.EmergencyContactPhone == null))
            .Select(employee => new Named(
                employee.Id, employee.FirstName + " " + employee.LastName))
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new HrActionDto
            {
                Id = $"missing-emergency-contact:{row.Id:D}",
                Title = "Missing emergency contact",
                Detail = row.Name,
                Severity = HrActionSeverity.Info,
                Href = "/organization/employees",
                EmployeeId = row.Id,
            }),
        ];
    }

    // Still advertised and past its own closing date. Through the domain's
    // expiry rule, so this and the recruitment board agree.
    private async Task<IReadOnlyList<HrActionDto>> StaleVacanciesAsync(
        DateOnly today,
        CancellationToken cancellationToken)
    {
        List<JobRequisition> rows = await dbContext.JobRequisitions
            .AsNoTracking()
            .Where(requisition => requisition.ExpiryDate != null)
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .Where(requisition => Requisitions.IsRecruiting(requisition.Status)
                    && Requisitions.HasExpired(requisition, today))
                .Select(requisition => Action(
                    $"vacancy-expired:{requisition.Id:D}",
                    "Vacancy past its closing date",
                    requisition.PositionTitle,
                    requisition.ExpiryDate!.Value.DayNumber - today.DayNumber,
                    "/talent/recruitment")),
        ];
    }

    private async Task<IReadOnlyList<HrActionDto>> OpenCasesAsync(
        DateOnly today,
        IReadOnlyDictionary<Guid, string> who,
        CancellationToken cancellationToken)
    {
        List<OpenCase> rows = await dbContext.ErCases
            .AsNoTracking()
            .Where(row => row.Stage != CaseStage.Closed)
            .Select(row => new OpenCase(row.Id, row.CaseNumber, row.SubjectEmployeeId, row.RaisedOn))
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new HrActionDto
            {
                Id = $"er-case-open:{row.Id:D}",
                Title = "Employee relations case open",
                Detail = row.SubjectEmployeeId is Guid subject
                    ? $"{row.CaseNumber} · {who.GetValueOrDefault(subject, "Unknown")}"
                    : row.CaseNumber,

                // Against the ageing of the case rather than a due date, which
                // an ER case does not have: a grievance open for two months is
                // the problem whatever stage it is at.
                Severity = today.DayNumber - row.RaisedOn.DayNumber > CaseAgeLimitDays
                    ? HrActionSeverity.Critical
                    : HrActionSeverity.Warning,
                Href = "/hr-action-center/employee-relations",
                DueInDays = CaseAgeLimitDays - (today.DayNumber - row.RaisedOn.DayNumber),
                EmployeeId = row.SubjectEmployeeId,
            }),
        ];
    }

    private async Task<IReadOnlyList<HrActionDto>> OverdueReviewsAsync(
        DateOnly today,
        IReadOnlyDictionary<Guid, string> who,
        CancellationToken cancellationToken)
    {
        List<PerformanceReview> rows = await dbContext.PerformanceReviews
            .AsNoTracking()
            .Where(review => review.DueDate < today)
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .Where(review => PerformanceReviews.IsOverdue(review, today))
                .Select(review => Action(
                    $"review-overdue:{review.Id:D}",
                    "Performance review overdue",
                    who.GetValueOrDefault(review.EmployeeId, "Unknown"),
                    review.DueDate.DayNumber - today.DayNumber,
                    "/talent/performance",
                    review.EmployeeId)),
        ];
    }

    private async Task<IReadOnlyList<HrActionDto>> LeaveAndAttendanceAsync(
        DateOnly today,
        IReadOnlyDictionary<Guid, string> who,
        CancellationToken cancellationToken)
    {
        List<HrActionDto> items = [];

        List<Submitted> timesheets = await dbContext.Timesheets
            .AsNoTracking()
            .Where(sheet => sheet.Status == TimesheetStatus.Submitted)
            .Select(sheet => new Submitted(sheet.Id, sheet.EmployeeId, sheet.WeekStart))
            .ToListAsync(cancellationToken);

        items.AddRange(timesheets.Select(sheet => new HrActionDto
        {
            Id = $"timesheet-awaiting:{sheet.Id:D}",
            Title = "Timesheet awaiting approval",
            Detail = $"{who.GetValueOrDefault(sheet.EmployeeId, "Unknown")} · "
                + $"week of {sheet.WeekStart:d MMM}",
            Severity = HrActionSeverity.Warning,
            Href = "/time-payroll/attendance",
            EmployeeId = sheet.EmployeeId,
        }));

        int corrections = await dbContext.AttendanceCorrections
            .AsNoTracking()
            .CountAsync(row => row.Status == CorrectionStatus.Pending, cancellationToken);

        if (corrections > 0)
        {
            // One row for the queue rather than one per correction: the screen
            // links straight to it, and a hundred identical rows would bury
            // everything else in the category.
            items.Add(new HrActionDto
            {
                Id = "attendance-corrections",
                Title = "Attendance corrections awaiting review",
                Detail = $"{corrections} pending",
                Severity = HrActionSeverity.Warning,
                Href = "/time-payroll/attendance",
            });
        }

        List<Booking> starting = await dbContext.LeaveRequests
            .AsNoTracking()
            .Where(request => request.Status == LeaveRequestStatus.Pending
                && request.StartDate <= today.AddDays(UrgentDays))
            .Select(request => new Booking(request.Id, request.EmployeeId, request.StartDate))
            .ToListAsync(cancellationToken);

        items.AddRange(starting.Select(request => Action(
            $"leave-undecided:{request.Id:D}",
            "Leave starting without a decision",
            who.GetValueOrDefault(request.EmployeeId, "Unknown"),
            request.StartDate.DayNumber - today.DayNumber,
            "/time-payroll/leave",
            request.EmployeeId)));

        return items;
    }

    private async Task<IReadOnlyList<HrActionDto>> LearningAsync(
        DateOnly today,
        IReadOnlyDictionary<Guid, string> who,
        CancellationToken cancellationToken)
    {
        List<HrActionDto> items = [];

        List<Due> courses = await dbContext.CourseEnrollments
            .AsNoTracking()
            .Where(enrolment => enrolment.Status != EnrollmentStatus.Completed
                && enrolment.Status != EnrollmentStatus.Dropped
                && enrolment.DueOn != null
                && enrolment.DueOn < today)
            .Select(enrolment => new Due(
                enrolment.Id, enrolment.EmployeeId, enrolment.DueOn!.Value))
            .ToListAsync(cancellationToken);

        items.AddRange(courses.Select(course => Action(
            $"course-overdue:{course.Id:D}",
            "Mandatory course overdue",
            who.GetValueOrDefault(course.EmployeeId, "Unknown"),
            course.DueOn.DayNumber - today.DayNumber,
            "/talent/training",
            course.EmployeeId)));

        List<Expiry> certificates = await dbContext.Certifications
            .AsNoTracking()
            .Where(certificate => certificate.ExpiresOn != null
                && certificate.ExpiresOn <= today.AddDays(Certifications.SoonDays))
            .Select(certificate => new Expiry(
                certificate.Id,
                certificate.EmployeeId,
                certificate.Title,
                certificate.ExpiresOn!.Value))
            .ToListAsync(cancellationToken);

        items.AddRange(certificates.Select(certificate => Action(
            $"certification-expiry:{certificate.Id:D}",
            Certifications.StatusOf(certificate.ExpiresOn, today) == CertStatus.Expired
                ? "Certification expired"
                : "Certification expiring",
            $"{who.GetValueOrDefault(certificate.EmployeeId, "Unknown")} · {certificate.Title}",
            certificate.ExpiresOn.DayNumber - today.DayNumber,
            "/talent/training",
            certificate.EmployeeId)));

        return items;
    }

    // Kit still on somebody who has gone. Critical because it is the one on
    // this list that walks out of the building.
    private async Task<IReadOnlyList<HrActionDto>> AssetsWithLeaversAsync(
        IReadOnlyDictionary<Guid, string> who,
        CancellationToken cancellationToken)
    {
        List<Held> rows = await dbContext.Assets
            .AsNoTracking()
            .Where(asset => asset.AssignedToEmployeeId != null
                && dbContext.Employees.Any(employee =>
                    employee.Id == asset.AssignedToEmployeeId
                    && !InPost.Contains(employee.Status)))
            .Select(asset => new Held(
                asset.Id, asset.Name, asset.Tag, asset.AssignedToEmployeeId!.Value))
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new HrActionDto
            {
                Id = $"asset-with-leaver:{row.Id:D}",
                Title = "Asset still issued to a leaver",
                Detail = $"{row.Tag} {row.Name} · {who.GetValueOrDefault(row.Holder, "Unknown")}",
                Severity = HrActionSeverity.Critical,
                Href = "/organization/assets",
                EmployeeId = row.Holder,
            }),
        ];
    }

    // Live, and waiting on a step that resolved to nobody. Nothing will happen
    // to these without somebody fixing the org chart and asking the chain to
    // look again, so they age quietly in no queue at all — which is exactly
    // what the Action Centre is for.
    private async Task<IReadOnlyList<HrActionDto>> StuckApprovalsAsync(
        CancellationToken cancellationToken)
    {
        List<Stuck> rows = await dbContext.ApprovalInstances
            .AsNoTracking()
            .Where(instance => instance.Status == ApprovalStatus.InProgress
                && dbContext.ApprovalStepInstances.Any(step =>
                    step.InstanceId == instance.Id
                    && step.Status == ApprovalStepStatus.Pending
                    && step.ResolvedEmployeeId == null
                    && step.ResolvedRoleId == null))
            .Select(instance => new Stuck(instance.Id, instance.DocumentType))
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new HrActionDto
            {
                Id = $"approval-stuck:{row.Id:D}",
                Title = "Approval waiting on nobody",
                Detail = row.DocumentType,
                Severity = HrActionSeverity.Critical,
                Href = "/hr-action-center/submissions",
            }),
        ];
    }

    private async Task<IReadOnlyList<HrActionDto>> RiskAsync(
        DateOnly today,
        IReadOnlyDictionary<Guid, string> who,
        CancellationToken cancellationToken)
    {
        List<OhReferral> referrals = await dbContext.OhReferrals
            .AsNoTracking()
            .Where(referral => referral.ReviewDate != null && referral.ClosedOn == null)
            .ToListAsync(cancellationToken);

        return
        [
            .. referrals
                .Where(referral => OhRules.ReviewIsDue(referral, today))
                .Select(referral => Action(
                    $"oh-review-due:{referral.Id:D}",
                    "Occupational health review due",

                    // The reference, not the reason. The clinical category is
                    // special-category data and an HR dashboard is the widest
                    // surface in the product.
                    $"{referral.ReferenceNumber} · "
                    + who.GetValueOrDefault(referral.EmployeeId, "Unknown"),
                    referral.ReviewDate!.Value.DayNumber - today.DayNumber,
                    "/time-payroll/occupational-health",
                    referral.EmployeeId)),
        ];
    }

    private static HrActionDto Action(
        string id,
        string title,
        string? detail,
        int dueInDays,
        string href,
        Guid? employeeId = null) =>
        new()
        {
            Id = id,
            Title = title,
            Detail = detail,
            Severity = ByDate(dueInDays),
            Href = href,
            DueInDays = dueInDays,
            EmployeeId = employeeId,
        };

    private sealed record Named(Guid Id, string Name);

    private sealed record Expiring(Guid Id, string Name, DateOnly ExpiresOn);

    private sealed record OpenCase(
        Guid Id, string CaseNumber, Guid? SubjectEmployeeId, DateOnly RaisedOn);

    private sealed record Submitted(Guid Id, Guid EmployeeId, DateOnly WeekStart);

    private sealed record Booking(Guid Id, Guid EmployeeId, DateOnly StartDate);

    private sealed record Due(Guid Id, Guid EmployeeId, DateOnly DueOn);

    private sealed record Expiry(Guid Id, Guid EmployeeId, string Title, DateOnly ExpiresOn);

    private sealed record Held(Guid Id, string Name, string Tag, Guid Holder);

    private sealed record Stuck(Guid Id, string DocumentType);

    // ── self-service ────────────────────────────────────────────────────────

    public async Task<MyDashboardDto> MineAsync(CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);

        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            // The account owner permanently has no employee record: they run
            // the company's account rather than working there. An empty day is
            // the right answer, not a 404 on a page they can open.
            return new MyDashboardDto
            {
                AsOf = today,
                Today = Empty(),
                Pending = [],
                TeamOnLeave = [],
            };
        }

        // Delegated, both of them. The dashboard's clock and the attendance
        // screen's clock are then the same answer by construction, and the task
        // counts match the task page's.
        AttendanceDayDto? day = await attendance.MineAsync(today, cancellationToken);
        TaskTally tasks = await taskList.SummaryAsync(cancellationToken);

        LeaveOnDay? mine = await LeaveOnAsync(me, today, cancellationToken);

        List<MyPendingItemDto> pending = await PendingAsync(me, today, cancellationToken);

        return new MyDashboardDto
        {
            AsOf = today,
            Today = new MyDayDto
            {
                Clock = day?.State ?? ClockState.NotStarted,
                ClockedInAt = day?.ClockInAt,
                WorkedHours = day?.WorkedHours ?? 0m,
                OnLeave = mine?.LeaveType,
                TasksOutstanding = tasks.Outstanding,
                TasksOverdue = tasks.Overdue,
                PendingActions = pending.Count,
            },
            Pending = pending,
            TeamOnLeave = await TeamOnLeaveAsync(me, today, cancellationToken),
        };
    }

    private static MyDayDto Empty() => new()
    {
        Clock = ClockState.NotStarted,
        WorkedHours = 0m,
        TasksOutstanding = 0,
        TasksOverdue = 0,
        PendingActions = 0,
    };

    private sealed record LeaveOnDay(string LeaveType, DateOnly EndDate);

    // Approved leave covering a day. Pending leave is not being on leave: the
    // days are reserved against the balance, but somebody awaiting a decision
    // is at their desk.
    private async Task<LeaveOnDay?> LeaveOnAsync(
        Guid employeeId,
        DateOnly day,
        CancellationToken cancellationToken) =>
        await dbContext.LeaveRequests
            .AsNoTracking()
            .Where(request => request.EmployeeId == employeeId
                && request.Status == LeaveRequestStatus.Approved
                && request.StartDate <= day
                && request.EndDate >= day)
            .Select(request => new LeaveOnDay(
                dbContext.LeaveTypes
                    .Where(type => type.Id == request.LeaveTypeId)
                    .Select(type => type.Name)
                    .FirstOrDefault() ?? "Leave",
                request.EndDate))
            .FirstOrDefaultAsync(cancellationToken);

    // Who else on the team is off today.
    //
    // "The team" is whoever shares a line manager, plus the manager — which is
    // what somebody means by it, and avoids a department-wide list in a company
    // where a department is two hundred people. Somebody with no manager
    // recorded has no team to report, rather than the whole company.
    private async Task<IReadOnlyList<TeamLeaveDto>> TeamOnLeaveAsync(
        Guid employeeId,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        Guid? managerId = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .Select(employee => employee.ManagerId)
            .FirstOrDefaultAsync(cancellationToken);

        if (managerId is not Guid manager)
        {
            return [];
        }

        return await dbContext.LeaveRequests
            .AsNoTracking()
            .Where(request => request.Status == LeaveRequestStatus.Approved
                && request.StartDate <= day
                && request.EndDate >= day
                && request.EmployeeId != employeeId
                && dbContext.Employees.Any(employee =>
                    employee.Id == request.EmployeeId
                    && (employee.ManagerId == manager || employee.Id == manager)))
            .Select(request => new TeamLeaveDto
            {
                EmployeeId = request.EmployeeId,
                Name = dbContext.Employees
                    .Where(employee => employee.Id == request.EmployeeId)
                    .Select(employee => employee.FirstName + " " + employee.LastName)
                    .FirstOrDefault() ?? "Unknown",
                LeaveType = dbContext.LeaveTypes
                    .Where(type => type.Id == request.LeaveTypeId)
                    .Select(type => type.Name)
                    .FirstOrDefault() ?? "Leave",

                // The day after their last day off. What the card means by
                // "back on", and a figure nobody should have to add one to.
                ReturnsOn = request.EndDate.AddDays(1),
            })
            .OrderBy(row => row.Name)
            .ToListAsync(cancellationToken);
    }

    // What is still waiting on this person.
    //
    // Four sources, chosen because each one is an obligation that persists until
    // discharged. Notifications are deliberately not among them: a notification
    // records that something happened, and the course it announced last week is
    // still outstanding today — which is the distinction this list exists for.
    private async Task<List<MyPendingItemDto>> PendingAsync(
        Guid employeeId,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        List<MyPendingItemDto> items = [];

        List<LeaveRequest> awaiting = await dbContext.LeaveRequests
            .AsNoTracking()
            .Where(request => request.EmployeeId == employeeId
                && request.Status == LeaveRequestStatus.Pending)
            .OrderBy(request => request.StartDate)
            .ToListAsync(cancellationToken);

        items.AddRange(awaiting.Select(request => new MyPendingItemDto
        {
            Kind = "leave-request",
            Label = "Leave request pending",
            Detail = $"{request.StartDate:d MMM} to {request.EndDate:d MMM}",
            Href = "/employee/leave",

            // Starting before it has been decided. Nobody can plan around a
            // request that is still pending on the morning it begins.
            Urgent = request.StartDate <= today.AddDays(LeaveDecisionWarningDays),
        }));

        List<Course> overdue = await dbContext.CourseEnrollments
            .AsNoTracking()
            .Where(enrolment => enrolment.EmployeeId == employeeId
                && enrolment.Status != EnrollmentStatus.Completed
                && enrolment.Status != EnrollmentStatus.Dropped
                && enrolment.DueOn != null
                && enrolment.DueOn <= today.AddDays(CourseWarningDays))
            .Join(
                dbContext.Courses.AsNoTracking(),
                enrolment => enrolment.CourseId,
                course => course.Id,
                (enrolment, course) => new Course(course.Title, enrolment.DueOn!.Value))
            .ToListAsync(cancellationToken);

        items.AddRange(overdue
            .OrderBy(course => course.DueOn)
            .Select(course => new MyPendingItemDto
            {
                Kind = "course",
                Label = course.DueOn < today ? "Course overdue" : "Course due",
                Detail = course.Title,
                Href = "/employee/training",
                Urgent = course.DueOn < today,
            }));

        // Assigned or shared, needs acknowledging, and has not been. Through the
        // same reach the documents module uses, so a document somebody cannot
        // see is not listed as something they must sign.
        List<string> unsigned = await dbContext.HrDocuments
            .AsNoTracking()
            .Where(document => document.RequiresAcknowledgement
                && document.TrashedAt == null
                && !document.IsArchived
                && (document.Assignment == AssignmentScope.Global
                    || dbContext.DocumentShares.Any(share =>
                        share.DocumentId == document.Id && share.EmployeeId == employeeId))
                && !dbContext.DocumentAcknowledgements.Any(ack =>
                    ack.DocumentId == document.Id && ack.EmployeeId == employeeId))
            .Select(document => document.Name)
            .ToListAsync(cancellationToken);

        items.AddRange(unsigned
            .OrderBy(title => title, StringComparer.OrdinalIgnoreCase)
            .Select(title => new MyPendingItemDto
            {
                Kind = "acknowledgement",
                Label = "Policy acknowledgement",
                Detail = title,
                Href = "/employee/documents",

                // Not urgent on its own: an unsigned policy has no date, and
                // marking everything urgent is the same as marking nothing.
                Urgent = false,
            }));

        // Urgent first, then by the order each source produced.
        return [.. items.OrderByDescending(item => item.Urgent)];
    }

    private sealed record Course(string Title, DateOnly DueOn);

    // ── untaken leave ───────────────────────────────────────────────────────

    public async Task<UntakenLeaveDashboardDto> UntakenLeaveAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);

        // Annual leave only. Sick leave is reported rather than booked, and
        // nobody is at risk of failing to use it.
        LeaveType? annual = await dbContext.LeaveTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(type => type.Code == AnnualLeaveCode, cancellationToken);

        if (annual is null)
        {
            return new UntakenLeaveDashboardDto { AsOf = today, AtRisk = [] };
        }

        LeavePolicy? policy = await dbContext.LeavePolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.LeaveTypeId == annual.Id && candidate.IsActive,
                cancellationToken);

        // What they would keep. Unlimited carry-over means nothing is at risk,
        // which is the case a flat "more than ten days left" gets wrong — it
        // would chase people who lose nothing.
        decimal carried = policy is { CarryOverAllowed: true }
            ? policy.MaxCarryOverDays
            : 0m;

        // Through the balance service rather than recomputed here. Accrual,
        // carry-over expiry and adjustments all feed Available, and a second
        // arithmetic would disagree with the balances screen the first time one
        // of those rules changed.
        PagedResult<LeaveBalanceDto> balances = await leaveBalances.ListAsync(
            new LeaveBalanceQuery
            {
                LeaveTypeId = annual.Id,
                AsAt = today,
                PageSize = PagedQuery.MaxPageSize,
            },
            cancellationToken);

        List<UntakenLeaveRowDto> atRisk =
        [
            .. balances.Items
                .Select(balance => new
                {
                    Balance = balance,
                    Lost = balance.Available - carried,
                })
                .Where(row => row.Lost > 0m)
                .Select(row => new UntakenLeaveRowDto
                {
                    EmployeeId = row.Balance.EmployeeId,
                    EmployeeName = row.Balance.EmployeeName,
                    Department = row.Balance.DepartmentName,
                    Remaining = row.Balance.Available,
                    AtRiskDays = row.Lost,
                })

                // Most at risk first: the card shows the first few rows and the
                // person losing a fortnight is the one to ring.
                .OrderByDescending(row => row.AtRiskDays)
                .ThenBy(row => row.EmployeeName, StringComparer.OrdinalIgnoreCase),
        ];

        return new UntakenLeaveDashboardDto
        {
            AsOf = today,
            // The day after the year starts again, less one. Taken from the
            // balances rather than recomputed from the tenant's turnover, so
            // the deadline shown and the year the figures were counted in are
            // the same year by construction.
            LeaveYearEnds = balances.Items.Count == 0
                ? null
                : balances.Items[0].LeaveYearStart.AddYears(1).AddDays(-1),
            AtRisk = atRisk,
        };
    }

    private async Task<Dictionary<Guid, ApprovalStatus>> ChainStatusesAsync(
        IEnumerable<Guid?> instanceIds,
        CancellationToken cancellationToken)
    {
        List<Guid> ids = [.. instanceIds.OfType<Guid>().Distinct()];

        return ids.Count == 0
            ? []
            : await dbContext.ApprovalInstances
                .AsNoTracking()
                .Where(instance => ids.Contains(instance.Id))
                .ToDictionaryAsync(
                    instance => instance.Id, instance => instance.Status, cancellationToken);
    }

    // Null means no chain, which Derive reads as a draft — distinct from a
    // chain that exists and has not started.
    private static ApprovalStatus? Chain(
        IReadOnlyDictionary<Guid, ApprovalStatus> chains,
        Guid? instanceId) =>
        instanceId is Guid id && chains.TryGetValue(id, out ApprovalStatus status)
            ? status
            : null;

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
