using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Calendar;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Calendar;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Domain.Organisation;
using Motee.Domain.Performance;
using Motee.Domain.Recruitment;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The calendar.
//
// Nine sources, one window, and a visibility rule per source — which is the
// part worth testing, because getting it wrong does not throw. It shows one
// person's maternity leave to the whole office, or hides the week's only
// meeting, and in both cases the screen looks perfectly normal.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class CalendarServiceTests(PostgresFixture fixture)
{
    // A Monday. The window arithmetic and "next seven days" both have a known
    // answer against it, and a calendar test that passes in June and fails over
    // New Year is worse than no test at all.
    private static readonly DateOnly Today = new(2026, 6, 15);

    private static readonly DateOnly WeekEnd = Today.AddDays(6);

    // Held in UTC, because Npgsql refuses to write a timestamptz carrying any
    // other offset. 08:00 UTC is nine in the morning in Lagos, which is where
    // the tenant below is, so "today" is unambiguously the Monday.
    private readonly MovableClock _clock = new(
        new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;

    private Guid _caraId;
    private Guid _bolaId;
    private Guid _deleId;

    private Guid _caraUserId;
    private Guid _bolaUserId;
    private Guid _deleUserId;

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

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        // Cara manages Bola. Dele reports to nobody here, which is what makes
        // him the right person to check that an event does not leak.
        _caraId = await HireAsync(
            provider, "Cara", startDate: new DateOnly(2023, 6, 18),
            dateOfBirth: new DateOnly(1990, 6, 18));

        _bolaId = await HireAsync(
            provider, "Bola", startDate: new DateOnly(2026, 6, 1),
            dateOfBirth: new DateOnly(1992, 1, 5), managerId: _caraId);

        _deleId = await HireAsync(
            provider, "Dele", startDate: new DateOnly(2020, 12, 24),
            dateOfBirth: new DateOnly(1985, 12, 24));

        // Invited, never started. Nothing about him belongs on a calendar yet —
        // least of all a birthday card from people he has not met.
        await HireAsync(
            provider, "Emeka", startDate: new DateOnly(2026, 7, 1),
            dateOfBirth: Today.AddDays(2), status: EmployeeStatus.Pending);

        _caraUserId = await AccountAsync(_caraId, "cara");
        _bolaUserId = await AccountAsync(_bolaId, "bola");
        _deleUserId = await AccountAsync(_deleId, "dele");

        ActAs(provider, _bolaUserId);

        return provider;
    }

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        DateOnly startDate,
        DateOnly? dateOfBirth = null,
        Guid? managerId = null,
        EmployeeStatus status = EmployeeStatus.Active) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = status,
            StartDate = startDate,
            DateOfBirth = dateOfBirth,
            ManagerId = managerId,
        })).Employee!.Id;

    private async Task<Guid> AccountAsync(Guid employeeId, string handle)
    {
        Guid userId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = userId,
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = char.ToUpperInvariant(handle[0]) + handle[1..],
            LastName = "Okafor",
            Email = $"{handle}-login@acme.com",
            NormalizedEmail = $"{handle.ToUpperInvariant()}-LOGIN@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        return userId;
    }

    private static void ActAs(ServiceProvider provider, Guid userId) =>
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    // Resolved per call on purpose: each HTTP request gets its own scope in
    // production, and a service held across writes reads its own stale tracker.
    private static Task<IReadOnlyList<CalendarItemDto>> AgendaAsync(
        ServiceProvider provider,
        params Guid[] employeeIds) =>
        Resolve<ICalendarService>(provider).AgendaAsync(new CalendarQuery
        {
            From = Today,
            To = WeekEnd,
            EmployeeIds = employeeIds,
        });

    private static CalendarItemDto? OfKind(
        IReadOnlyList<CalendarItemDto> items,
        CalendarCategory category) =>
        items.FirstOrDefault(item => item.Category == category);

    // ── stored events ───────────────────────────────────────────────────────

    private static Task<CalendarEventResult> CreateAsync(
        ServiceProvider provider,
        CalendarEventRequest request,
        bool canActForAnyone = false) =>
        Resolve<ICalendarService>(provider).CreateAsync(request, canActForAnyone);

    private static CalendarEventRequest CompanyEvent(string title = "All-hands") => new()
    {
        Title = title,
        Date = Today.AddDays(2),
        AllDay = false,
        StartTime = new TimeOnly(14, 0),
        EndTime = new TimeOnly(15, 0),
        Category = CalendarCategory.Meeting,
        Audience = CalendarAudience.Company,
    };

    private static CalendarEventRequest PeopleEvent(params Guid[] employeeIds) => new()
    {
        Title = "Standup",
        Date = Today.AddDays(1),
        AllDay = true,
        Category = CalendarCategory.Meeting,
        Audience = CalendarAudience.People,
        EmployeeIds = employeeIds,
    };

    [SkippableFact]
    public async Task ACompanyEventIsOnEverybodysCalendar()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ActAs(provider, _caraUserId);
        CalendarEventResult created = await CreateAsync(
            provider, CompanyEvent(), canActForAnyone: true);

        Assert.True(created.Succeeded, created.Outcome.ToString());

        // Dele is in no department with her and reports to nobody.
        ActAs(provider, _deleUserId);
        CalendarItemDto item = Assert.Single(
            await AgendaAsync(provider), candidate => candidate.Title == "All-hands");

        Assert.Equal(CalendarLayer.Company, item.Layer);
        Assert.Equal(new TimeOnly(14, 0), item.StartTime);
        Assert.False(item.AllDay);
        Assert.Null(item.PersonId);
    }

    [SkippableFact]
    public async Task AnEventOnSomebodysScheduleIsNotOnAnyoneElses()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await CreateAsync(provider, PeopleEvent(_bolaId))).Succeeded);

        ActAs(provider, _deleUserId);

        Assert.DoesNotContain(
            await AgendaAsync(provider), item => item.Title == "Standup");
    }

    [SkippableFact]
    public async Task AnEventOnSomebodysScheduleSitsOnTheScheduleLayer()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await CreateAsync(provider, PeopleEvent(_bolaId))).Succeeded);

        CalendarItemDto item = Assert.Single(
            await AgendaAsync(provider), candidate => candidate.Title == "Standup");

        Assert.Equal(CalendarLayer.Schedule, item.Layer);
        Assert.Equal(_bolaId, item.PersonId);
        Assert.Equal("Bola Okafor", item.PersonName);
        Assert.True(item.CanDelete);
    }

    // Filling a colleague's week is not a thing a colleague gets to do.
    [SkippableFact]
    public async Task AnEventCannotBePutOnTheCalendarOfSomebodyWhoIsNotYours()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        CalendarEventResult result = await CreateAsync(provider, PeopleEvent(_deleId));

        Assert.Equal(CalendarEventOutcome.NotYoursToAddTo, result.Outcome);
    }

    [SkippableFact]
    public async Task AManagerPutsEventsOnTheirOwnReportsCalendars()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ActAs(provider, _caraUserId);

        Assert.True((await CreateAsync(provider, PeopleEvent(_caraId, _bolaId))).Succeeded);
    }

    // Without this, HR puts a one-to-one on two people's calendars and then
    // cannot find it to move it.
    [SkippableFact]
    public async Task WhoeverAddedAnEventStillSeesItOnTheirOwnCalendar()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ActAs(provider, _caraUserId);
        Assert.True(
            (await CreateAsync(provider, PeopleEvent(_bolaId), canActForAnyone: true)).Succeeded);

        Assert.Contains(await AgendaAsync(provider), item => item.Title == "Standup");
    }

    [SkippableFact]
    public async Task HoldingTheModuleMeansAnybodysCalendarCanBeAddedTo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        CalendarEventResult result = await CreateAsync(
            provider, PeopleEvent(_deleId), canActForAnyone: true);

        Assert.True(result.Succeeded, result.Outcome.ToString());
    }

    [SkippableFact]
    public async Task ACompanyEventSentWithAGuestListIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        CalendarEventResult result = await CreateAsync(
            provider,
            CompanyEvent() with { EmployeeIds = [_bolaId] },
            canActForAnyone: true);

        Assert.Equal(CalendarEventOutcome.Refused, result.Outcome);
        Assert.Equal(CalendarRefusal.CompanyEventTakesNoAttendees, result.Refusal);
    }

    [SkippableFact]
    public async Task AnAttendeeWhoDoesNotWorkHereIsReported()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        CalendarEventResult result = await CreateAsync(
            provider, PeopleEvent(Guid.NewGuid()), canActForAnyone: true);

        Assert.Equal(CalendarEventOutcome.EmployeeNotFound, result.Outcome);
    }

    [SkippableFact]
    public async Task ADraftTheDomainRefusesComesBackWithTheReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        CalendarEventResult result = await CreateAsync(
            provider, PeopleEvent(_bolaId) with { Title = "  " });

        Assert.Equal(CalendarEventOutcome.Refused, result.Outcome);
        Assert.Equal(CalendarRefusal.TitleRequired, result.Refusal);
    }

    [SkippableFact]
    public async Task AnEventIsEditedByWhoeverAddedIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await CreateAsync(provider, PeopleEvent(_bolaId))).Event!.Id;

        CalendarEventResult updated = await Resolve<ICalendarService>(provider)
            .UpdateAsync(id, PeopleEvent(_bolaId) with { Title = "Sprint review" });

        Assert.True(updated.Succeeded, updated.Outcome.ToString());
        Assert.Equal("Sprint review", updated.Event!.Title);
    }

    [SkippableFact]
    public async Task AnEventIsRemovedByWhoeverAddedIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await CreateAsync(provider, PeopleEvent(_bolaId))).Event!.Id;

        Assert.Equal(
            CalendarEventOutcome.Succeeded,
            await Resolve<ICalendarService>(provider).DeleteAsync(id));

        Assert.DoesNotContain(await AgendaAsync(provider), item => item.Title == "Standup");
    }

    [SkippableFact]
    public async Task SomebodyElsesEventIsNotYoursToRemove()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ActAs(provider, _caraUserId);
        Guid id = (await CreateAsync(
            provider, PeopleEvent(_caraId, _bolaId), canActForAnyone: true)).Event!.Id;

        ActAs(provider, _bolaUserId);

        Assert.Equal(
            CalendarEventOutcome.NotYoursToChange,
            await Resolve<ICalendarService>(provider).DeleteAsync(id));
    }

    [SkippableFact]
    public async Task AnEventEntirelyOutsideTheWindowIsNotInTheAgenda()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await CreateAsync(
            provider,
            PeopleEvent(_bolaId) with { Date = WeekEnd.AddDays(3) })).Succeeded);

        Assert.DoesNotContain(await AgendaAsync(provider), item => item.Title == "Standup");
    }

    // A fortnight that starts before the window and ends inside it is in the
    // window. Filtering on the start date alone is how a week view loses the
    // leave somebody is already on.
    [SkippableFact]
    public async Task AnEventStraddlingTheStartOfTheWindowIsInTheAgenda()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await CreateAsync(
            provider,
            PeopleEvent(_bolaId) with
            {
                Date = Today.AddDays(-5),
                EndDate = Today.AddDays(1),
            })).Succeeded);

        Assert.Contains(await AgendaAsync(provider), item => item.Title == "Standup");
    }

    // ── public holidays ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task APublicHolidayIsOnEverybodysCalendar()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await SeedAsync(new PublicHoliday
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            Date = Today.AddDays(3),
            Name = "Democracy Day",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        CalendarItemDto item = Assert.Single(
            await AgendaAsync(provider), candidate => candidate.Title == "Democracy Day");

        Assert.Equal(CalendarLayer.Holidays, item.Layer);
        Assert.Equal(CalendarCategory.Holiday, item.Category);
        Assert.True(item.AllDay);
        Assert.False(item.CanDelete);
    }

    // ── leave ───────────────────────────────────────────────────────────────

    private async Task<Guid> LeaveTypeAsync(string code, string name)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        LeaveType? existing = await seed.LeaveTypes.FirstOrDefaultAsync(
            type => type.Code == code);

        if (existing is not null)
        {
            return existing.Id;
        }

        LeaveType created = new()
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            Code = code,
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        seed.LeaveTypes.Add(created);
        await seed.SaveChangesAsync();

        return created.Id;
    }

    // Written straight in rather than driven through submit-and-approve: the
    // approval chain has its own tests, and what the calendar needs is a row in
    // a given state.
    private async Task BookedOffAsync(
        Guid employeeId,
        Guid leaveTypeId,
        DateOnly from,
        DateOnly to,
        LeaveRequestStatus status = LeaveRequestStatus.Approved,
        Guid? reliefEmployeeId = null) =>
        await SeedAsync(new LeaveRequest
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            LeaveTypeId = leaveTypeId,
            StartDate = from,
            EndDate = to,
            TotalDays = to.DayNumber - from.DayNumber + 1,
            LeaveYearStart = new DateOnly(from.Year, 1, 1),
            Status = status,
            Reason = "Booked months ago",
            ReliefEmployeeId = reliefEmployeeId,
            SubmittedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    [SkippableFact]
    public async Task ApprovedLeaveIsOnThePersonsSchedule()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid annual = await LeaveTypeAsync("annual", "Annual Leave");
        await BookedOffAsync(_bolaId, annual, Today.AddDays(1), Today.AddDays(3));

        CalendarItemDto leave = Assert.Single(
            await AgendaAsync(provider),
            item => item.Category == CalendarCategory.Leave);

        Assert.Equal("Annual Leave", leave.Title);
        Assert.Equal(Today.AddDays(1), leave.Date);
        Assert.Equal(Today.AddDays(3), leave.EndDate);
        Assert.Equal(_bolaId, leave.PersonId);
        Assert.False(leave.Pending);
        Assert.True(leave.AllDay);
    }

    [SkippableFact]
    public async Task LeaveNobodyHasApprovedYetIsMarkedPending()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid annual = await LeaveTypeAsync("annual", "Annual Leave");
        await BookedOffAsync(
            _bolaId, annual, Today.AddDays(1), Today.AddDays(3), LeaveRequestStatus.Pending);

        CalendarItemDto leave = Assert.Single(
            await AgendaAsync(provider),
            item => item.Category == CalendarCategory.LeaveRequest);

        Assert.True(leave.Pending);
    }

    [SkippableTheory]
    [InlineData(LeaveRequestStatus.Rejected)]
    [InlineData(LeaveRequestStatus.Cancelled)]
    public async Task LeaveThatIsNotHappeningIsNotOnTheCalendar(LeaveRequestStatus status)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid annual = await LeaveTypeAsync("annual", "Annual Leave");
        await BookedOffAsync(_bolaId, annual, Today.AddDays(1), Today.AddDays(3), status);

        Assert.DoesNotContain(
            await AgendaAsync(provider),
            item => item.Category is CalendarCategory.Leave or CalendarCategory.LeaveRequest);
    }

    // The privacy rule. Maternity leave on a shared calendar announces a
    // pregnancy, and religious leave announces a religion — neither of which is
    // the calendar's to tell. Your own says what it is; everybody else's says
    // you are away, which is the part colleagues need.
    [SkippableFact]
    public async Task SomebodyElsesLeaveSaysTheyAreAwayAndNotWhy()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid maternity = await LeaveTypeAsync("maternity", "Maternity Leave");
        await BookedOffAsync(_caraId, maternity, Today.AddDays(1), Today.AddDays(3));

        CalendarItemDto leave = Assert.Single(
            await AgendaAsync(provider, _caraId),
            item => item.Category == CalendarCategory.Leave);

        Assert.Equal("Cara Okafor — on leave", leave.Title);
        Assert.DoesNotContain("Maternity", leave.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Null(leave.Description);
    }

    [SkippableFact]
    public async Task YourOwnLeaveSaysWhichKindItIs()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid maternity = await LeaveTypeAsync("maternity", "Maternity Leave");
        await BookedOffAsync(_bolaId, maternity, Today.AddDays(1), Today.AddDays(3));

        CalendarItemDto leave = Assert.Single(
            await AgendaAsync(provider),
            item => item.Category == CalendarCategory.Leave);

        Assert.Equal("Maternity Leave", leave.Title);
    }

    [SkippableFact]
    public async Task StandingInForSomebodyShowsOnTheStandInsSchedule()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid annual = await LeaveTypeAsync("annual", "Annual Leave");
        await BookedOffAsync(
            _caraId, annual, Today.AddDays(1), Today.AddDays(3), reliefEmployeeId: _bolaId);

        CalendarItemDto cover = Assert.Single(
            await AgendaAsync(provider),
            item => item.Category == CalendarCategory.Cover);

        Assert.Equal("Covering Cara Okafor", cover.Title);
        Assert.Equal(_bolaId, cover.PersonId);
    }

    // ── celebrations ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ABirthdayInTheWindowIsOnTheCalendarWithoutTheYear()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        CalendarItemDto birthday = Assert.Single(
            await AgendaAsync(provider),
            item => item.Category == CalendarCategory.Birthday);

        Assert.Equal("Cara Okafor's birthday", birthday.Title);
        Assert.Equal(new DateOnly(2026, 6, 18), birthday.Date);
        Assert.Equal(CalendarLayer.Company, birthday.Layer);
        Assert.DoesNotContain("1990", birthday.Title, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task SomebodyWhoHasNotStartedYetHasNoBirthdayOnTheCalendar()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.DoesNotContain(
            await AgendaAsync(provider),
            item => item.Title.StartsWith("Emeka", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task AWorkAnniversaryCountsTheYears()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        CalendarItemDto anniversary = Assert.Single(
            await AgendaAsync(provider),
            item => item.Category == CalendarCategory.Anniversary);

        // Cara started 18 June 2023, so this is her third.
        Assert.Equal("Cara Okafor — 3 years", anniversary.Title);
        Assert.Equal(new DateOnly(2026, 6, 18), anniversary.Date);
    }

    // Bola started on 1 June 2026. His first day is not an anniversary of
    // anything, and congratulating him on nought years is worse than silence.
    [SkippableFact]
    public async Task AFirstDayIsNotAnAnniversary()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<CalendarItemDto> items = await Resolve<ICalendarService>(provider)
            .AgendaAsync(new CalendarQuery
            {
                From = new DateOnly(2026, 5, 25),
                To = new DateOnly(2026, 6, 10),
            });

        Assert.DoesNotContain(
            items,
            item => item.Category == CalendarCategory.Anniversary
                && item.Title.StartsWith("Bola", StringComparison.Ordinal));
    }

    // ── interviews ──────────────────────────────────────────────────────────

    // A wall-clock time in the company's own city, as the instant Postgres will
    // take. Written this way round because that is how the assertions read:
    // the interview is at ten, and the calendar has to say ten.
    private static DateTimeOffset LagosAt(DateOnly day, TimeOnly time) =>
        new DateTimeOffset(day.ToDateTime(time), TimeSpan.FromHours(1)).ToUniversalTime();

    private async Task<Guid> InterviewAsync(
        DateTimeOffset at,
        IEnumerable<Guid> panel,
        InterviewStatus status = InterviewStatus.Scheduled)
    {
        Guid requisitionId = Guid.NewGuid();
        Guid candidateId = Guid.NewGuid();
        Guid interviewId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.JobRequisitions.Add(new JobRequisition
        {
            Id = requisitionId,
            TenantId = _tenantId,
            PositionTitle = "Backend engineer",
            DepartmentId = _departmentId,
            Status = RequisitionStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        seed.Candidates.Add(new Candidate
        {
            Id = candidateId,
            TenantId = _tenantId,
            RequisitionId = requisitionId,
            FirstName = "Ngozi",
            LastName = "Eze",
            Email = "ngozi@example.com",
            Stage = CandidateStage.Interview,
            Status = CandidateStatus.Active,
            AppliedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        seed.Interviews.Add(new Interview
        {
            Id = interviewId,
            TenantId = _tenantId,
            CandidateId = candidateId,
            RequisitionId = requisitionId,
            Round = "First round",
            ScheduledAt = at,
            DurationMinutes = 45,
            Mode = InterviewMode.Video,
            Panel = [.. panel],
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        return interviewId;
    }

    [SkippableFact]
    public async Task AnInterviewIsOnThePanelMembersCalendar()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        // 10:00 Lagos on the Wednesday.
        await InterviewAsync(
            LagosAt(Today.AddDays(2), new TimeOnly(10, 0)),
            [_bolaId]);

        CalendarItemDto interview = Assert.Single(
            await AgendaAsync(provider),
            item => item.Category == CalendarCategory.Interview);

        Assert.Equal(Today.AddDays(2), interview.Date);
        Assert.Equal(new TimeOnly(10, 0), interview.StartTime);
        Assert.Equal(new TimeOnly(10, 45), interview.EndTime);
        Assert.False(interview.AllDay);
        Assert.Contains("Ngozi Eze", interview.Title, StringComparison.Ordinal);
    }

    // An internal candidate's name on a calendar a colleague can open is how
    // somebody's manager learns they are applying elsewhere in the company.
    [SkippableFact]
    public async Task AnInterviewNamesTheCandidateOnlyToThePanel()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await InterviewAsync(
            LagosAt(Today.AddDays(2), new TimeOnly(10, 0)),
            [_caraId]);

        CalendarItemDto interview = Assert.Single(
            await AgendaAsync(provider, _caraId),
            item => item.Category == CalendarCategory.Interview);

        Assert.DoesNotContain("Ngozi", interview.Title, StringComparison.Ordinal);
        Assert.Equal("Interview — First round", interview.Title);
    }

    [SkippableFact]
    public async Task ACancelledInterviewIsNotInTheDiary()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await InterviewAsync(
            LagosAt(Today.AddDays(2), new TimeOnly(10, 0)),
            [_bolaId],
            InterviewStatus.Cancelled);

        Assert.DoesNotContain(
            await AgendaAsync(provider), item => item.Category == CalendarCategory.Interview);
    }

    // ── review due dates ────────────────────────────────────────────────────

    private Task ReviewAsync(
        Guid employeeId,
        Guid? reviewerId,
        DateOnly due,
        ReviewStatus status = ReviewStatus.InProgress) =>
        SeedAsync(new PerformanceReview
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            ReviewerId = reviewerId,
            ReviewType = ReviewType.Annual,
            Period = "H1 2026",
            Status = status,
            DueDate = due,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    [SkippableFact]
    public async Task AReviewDueDateIsOnTheCalendarOfWhoeverItIsAbout()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReviewAsync(_bolaId, _caraId, Today.AddDays(4));

        CalendarItemDto review = Assert.Single(
            await AgendaAsync(provider),
            item => item.Category == CalendarCategory.Review);

        Assert.Equal("Performance review due — H1 2026", review.Title);
        Assert.Equal(Today.AddDays(4), review.Date);
        Assert.True(review.AllDay);
    }

    [SkippableFact]
    public async Task AReviewDueDateIsAlsoOnTheReviewersCalendar()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReviewAsync(_bolaId, _caraId, Today.AddDays(4));

        ActAs(provider, _caraUserId);

        CalendarItemDto review = Assert.Single(
            await AgendaAsync(provider),
            item => item.Category == CalendarCategory.Review);

        Assert.Equal("Bola Okafor's review due — H1 2026", review.Title);
        Assert.Equal(_caraId, review.PersonId);
    }

    [SkippableFact]
    public async Task AFinishedReviewHasNoDueDateLeft()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReviewAsync(_bolaId, _caraId, Today.AddDays(4), ReviewStatus.Completed);

        Assert.DoesNotContain(
            await AgendaAsync(provider), item => item.Category == CalendarCategory.Review);
    }

    // ── the next seven days ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task UpcomingStartsTodayAndStopsAtTheEndOfTheWindow()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await CreateAsync(
            provider, PeopleEvent(_bolaId) with { Title = "Yesterday", Date = Today.AddDays(-1) }))
            .Succeeded);

        Assert.True((await CreateAsync(
            provider, PeopleEvent(_bolaId) with { Title = "Inside", Date = Today.AddDays(5) }))
            .Succeeded);

        Assert.True((await CreateAsync(
            provider, PeopleEvent(_bolaId) with { Title = "Later", Date = Today.AddDays(20) }))
            .Succeeded);

        IReadOnlyList<CalendarItemDto> upcoming =
            await Resolve<ICalendarService>(provider).UpcomingAsync();

        Assert.Contains(upcoming, item => item.Title == "Inside");
        Assert.DoesNotContain(upcoming, item => item.Title == "Yesterday");
        Assert.DoesNotContain(upcoming, item => item.Title == "Later");
    }

    [SkippableFact]
    public async Task TheAgendaComesBackInDateOrder()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<CalendarItemDto> items = await AgendaAsync(provider);

        Assert.Equal([.. items.Select(item => item.Date).Order()], [.. items.Select(item => item.Date)]);
    }

    private async Task SeedAsync<T>(T row) where T : class
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Add(row);

        await seed.SaveChangesAsync();
    }
}
