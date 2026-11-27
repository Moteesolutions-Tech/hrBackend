using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Attendance;
using Motee.Application.Auth;
using Motee.Application.Dashboard;
using Motee.Application.Employees;
using Motee.Application.Leave;
using Motee.Application.Organisation;
using Motee.Domain.Attendance;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The employee's own front page.
//
// Composed by delegating: the clock comes back from the attendance service and
// the task counts from the task service, so this page and those screens cannot
// disagree. What is derived here is only what nothing else answered — whether
// somebody is off today, what is still waiting on them, and who on their team
// is away.
[Collection(PostgresCollection.Name)]
public class MyDashboardTests(PostgresFixture fixture)
{
    // A Tuesday, so "today" is a working day and a clock-in is legitimate.
    private static readonly DateOnly Today = new(2026, 9, 22);

    private readonly MovableClock _clock = new(
        new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 54)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _annualTypeId;
    private Guid _managerId;
    private Guid _adaId;
    private Guid _adaUserId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Owner",
            LastName = "Okafor",
            Email = "owner@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _managerId = await HireAsync(provider, "Cara");
        _adaId = await HireAsync(provider, "Ada", _managerId);

        _adaUserId = await AccountAsync(_adaId, "ada");

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            _annualTypeId = await seed.LeaveTypes
                .Where(type => type.Code == "annual").Select(type => type.Id).FirstAsync();
        }

        ActAs(provider, _adaUserId);

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static void ActAs(ServiceProvider provider, Guid userId) =>
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

    private static Task<MyDashboardDto> MineAsync(ServiceProvider provider) =>
        Resolve<IDashboardService>(provider).MineAsync();

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid? managerId = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Adeyemi",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            ManagerId = managerId,
            StartDate = new DateOnly(2021, 1, 10),
        })).Employee!.Id;

    private async Task<Guid> AccountAsync(Guid employeeId, string handle)
    {
        Guid userId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Users.Add(new ApplicationUser
        {
            Id = userId,
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = handle,
            LastName = "Adeyemi",
            Email = $"{handle}@acme.com",
            NormalizedEmail = $"{handle.ToUpperInvariant()}@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = _clock.GetUtcNow(),
        });

        await seed.SaveChangesAsync();

        return userId;
    }

    // Written straight in: booking through the leave service would drag an
    // approval chain into a test about what the dashboard reports.
    private async Task LeaveAsync(
        Guid employeeId,
        DateOnly from,
        DateOnly to,
        LeaveRequestStatus status = LeaveRequestStatus.Approved)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.LeaveRequests.Add(new LeaveRequest
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            LeaveTypeId = _annualTypeId,
            StartDate = from,
            EndDate = to,
            TotalDays = (to.DayNumber - from.DayNumber) + 1,
            Status = status,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        });

        await seed.SaveChangesAsync();
    }

    // ── the clock, delegated ────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheDayStartsNotClockedIn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        MyDashboardDto board = await MineAsync(provider);

        Assert.Equal(Today, board.AsOf);
        Assert.Equal(ClockState.NotStarted, board.Today.Clock);
        Assert.Null(board.Today.ClockedInAt);
        Assert.Equal(0m, board.Today.WorkedHours);
    }

    // The same answer the attendance screen gives, because it is the same
    // service answering. A second reading of the punches here is how a
    // dashboard ends up offering "clock in" to somebody already clocked in.
    [SkippableFact]
    public async Task TheClockMatchesTheAttendanceService()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await Resolve<IAttendanceService>(provider)
            .ClockInAsync(new ClockInRequest())).Succeeded);

        MyDashboardDto board = await MineAsync(provider);
        AttendanceDayDto? day = await Resolve<IAttendanceService>(provider).MineAsync(Today);

        Assert.Equal(ClockState.ClockedIn, board.Today.Clock);
        Assert.Equal(day!.State, board.Today.Clock);
        Assert.Equal(day.ClockInAt, board.Today.ClockedInAt);
        Assert.Equal(day.WorkedHours, board.Today.WorkedHours);
    }

    // ── on leave today ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task BeingOffTodayIsNamedByItsLeaveType()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await LeaveAsync(_adaId, Today.AddDays(-1), Today.AddDays(2));

        Assert.Equal("Annual Leave", (await MineAsync(provider)).Today.OnLeave);
    }

    // Pending leave is not being on leave. The days are reserved against the
    // balance, but somebody waiting on a decision is at their desk — and a
    // dashboard saying otherwise would have them stay home.
    [SkippableFact]
    public async Task PendingLeaveDoesNotMakeSomebodyOffToday()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await LeaveAsync(_adaId, Today, Today.AddDays(2), LeaveRequestStatus.Pending);

        Assert.Null((await MineAsync(provider)).Today.OnLeave);
    }

    [SkippableFact]
    public async Task LeaveThatEndedYesterdayIsNotToday()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await LeaveAsync(_adaId, Today.AddDays(-5), Today.AddDays(-1));

        Assert.Null((await MineAsync(provider)).Today.OnLeave);
    }

    // ── what is waiting on me ───────────────────────────────────────────────

    [SkippableFact]
    public async Task APendingLeaveRequestIsWaitingOnMe()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await LeaveAsync(_adaId, Today.AddDays(30), Today.AddDays(34),
            LeaveRequestStatus.Pending);

        MyDashboardDto board = await MineAsync(provider);

        MyPendingItemDto item = Assert.Single(board.Pending);

        Assert.Equal("leave-request", item.Kind);
        Assert.Equal("Leave request pending", item.Label);

        // A month away, so there is still time for somebody to decide it.
        Assert.False(item.Urgent);

        // Carried onto the tile so it can show a number without holding the
        // list it does not display.
        Assert.Equal(1, board.Today.PendingActions);
    }

    // Starting before it has been decided is the thing nobody can plan around.
    [SkippableFact]
    public async Task LeaveStartingThisWeekAndStillUndecidedIsUrgent()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await LeaveAsync(_adaId, Today.AddDays(2), Today.AddDays(4),
            LeaveRequestStatus.Pending);

        Assert.True(Assert.Single((await MineAsync(provider)).Pending).Urgent);
    }

    [SkippableFact]
    public async Task ApprovedLeaveIsNotWaitingOnAnybody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await LeaveAsync(_adaId, Today.AddDays(30), Today.AddDays(34));

        Assert.Empty((await MineAsync(provider)).Pending);
    }

    [SkippableFact]
    public async Task UrgentItemsComeFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await LeaveAsync(_adaId, Today.AddDays(40), Today.AddDays(44),
            LeaveRequestStatus.Pending);
        await LeaveAsync(_adaId, Today.AddDays(1), Today.AddDays(2),
            LeaveRequestStatus.Pending);

        IReadOnlyList<MyPendingItemDto> pending = (await MineAsync(provider)).Pending;

        Assert.Equal(2, pending.Count);
        Assert.True(pending[0].Urgent);
        Assert.False(pending[1].Urgent);
    }

    // ── the team ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ATeammateOffTodayIsListedWithTheirReturnDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ben = await HireAsync(provider, "Ben", _managerId);

        await LeaveAsync(ben, Today, Today.AddDays(3));

        TeamLeaveDto away = Assert.Single((await MineAsync(provider)).TeamOnLeave);

        Assert.Equal(ben, away.EmployeeId);
        Assert.Equal("Ben Adeyemi", away.Name);
        Assert.Equal("Annual Leave", away.LeaveType);

        // The day after their last day off, so the card reads "back on" rather
        // than making somebody add one to the end date.
        Assert.Equal(Today.AddDays(4), away.ReturnsOn);
    }

    // Their own leave is in Today.OnLeave. Listing it again under "team on
    // leave" would have somebody reading that they are away from themselves.
    [SkippableFact]
    public async Task MyOwnLeaveIsNotInTheTeamList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await LeaveAsync(_adaId, Today, Today.AddDays(2));

        MyDashboardDto board = await MineAsync(provider);

        Assert.NotNull(board.Today.OnLeave);
        Assert.Empty(board.TeamOnLeave);
    }

    [SkippableFact]
    public async Task SomebodyUnderADifferentManagerIsNotMyTeam()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid otherManager = await HireAsync(provider, "Dele");
        Guid theirReport = await HireAsync(provider, "Emeka", otherManager);

        await LeaveAsync(theirReport, Today, Today.AddDays(2));

        Assert.Empty((await MineAsync(provider)).TeamOnLeave);
    }

    // The manager counts as part of the team: their absence is the one that
    // changes what a report can get signed off today.
    [SkippableFact]
    public async Task MyManagerBeingOffIsOnTheTeamList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await LeaveAsync(_managerId, Today, Today.AddDays(1));

        Assert.Equal(
            _managerId,
            Assert.Single((await MineAsync(provider)).TeamOnLeave).EmployeeId);
    }

    // ── the owner ───────────────────────────────────────────────────────────

    // The account owner permanently has no employee record — they run the
    // company's account rather than working there. An empty day is the right
    // answer for a page they can open, not a failure.
    [SkippableFact]
    public async Task AnAccountWithNoEmployeeRecordGetsAnEmptyDay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ownerUserId;

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            ownerUserId = await seed.Users
                .IgnoreQueryFilters()
                .Where(user => user.TenantId == _tenantId && user.EmployeeId == null)
                .Select(user => user.Id)
                .FirstAsync();
        }

        ActAs(provider, ownerUserId);

        MyDashboardDto board = await MineAsync(provider);

        Assert.Equal(ClockState.NotStarted, board.Today.Clock);
        Assert.Equal(0, board.Today.PendingActions);
        Assert.Empty(board.Pending);
        Assert.Empty(board.TeamOnLeave);
    }
}
