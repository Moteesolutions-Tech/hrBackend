using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Attendance;
using Motee.Application.Auth;
using Motee.Application.Dashboard;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Domain.Offboarding;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The HR dashboard's figures.
//
// Every number here is an aggregate over data the other modules own, which makes
// the failure mode quiet: a miscount does not throw, it just puts a wrong number
// on the first screen anybody sees after signing in. So the clock is pinned and
// the roster is small enough to count by hand.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class DashboardServiceTests(PostgresFixture fixture)
{
    // A Monday, so the week-boundary arithmetic has a known answer. Pinned
    // rather than taken from the system clock: "new hires this month" and
    // "birthdays within seven days" are date arithmetic, and a test that passes
    // in June and fails on New Year's Eve is worse than no test.
    private static readonly DateOnly Today = new(2026, 6, 15);

    private readonly MovableClock _clock = new(
        new DateTimeOffset(Today.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _engineeringId;
    private Guid _salesId;

    private Guid _caraId;
    private Guid _bolaId;
    private Guid _deleId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        // Registered before AddInfrastructure, which uses TryAdd, so this wins.
        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        IDepartmentService departments = Resolve<IDepartmentService>(provider);

        _engineeringId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _salesId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        // Four people, each standing for one thing the counting has to get right.
        //
        // Cara  — in post a long time, birthday three days away.
        // Bola  — started this month, works remotely.
        // Dele  — left this month, so counts as a leaver and not as headcount.
        // Emeka — invited but not started, so counts as nothing at all.
        _caraId = await HireAsync(
            provider, "Cara", _engineeringId, new DateOnly(2024, 1, 10),
            gender: "female", dateOfBirth: new DateOnly(1990, 6, 18));

        _bolaId = await HireAsync(
            provider, "Bola", _engineeringId, new DateOnly(2026, 6, 1),
            gender: "male", dateOfBirth: new DateOnly(1992, 1, 5),
            workMode: WorkMode.Remote);

        _deleId = await HireAsync(
            provider, "Dele", _salesId, new DateOnly(2023, 3, 1),
            gender: "male", employmentType: EmploymentType.Contract);

        await HireAsync(
            provider, "Emeka", _salesId, new DateOnly(2026, 7, 1),
            status: EmployeeStatus.Pending);

        await LeftAsync(_deleId, new DateOnly(2026, 6, 5), ExitReason.Resignation);

        return provider;
    }

    private static async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid departmentId,
        DateOnly startDate,
        string? gender = null,
        DateOnly? dateOfBirth = null,
        WorkMode workMode = WorkMode.Onsite,
        EmploymentType employmentType = EmploymentType.FullTime,
        EmployeeStatus status = EmployeeStatus.Active) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            EmploymentType = employmentType,
            Status = status,
            StartDate = startDate,
            DateOfBirth = dateOfBirth,
            Gender = gender,
            WorkMode = workMode,
        })).Employee!.Id;

    // Leaving is offboarding's job, so the test writes what offboarding would
    // leave behind rather than driving a whole exit through the chain.
    private async Task LeftAsync(Guid employeeId, DateOnly on, ExitReason reason)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Employee employee = await seed.Employees.FirstAsync(
            candidate => candidate.Id == employeeId);

        employee.DateOfLeaving = on;
        employee.Status = EmployeeStatus.Inactive;

        seed.OffboardingRecords.Add(new OffboardingRecord
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            Status = OffboardingStatus.Completed,
            ExitReason = reason,
            LastWorkingDate = on,
            InitiatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static Task<PeopleDashboardDto> PeopleAsync(ServiceProvider provider) =>
        Resolve<IDashboardService>(provider).PeopleAsync();

    private static Task<AttendanceDashboardDto> AttendanceAsync(ServiceProvider provider) =>
        Resolve<IDashboardService>(provider).AttendanceAsync();

    private static int CountOf(IReadOnlyList<CategoryCountDto> counts, string label) =>
        counts.FirstOrDefault(row => row.Label == label)?.Count ?? 0;

    // ── headcount ───────────────────────────────────────────────────────────

    // Three of the four are not headcount, each for a different reason.
    [SkippableFact]
    public async Task HeadcountCountsPeopleInPostAndNobodyElse()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        HeadcountDto headcount = (await PeopleAsync(provider)).Headcount;

        Assert.Equal(2, headcount.Total);
        Assert.Equal(1, headcount.NewHiresThisMonth);
        Assert.Equal(1, headcount.LeaversThisMonth);
        Assert.Equal(1, headcount.Remote);
    }

    [SkippableFact]
    public async Task TheDateEverythingWasCountedAgainstIsOnTheAnswer()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(Today, (await PeopleAsync(provider)).AsOf);
    }

    [SkippableFact]
    public async Task ABirthdayInsideTheWeekIsListedWithACountdown()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        BirthdayDto birthday = Assert.Single((await PeopleAsync(provider)).UpcomingBirthdays);

        Assert.Equal(_caraId, birthday.EmployeeId);

        // This year's, not 1990 — the card shows a date, not a year of birth.
        Assert.Equal(new DateOnly(2026, 6, 18), birthday.Date);
        Assert.Equal(3, birthday.DaysUntil);
    }

    // The arithmetic that breaks every December: "within seven days" of the 28th
    // reaches into January, and comparing month-and-day as numbers does not.
    [SkippableFact]
    public async Task ABirthdayInTheNewYearStillCountsFromDecember()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        _clock.Advance(new DateOnly(2026, 12, 28).ToDateTime(new TimeOnly(9, 0))
            - Today.ToDateTime(new TimeOnly(9, 0)));

        // Bola was born on 5 January, eight days away — still outside.
        Assert.Empty((await PeopleAsync(provider)).UpcomingBirthdays);

        _clock.Advance(TimeSpan.FromDays(1));

        BirthdayDto birthday = Assert.Single((await PeopleAsync(provider)).UpcomingBirthdays);

        Assert.Equal(_bolaId, birthday.EmployeeId);
        Assert.Equal(new DateOnly(2027, 1, 5), birthday.Date);
        Assert.Equal(7, birthday.DaysUntil);
    }

    // ── breakdowns ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheBreakdownsCountOnlyPeopleInPost()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PeopleDashboardDto people = await PeopleAsync(provider);

        // Dele has left and Emeka has not started, so Sales is empty and is not
        // listed at all — a department reading zero looks like a bug.
        Assert.Equal(2, CountOf(people.ByDepartment, "Engineering"));
        Assert.Equal(0, CountOf(people.ByDepartment, "Sales"));

        Assert.Equal(2, CountOf(people.ByEmploymentType, "FullTime"));
        Assert.Equal(0, CountOf(people.ByEmploymentType, "Contract"));
    }

    // Unrecorded is its own answer. Folding it into "other" would turn a gap in
    // the data into a claim about somebody.
    [SkippableFact]
    public async Task TheGenderSplitSaysWhatWasNotRecorded()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Funmi", _engineeringId, new DateOnly(2025, 2, 1));

        IReadOnlyList<CategoryCountDto> split = (await PeopleAsync(provider)).ByGender;

        Assert.Equal(1, CountOf(split, "female"));
        Assert.Equal(1, CountOf(split, "male"));
        Assert.Equal(1, CountOf(split, "Not recorded"));
    }

    // Reconstructed from start and leaving dates rather than read from a
    // snapshot table, so a month nobody wrote a snapshot for is not a hole.
    [SkippableFact]
    public async Task TheHeadcountTrendIsRebuiltFromStartAndLeavingDates()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<HeadcountPointDto> trend = (await PeopleAsync(provider)).HeadcountTrend;

        Assert.Equal(12, trend.Count);
        Assert.Equal(new DateOnly(2025, 7, 1), trend[0].Month);
        Assert.Equal(new DateOnly(2026, 6, 1), trend[^1].Month);

        // May 2026: Cara and Dele in post, Bola not yet started.
        HeadcountPointDto may = trend[^2];

        Assert.Equal(2, may.Headcount);
        Assert.Equal(0, may.Joiners);
        Assert.Equal(0, may.Leavers);

        // June: Bola joins, Dele goes, so headcount is unchanged but the month
        // was not quiet — which is the whole reason joiners and leavers are
        // reported beside it.
        HeadcountPointDto june = trend[^1];

        Assert.Equal(2, june.Headcount);
        Assert.Equal(1, june.Joiners);
        Assert.Equal(1, june.Leavers);
        Assert.Equal(0, june.Net);
    }

    [SkippableFact]
    public async Task TurnoverSplitsThoseWhoChoseToGoFromThoseWhoDidNot()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid gone = await HireAsync(
            provider, "Grace", _salesId, new DateOnly(2022, 5, 1));

        await LeftAsync(gone, new DateOnly(2026, 5, 20), ExitReason.Redundancy);

        TurnoverDto turnover = (await PeopleAsync(provider)).Turnover;

        Assert.Equal(1, turnover.Voluntary);
        Assert.Equal(1, turnover.Involuntary);
        Assert.Equal(0, turnover.Unclassified);
        Assert.True(turnover.Rate > 0, $"Expected a turnover rate, got {turnover.Rate}.");
    }

    // An exit nobody recorded a reason for is said to be unclassified rather
    // than guessed at.
    [SkippableFact]
    public async Task AnExitWithNoRecordIsNotGuessedAt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid gone = await HireAsync(provider, "Hadiza", _salesId, new DateOnly(2022, 5, 1));

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            Employee employee = await seed.Employees.FirstAsync(
                candidate => candidate.Id == gone);

            employee.DateOfLeaving = new DateOnly(2026, 5, 20);
            employee.Status = EmployeeStatus.Inactive;

            await seed.SaveChangesAsync();
        }

        TurnoverDto turnover = (await PeopleAsync(provider)).Turnover;

        Assert.Equal(1, turnover.Voluntary);
        Assert.Equal(0, turnover.Involuntary);
        Assert.Equal(1, turnover.Unclassified);
    }

    // ── attendance ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TodayIsCountedByStateWithEachStateKeptApart()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await LogAsync(provider, _caraId, Today, 8, 17);
        await LogAsync(provider, _bolaId, Today, 11, 17);

        AttendanceTodayDto today = (await AttendanceAsync(provider)).Today;

        Assert.Equal(1, today.Present);
        Assert.Equal(1, today.Late);
        Assert.Equal(2, today.Total);
    }

    [SkippableFact]
    public async Task TheLateListNamesPeopleRatherThanCountingThem()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await LogAsync(provider, _bolaId, Today, 11, 17);

        AttendancePersonDto late = Assert.Single((await AttendanceAsync(provider)).Late);

        Assert.Equal(_bolaId, late.EmployeeId);
        Assert.Equal("Bola Okafor", late.Name);
        Assert.Equal("Engineering", late.Department);
        Assert.NotNull(late.ClockInAt);
    }

    [SkippableFact]
    public async Task TheDailySeriesCoversThirtyDaysOldestFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await LogAsync(provider, _caraId, Today.AddDays(-3), 8, 17);
        await LogAsync(provider, _bolaId, Today.AddDays(-3), 11, 17);

        IReadOnlyList<AttendancePointDto> daily = (await AttendanceAsync(provider)).Daily;

        Assert.Equal(30, daily.Count);
        Assert.Equal(Today.AddDays(-29), daily[0].Date);
        Assert.Equal(Today, daily[^1].Date);

        AttendancePointDto day = daily.Single(point => point.Date == Today.AddDays(-3));

        Assert.Equal(1, day.Present);
        Assert.Equal(1, day.Late);
    }

    // Averaged per day rather than totalled, so a bank holiday does not read as
    // a collapse in attendance.
    [SkippableFact]
    public async Task TheWeeklySeriesAveragesRatherThanTotals()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        // Five working days last week, both of them in every day.
        for (int back = 3; back <= 7; back++)
        {
            await LogAsync(provider, _caraId, Today.AddDays(-back), 8, 17);
            await LogAsync(provider, _bolaId, Today.AddDays(-back), 8, 17);
        }

        IReadOnlyList<WeeklyAttendancePointDto> weekly =
            (await AttendanceAsync(provider)).Weekly;

        Assert.Equal(3, weekly.Count);

        // Monday-starting weeks, oldest first.
        Assert.Equal(Today.AddDays(-14), weekly[0].WeekStart);
        Assert.Equal(Today, weekly[^1].WeekStart);

        // Two people a day, not ten over the week.
        Assert.Equal(2, weekly[^2].Present);
    }

    // ── leave ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task LeaveIsSplitByKindAndCountedThreeWays()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await BookAsync("annual", new DateOnly(2026, 4, 6), 5, LeaveRequestStatus.Approved);
        await BookAsync("annual", new DateOnly(2026, 5, 11), 2, LeaveRequestStatus.Pending);
        await BookAsync("sick", new DateOnly(2026, 2, 2), 1, LeaveRequestStatus.Approved);

        LeaveBreakdownDto leave = (await AttendanceAsync(provider)).Leave;

        Assert.Equal(2, leave.Annual.Requests);
        Assert.Equal(7m, leave.Annual.Days);
        Assert.Equal(1, leave.Annual.Pending);

        Assert.Equal(1, leave.Sick.Requests);
        Assert.Equal(1m, leave.Sick.Days);
        Assert.Equal(0, leave.Sick.Pending);
    }

    // The code is what the split reads, not the name. A company renaming "Annual
    // Leave" to "Holiday" must not empty the annual tile.
    [SkippableFact]
    public async Task ARenamedLeaveTypeStillCountsAsWhatItIs()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            LeaveType annual = await seed.LeaveTypes.FirstAsync(type => type.Code == "annual");

            annual.Name = "Holiday";

            await seed.SaveChangesAsync();
        }

        await BookAsync("annual", new DateOnly(2026, 4, 6), 5, LeaveRequestStatus.Approved);

        Assert.Equal(1, (await AttendanceAsync(provider)).Leave.Annual.Requests);
    }

    // A type the company invented is neither annual nor sick, and there is no
    // fixed list it could be checked against.
    [SkippableFact]
    public async Task ACompanysOwnLeaveTypeFallsUnderOther()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid studyId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.LeaveTypes.Add(new LeaveType
            {
                Id = studyId,
                TenantId = _tenantId,
                Name = "Study Leave",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        await BookAsync(studyId, new DateOnly(2026, 3, 2), 3, LeaveRequestStatus.Approved);

        LeaveBreakdownDto leave = (await AttendanceAsync(provider)).Leave;

        Assert.Equal(1, leave.Other.Requests);
        Assert.Equal(3m, leave.Other.Days);
        Assert.Equal(0, leave.Annual.Requests);
    }

    // Twelve months back from today, so last year's leave does not sit in this
    // year's tile.
    [SkippableFact]
    public async Task LeaveOlderThanTheWindowIsLeftOut()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await BookAsync("annual", new DateOnly(2025, 5, 6), 5, LeaveRequestStatus.Approved);

        Assert.Equal(0, (await AttendanceAsync(provider)).Leave.Annual.Requests);
    }

    // Cancelled and refused bookings are not leave anybody took or is waiting on.
    [SkippableFact]
    public async Task ARefusedBookingIsNotCounted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await BookAsync("annual", new DateOnly(2026, 4, 6), 5, LeaveRequestStatus.Rejected);
        await BookAsync("annual", new DateOnly(2026, 4, 20), 2, LeaveRequestStatus.Cancelled);

        Assert.Equal(0, (await AttendanceAsync(provider)).Leave.Annual.Requests);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    // Lagos, because the tenant is Nigerian and the default working day it was
    // seeded with runs 08:00–17:00 local. Punching in UTC would make an eight
    // o'clock start read as nine, and every one of these people late.
    private static DateTimeOffset At(DateOnly day, int hour) =>
        new(day.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.FromHours(1));

    private static Task LogAsync(
        ServiceProvider provider, Guid employeeId, DateOnly day, int from, int to) =>
        Resolve<IAttendanceService>(provider).LogAsync(new LogAttendanceRequest
        {
            EmployeeId = employeeId,
            Date = day,
            ClockInAt = At(day, from),
            ClockOutAt = At(day, to),
        });

    private async Task BookAsync(
        string code, DateOnly start, decimal days, LeaveRequestStatus status)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        LeaveType type = await seed.LeaveTypes.FirstAsync(candidate => candidate.Code == code);

        await BookAsync(type.Id, start, days, status);
    }

    // Written straight in, like the leave year-end tests: the notice and balance
    // rules would refuse bookings dated across a whole year, and none of them is
    // what this is checking.
    private async Task BookAsync(
        Guid leaveTypeId, DateOnly start, decimal days, LeaveRequestStatus status)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.LeaveRequests.Add(new LeaveRequest
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = _caraId,
            LeaveTypeId = leaveTypeId,
            StartDate = start,
            EndDate = start.AddDays((int)days - 1),
            TotalDays = days,
            LeaveYearStart = new DateOnly(start.Year, 1, 1),
            Status = status,
            SubmittedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }
}
