using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Attendance;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Attendance;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// HR notes and the shift roster, out of the database.
//
// The two things worth proving. A note's audience actually withholds it — the
// screen renders that field as a pill and filters on nothing. And an unrostered
// day comes back unrostered, where the client invents a default shift "so the
// schedule always reads as populated", which makes a manager read coverage that
// does not exist.
//
// Written before the services.
[Collection(PostgresCollection.Name)]
public class NotesAndShiftsServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero));

    // A Tuesday, so the default Monday-to-Friday pattern runs.
    private static readonly DateOnly Today = new(2026, 9, 22);

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _adaId;
    private Guid _boluId;
    private Guid _scheduleId;

    // ── notes ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AddsANote()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        NoteResult result = await Notes(provider).AddAsync(_adaId, Writing());

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
        Assert.Equal(NoteAudience.HrOnly, result.Note!.Audience);

        // Taken from the request, not the payload.
        Assert.Equal("Owner Okafor", result.Note.AuthorName);
    }

    // The pill the screen renders and nothing filters on.
    [SkippableFact]
    public async Task AnHrOnlyNoteIsWithheldFromAManager()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Notes(provider).AddAsync(_adaId, Writing());
        await Notes(provider).AddAsync(
            _adaId, Writing("Agreed a later start on Mondays.", NoteAudience.ManagerAndHr));

        Assert.Equal(2, (await Notes(provider).ForAsync(_adaId, includePrivate: true)).Count);

        IReadOnlyList<EmployeeNoteDto> managerSees =
            await Notes(provider).ForAsync(_adaId, includePrivate: false);

        Assert.Single(managerSees);
        Assert.Equal(NoteAudience.ManagerAndHr, managerSees[0].Audience);
    }

    // A note written without thinking about the setting is the one most likely
    // to need protecting.
    [SkippableFact]
    public async Task ANoteIsPrivateUnlessSomebodySaysOtherwise()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Notes(provider).AddAsync(
            _adaId, new EmployeeNoteRequest { Body = "Something frank." });

        Assert.Empty(await Notes(provider).ForAsync(_adaId, includePrivate: false));
    }

    [SkippableFact]
    public async Task RefusesAnEmptyNote()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            NoteRefusal.BodyRequired,
            (await Notes(provider).AddAsync(_adaId, Writing("   "))).Refusal);
    }

    [SkippableFact]
    public async Task RefusesAReminderWithNoDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            NoteRefusal.ReminderNeedsADate,
            (await Notes(provider).AddAsync(
                _adaId, Writing() with { Kind = NoteKind.Reminder })).Refusal);
    }

    // The screen renders the pin icon without sorting by it, so a pinned note
    // sinks down the list as others are added.
    [SkippableFact]
    public async Task APinnedNoteComesFirstHoweverOldItIs()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid oldId = (await Notes(provider).AddAsync(_adaId, Writing("Older."))).Note!.Id;

        _clock.Advance(TimeSpan.FromDays(30));

        await Notes(provider).AddAsync(_adaId, Writing("Newer."));

        await Notes(provider).PinAsync(_adaId, oldId, pinned: true);

        IReadOnlyList<EmployeeNoteDto> ordered =
            await Notes(provider).ForAsync(_adaId, includePrivate: true);

        Assert.Equal(oldId, ordered[0].Id);
    }

    // Otherwise pushing a reminder out would leave it already raised and it
    // would never fire again.
    [SkippableFact]
    public async Task MovingAReminderReArmsIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Notes(provider).AddAsync(
            _adaId,
            Writing() with { Kind = NoteKind.Reminder, RemindAt = Today })).Note!.Id;

        await using (MoteeDbContext context = fixture.CreateContext(_tenantId))
        {
            await context.EmployeeNotes
                .Where(note => note.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    note => note.RemindedAt, _clock.GetUtcNow()));
        }

        await Notes(provider).UpdateAsync(
            _adaId,
            id,
            Writing() with { Kind = NoteKind.Reminder, RemindAt = Today.AddDays(30) });

        await using MoteeDbContext check = fixture.CreateContext(_tenantId);

        Assert.Null((await check.EmployeeNotes.SingleAsync(note => note.Id == id)).RemindedAt);
    }

    [SkippableFact]
    public async Task MyRemindersAreTheOnesIHaveSetAndThatAreDue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Notes(provider).AddAsync(
            _adaId, Writing() with { Kind = NoteKind.Reminder, RemindAt = Today });
        await Notes(provider).AddAsync(
            _boluId,
            Writing() with { Kind = NoteKind.Reminder, RemindAt = Today.AddDays(60) });

        IReadOnlyList<EmployeeNoteDto> mine = await Notes(provider).MyRemindersAsync();

        Assert.Single(mine);
        Assert.Equal(_adaId, mine[0].EmployeeId);
        Assert.True(mine[0].ReminderIsDue);
    }

    // These are the notes somebody writes before a difficult conversation.
    // Reproducing one into the audit trail would put it in a table this module
    // does not gate.
    [SkippableFact]
    public async Task TheAuditTrailDoesNotReproduceWhatANoteSaid()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Notes(provider).AddAsync(_adaId, Writing("Something nobody else should read."));

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        // Projected as two columns and joined here. Changes is jsonb, so
        // coalescing it to an empty string in SQL asks Postgres to read "" as
        // json and it refuses.
        var entries = await context.AuditEntries
            .Where(entry => entry.EntityType == nameof(EmployeeNote))
            .Select(entry => new { entry.Description, entry.Changes })
            .ToListAsync();

        Assert.NotEmpty(entries);
        Assert.All(
            entries,
            entry => Assert.DoesNotContain(
                "nobody else should read",
                $"{entry.Description} {entry.Changes}",
                StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RemovesANote()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Notes(provider).AddAsync(_adaId, Writing())).Note!.Id;

        Assert.Equal(NoteOutcome.Succeeded, await Notes(provider).RemoveAsync(_adaId, id));
        Assert.Empty(await Notes(provider).ForAsync(_adaId, includePrivate: true));
    }

    // ── shifts ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RostersSomebodyOntoAShift()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        ShiftResult result = await Shifts(provider).AssignAsync(Rostering());

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
        Assert.Equal("Day shift", result.Assignment!.ShiftName);
        Assert.Equal(ShiftState.Scheduled, result.Assignment.State);
    }

    // The defect: an empty square is an empty square.
    [SkippableFact]
    public async Task AnUnrosteredDayComesBackUnrostered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        RosterResult roster = await Shifts(provider).RosterAsync(new RosterQuery
        {
            From = Today,
            To = Today.AddDays(4),
        });

        Assert.True(roster.Succeeded, $"{roster.Refusal}");

        RosterRow row = roster.Rows.Single(candidate => candidate.EmployeeId == _adaId);

        Assert.All(row.Days, day => Assert.False(day.IsRostered));
        Assert.All(row.Days, day => Assert.Null(day.State));
        Assert.Equal(5, row.UnrosteredDays);
    }

    [SkippableFact]
    public async Task ARosteredDayCarriesTheShiftsHours()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Shifts(provider).AssignAsync(Rostering());

        RosterRow row = (await Shifts(provider).RosterAsync(new RosterQuery
        {
            From = Today,
            To = Today,
            EmployeeId = _adaId,
        })).Rows.Single();

        RosterDay day = Assert.Single(row.Days);

        Assert.True(day.IsRostered);
        Assert.Equal(new TimeOnly(9, 0), day.StartTime);
        Assert.Equal(new TimeOnly(17, 0), day.EndTime);
        Assert.Equal(7m, day.PaidHours);
    }

    // The figure a manager checking cover actually needs.
    [SkippableFact]
    public async Task TheGapsAcrossTheGridAreCounted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Shifts(provider).AssignAsync(Rostering());

        RosterResult roster = await Shifts(provider).RosterAsync(new RosterQuery
        {
            From = Today,
            To = Today.AddDays(1),
        });

        // Three people, two days, one shift rostered.
        Assert.Equal(5, roster.UnrosteredDays);
    }

    [SkippableFact]
    public async Task TheRosterCanBeNarrowedToWhoHasGaps()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Shifts(provider).AssignAsync(Rostering());

        RosterResult roster = await Shifts(provider).RosterAsync(new RosterQuery
        {
            From = Today,
            To = Today,
            WithGaps = true,
        });

        Assert.DoesNotContain(roster.Rows, row => row.EmployeeId == _adaId);
    }

    // Rostering onto a pattern that does not run that day is a shift nobody can
    // work. 26 September 2026 is a Saturday.
    [SkippableFact]
    public async Task RefusesADayThePatternDoesNotRun()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ShiftRefusal.NotAWorkingDay,
            (await Shifts(provider).AssignAsync(
                Rostering() with { Date = new DateOnly(2026, 9, 26) })).Refusal);
    }

    [SkippableFact]
    public async Task RefusesToRosterIntoThePast()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ShiftRefusal.DateInThePast,
            (await Shifts(provider).AssignAsync(
                Rostering() with { Date = Today.AddDays(-7) })).Refusal);
    }

    // What dragging a different shift onto a filled cell means.
    [SkippableFact]
    public async Task RosteringADayTwiceChangesThePattern()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Shifts(provider).AssignAsync(Rostering());

        Guid lateId = await ScheduleAsync(provider, "Late shift", new TimeOnly(12, 0));

        ShiftResult again = await Shifts(provider).AssignAsync(
            Rostering() with { WorkScheduleId = lateId });

        Assert.True(again.Succeeded, $"{again.Outcome} / {again.Refusal}");
        Assert.Equal("Late shift", again.Assignment!.ShiftName);

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        Assert.Equal(1, await context.ShiftAssignments.CountAsync());
    }

    // Cancelled says somebody was meant to be there and is not; cleared says
    // nobody was ever put down for it.
    [SkippableFact]
    public async Task ACancelledShiftIsStillOnTheRota()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Shifts(provider).AssignAsync(Rostering())).Assignment!.Id;

        ShiftResult cancelled = await Shifts(provider).CancelAsync(id);

        Assert.True(cancelled.Succeeded, $"{cancelled.Outcome} / {cancelled.Refusal}");
        Assert.Equal(ShiftState.Cancelled, cancelled.Assignment!.State);

        RosterRow row = (await Shifts(provider).RosterAsync(new RosterQuery
        {
            From = Today,
            To = Today,
            EmployeeId = _adaId,
        })).Rows.Single();

        Assert.True(row.Days[0].IsRostered);
        Assert.Equal(0, row.UnrosteredDays);
        Assert.Equal(0m, row.RosteredHours);
    }

    [SkippableFact]
    public async Task ClearingAShiftLeavesTheDayUnrostered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Shifts(provider).AssignAsync(Rostering())).Assignment!.Id;

        Assert.Equal(ShiftOutcome.Succeeded, await Shifts(provider).ClearAsync(id));

        RosterRow row = (await Shifts(provider).RosterAsync(new RosterQuery
        {
            From = Today,
            To = Today,
            EmployeeId = _adaId,
        })).Rows.Single();

        Assert.False(row.Days[0].IsRostered);
    }

    // Putting somebody back on a shift that was called off is the ordinary way
    // a cancellation is reversed.
    [SkippableFact]
    public async Task RosteringACancelledDayAgainUncancelsIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Shifts(provider).AssignAsync(Rostering())).Assignment!.Id;

        await Shifts(provider).CancelAsync(id);

        ShiftResult again = await Shifts(provider).AssignAsync(Rostering());

        Assert.Equal(ShiftState.Scheduled, again.Assignment!.State);
        Assert.Null(again.Assignment.CancelledOn);
    }

    // Whether somebody worked is the attendance module's fact, and the one
    // thing separating a shift that was worked from one that was missed.
    [SkippableFact]
    public async Task APastShiftNobodyWorkedReadsAsMissed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Shifts(provider).AssignAsync(Rostering());

        _clock.Advance(TimeSpan.FromDays(2));

        RosterRow row = (await Shifts(provider).RosterAsync(new RosterQuery
        {
            From = Today,
            To = Today,
            EmployeeId = _adaId,
        })).Rows.Single();

        Assert.Equal(ShiftState.Missed, row.Days[0].State);
    }

    [SkippableFact]
    public async Task APastShiftSomebodyClockedIntoReadsAsWorked()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Shifts(provider).AssignAsync(Rostering());
        await ClockedInAsync(_adaId, Today);

        _clock.Advance(TimeSpan.FromDays(2));

        RosterRow row = (await Shifts(provider).RosterAsync(new RosterQuery
        {
            From = Today,
            To = Today,
            EmployeeId = _adaId,
        })).Rows.Single();

        Assert.Equal(ShiftState.Worked, row.Days[0].State);
    }

    // One call must not try to render a year of grid for every employee.
    [SkippableFact]
    public async Task RefusesARangeBeyondAQuarter()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        RosterResult roster = await Shifts(provider).RosterAsync(new RosterQuery
        {
            From = Today,
            To = Today.AddDays(Shifts_MaximumDays),
        });

        Assert.Equal(ShiftRefusal.RangeTooWide, roster.Refusal);
    }

    // ── tenancy ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanysNotesAreNotAnothers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Notes(provider).AddAsync(_adaId, Writing());

        RegisterTenantResult other = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Other",
            LastName = "Owner",
            Email = "owner@other.com",
            CompanyName = "Other Limited",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = other.TenantId;

        Assert.Empty(await Notes(provider).ForAsync(_adaId, includePrivate: true));
    }

    // ── arrange ────────────────────────────────────────────────────────────

    private const int Shifts_MaximumDays = Domain.Attendance.Shifts.MaximumRosterDays;

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
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            registration.UserId.ToString();

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _adaId = await HireAsync(provider, "Ada");
        _boluId = await HireAsync(provider, "Bolu");
        await HireAsync(provider, "Chidi");

        _scheduleId = await ScheduleAsync(provider, "Day shift", new TimeOnly(9, 0));

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IEmployeeNoteService Notes(ServiceProvider provider) =>
        Resolve<IEmployeeNoteService>(provider);

    private static IShiftService Shifts(ServiceProvider provider) =>
        Resolve<IShiftService>(provider);

    private static EmployeeNoteRequest Writing(
        string body = "Third late start this month — worth asking if something is going on.",
        NoteAudience audience = NoteAudience.HrOnly) => new()
        {
            Body = body,
            Audience = audience,
        };

    private ShiftAssignmentRequest Rostering() => new()
    {
        EmployeeId = _adaId,
        WorkScheduleId = _scheduleId,
        Date = Today,
    };

    private static async Task<Guid> ScheduleAsync(
        ServiceProvider provider,
        string name,
        TimeOnly start)
    {
        // The request takes day names as strings, not the domain's DayOfWeek
        // dictionary — so this builds Monday to Friday in the shape the service
        // actually accepts rather than the one the domain uses internally.
        Dictionary<string, WorkingHoursDto> days = new(StringComparer.Ordinal);

        foreach (DayOfWeek day in (DayOfWeek[])
        [
            DayOfWeek.Monday,
            DayOfWeek.Tuesday,
            DayOfWeek.Wednesday,
            DayOfWeek.Thursday,
            DayOfWeek.Friday,
        ])
        {
            days[day.ToString()] = new WorkingHoursDto
            {
                Start = start,
                End = start.AddHours(8),
            };
        }

        WorkScheduleResult result = await Resolve<IWorkScheduleService>(provider).CreateAsync(
            new WorkScheduleRequest
            {
                Name = name,
                Days = days,
                BreakMinutes = 60,
            });

        Assert.True(result.Succeeded, $"{result.Outcome}");

        return result.Schedule!.Id;
    }

    private async Task ClockedInAsync(Guid employeeId, DateOnly date)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.AttendanceRecords.Add(new AttendanceRecord
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            Date = date,
            ClockInAt = new DateTimeOffset(
                date.Year, date.Month, date.Day, 9, 0, 0, TimeSpan.Zero),
        });

        await seed.SaveChangesAsync();
    }

    private async Task<Guid> HireAsync(ServiceProvider provider, string first) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = first,
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;
}
