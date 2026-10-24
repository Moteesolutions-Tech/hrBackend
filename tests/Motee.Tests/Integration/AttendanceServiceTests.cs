using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Attendance;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Attendance;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The clock.
//
// Two things are being held in place here. The transition table, because every refusal
// in it is one somebody will hit — the double tap, the break after going home — and a
// service that grows those one at a time ends up answering differently in each branch.
// And the derivation: nothing about a day is stored except the instants, so the
// figures have to come out right from those alone.
[Collection(PostgresCollection.Name)]
public class AttendanceServiceTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _employeeId;

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

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        EmployeeResult hired = await Resolve<IEmployeeService>(provider)
            .CreateAsync(new EmployeeRequest
            {
                FirstName = "Bola",
                LastName = "Okafor",
                Email = "bola@acme.com",
                Phone = "08012345678",
                JobTitle = "Engineer",
                DepartmentId = _departmentId,
                EmploymentType = EmploymentType.FullTime,
                Status = EmployeeStatus.Active,
            });

        Assert.True(hired.Succeeded, hired.Outcome.ToString());

        _employeeId = hired.Employee!.Id;

        // The clock takes no employee id — it only ever acts on the caller's own day —
        // so the test has to be that person. ICurrentEmployee resolves through the
        // real lookup, which means an account linked to the employee rather than a
        // stub that would skip the thing worth exercising.
        Guid userId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
            {
                Id = userId,
                TenantId = _tenantId,
                EmployeeId = _employeeId,
                FirstName = "Bola",
                LastName = "Okafor",
                Email = "bola-login@acme.com",
                NormalizedEmail = "BOLA-LOGIN@ACME.COM",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    // An empty day is still a day: "not started" has to be distinguishable from "no
    // such person", because the clock widget renders something either way.
    [SkippableFact]
    public async Task ADayWithNoPunchesStillReads()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        AttendanceDayDto? day = await Resolve<IAttendanceService>(provider).MineAsync();

        Assert.NotNull(day);
        Assert.Equal(ClockState.NotStarted, day.State);
        Assert.Equal(0m, day.WorkedHours);

        // Today is still running, so nobody is absent yet.
        Assert.Equal(AttendanceStatus.NotClockedIn, day.Status);
    }

    [SkippableFact]
    public async Task ClockingInOpensTheDay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        AttendanceResult result = await Resolve<IAttendanceService>(provider)
            .ClockInAsync(new ClockInRequest { Location = WorkLocation.Remote });

        Assert.True(result.Succeeded, result.Outcome.ToString());
        Assert.Equal(ClockState.ClockedIn, result.Day!.State);
        Assert.Equal(WorkLocation.Remote, result.Day.Location);
        Assert.NotNull(result.Day.ClockInAt);
        Assert.False(result.Day.RecordedByHand);
    }

    // The impatient second tap. Refused with the sentence, not a state name — the
    // button is usually still on screen.
    [SkippableFact]
    public async Task ClockingInTwiceIsRefusedInWords()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceService attendance = Resolve<IAttendanceService>(provider);

        await attendance.ClockInAsync(new ClockInRequest());

        AttendanceResult again = await attendance.ClockInAsync(new ClockInRequest());

        Assert.Equal(AttendanceOutcome.NotAllowed, again.Outcome);
        Assert.Equal("You are already clocked in.", again.Reason);
    }

    [SkippableFact]
    public async Task ABreakCannotStartBeforeTheDayDoes()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        AttendanceResult result = await Resolve<IAttendanceService>(provider).StartBreakAsync();

        Assert.Equal(AttendanceOutcome.NotAllowed, result.Outcome);
        Assert.Contains("not clocked in", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task BreaksMoveTheStateAndBackAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceService attendance = Resolve<IAttendanceService>(provider);

        await attendance.ClockInAsync(new ClockInRequest());

        Assert.Equal(ClockState.OnBreak, (await attendance.StartBreakAsync()).Day!.State);
        Assert.Equal(AttendanceOutcome.NotAllowed, (await attendance.StartBreakAsync()).Outcome);
        Assert.Equal(ClockState.ClockedIn, (await attendance.EndBreakAsync()).Day!.State);
        Assert.Equal(AttendanceOutcome.NotAllowed, (await attendance.EndBreakAsync()).Outcome);
    }

    // Somebody who went home from lunch must not keep accruing break time against a
    // day that has ended.
    [SkippableFact]
    public async Task ClockingOutClosesAnOpenBreak()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceService attendance = Resolve<IAttendanceService>(provider);

        await attendance.ClockInAsync(new ClockInRequest());
        await attendance.StartBreakAsync();

        AttendanceDayDto day = (await attendance.ClockOutAsync(new ClockOutRequest())).Day!;

        Assert.Equal(ClockState.ClockedOut, day.State);
        Assert.All(day.Breaks, interval => Assert.NotNull(interval.End));
    }

    // Terminal. Re-opening a finished day is a correction, which is HR's act with its
    // own audit — not something the person does by tapping the button again.
    [SkippableFact]
    public async Task AFinishedDayAcceptsNothingFurther()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceService attendance = Resolve<IAttendanceService>(provider);

        await attendance.ClockInAsync(new ClockInRequest());
        await attendance.ClockOutAsync(new ClockOutRequest());

        Assert.Equal(
            AttendanceOutcome.NotAllowed,
            (await attendance.ClockInAsync(new ClockInRequest())).Outcome);
        Assert.Equal(AttendanceOutcome.NotAllowed, (await attendance.StartBreakAsync()).Outcome);
        Assert.Equal(
            AttendanceOutcome.NotAllowed,
            (await attendance.ClockOutAsync(new ClockOutRequest())).Outcome);
    }

    [SkippableFact]
    public async Task ClockingOutOfADayNeverStartedIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            AttendanceOutcome.NotAllowed,
            (await Resolve<IAttendanceService>(provider)
                .ClockOutAsync(new ClockOutRequest())).Outcome);
    }

    // Going home after lunch is a change to the day in progress, not a second arrival.
    [SkippableFact]
    public async Task LocationCanMoveWithoutPunchingAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceService attendance = Resolve<IAttendanceService>(provider);

        await attendance.ClockInAsync(new ClockInRequest { Location = WorkLocation.Office });

        AttendanceDayDto day = (await attendance.SetLocationAsync(
            new SetWorkLocationRequest { Location = WorkLocation.Remote })).Day!;

        Assert.Equal(WorkLocation.Remote, day.Location);
        Assert.Equal(ClockState.ClockedIn, day.State);
        Assert.Single(await OneDayAsync());
    }

    // The label HR owns. A client asking for it would make its own typing
    // indistinguishable from somebody turning up.
    [SkippableFact]
    public async Task APunchCannotClaimToHaveBeenTypedByHand()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        AttendanceDayDto day = (await Resolve<IAttendanceService>(provider)
            .ClockInAsync(new ClockInRequest { Source = PunchSource.Manual })).Day!;

        Assert.Equal(PunchSource.Web, day.Source);
        Assert.False(day.RecordedByHand);
    }

    // And the other way: a typed day says so, so a disputed one can be told apart.
    [SkippableFact]
    public async Task ADayTypedByHandSaysSo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        AttendanceResult logged = await Resolve<IAttendanceService>(provider)
            .LogAsync(new LogAttendanceRequest
            {
                EmployeeId = _employeeId,
                Date = yesterday,
                ClockInAt = new DateTimeOffset(
                    yesterday.ToDateTime(new TimeOnly(8, 0)), TimeSpan.FromHours(1)),
                ClockOutAt = new DateTimeOffset(
                    yesterday.ToDateTime(new TimeOnly(17, 0)), TimeSpan.FromHours(1)),
                BreakMinutes = 60,
                Note = "Reader was down.",
            });

        Assert.True(logged.Succeeded, logged.Outcome.ToString());
        Assert.Equal(PunchSource.Manual, logged.Day!.Source);
        Assert.True(logged.Day.RecordedByHand);

        // Nine hours less the hour of lunch.
        Assert.Equal(8m, logged.Day.WorkedHours);
    }

    // Almost always a device clock that is wrong, and storing it quietly leaves a
    // timesheet nobody can reconcile.
    [SkippableFact]
    public async Task ADayInTheFutureIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            AttendanceOutcome.InTheFuture,
            (await Resolve<IAttendanceService>(provider).LogAsync(new LogAttendanceRequest
            {
                EmployeeId = _employeeId,
                Date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3),
            })).Outcome);
    }

    [SkippableFact]
    public async Task ClockingOutBeforeClockingInIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        Assert.Equal(
            AttendanceOutcome.OutOfOrder,
            (await Resolve<IAttendanceService>(provider).LogAsync(new LogAttendanceRequest
            {
                EmployeeId = _employeeId,
                Date = yesterday,
                ClockInAt = new DateTimeOffset(
                    yesterday.ToDateTime(new TimeOnly(17, 0)), TimeSpan.FromHours(1)),
                ClockOutAt = new DateTimeOffset(
                    yesterday.ToDateTime(new TimeOnly(8, 0)), TimeSpan.FromHours(1)),
            })).Outcome);
    }

    // One person, one day — the unique index backs it, and logging twice replaces
    // rather than duplicating. Two half-days, one invisible, is the failure.
    [SkippableFact]
    public async Task LoggingTheSameDayTwiceReplacesIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceService attendance = Resolve<IAttendanceService>(provider);
        DateOnly yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        LogAttendanceRequest request = new()
        {
            EmployeeId = _employeeId,
            Date = yesterday,
            ClockInAt = new DateTimeOffset(
                yesterday.ToDateTime(new TimeOnly(8, 0)), TimeSpan.FromHours(1)),
            ClockOutAt = new DateTimeOffset(
                yesterday.ToDateTime(new TimeOnly(17, 0)), TimeSpan.FromHours(1)),
        };

        await attendance.LogAsync(request);
        await attendance.LogAsync(request with { Note = "Corrected." });

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        AttendanceRecord only = await context.AttendanceRecords.SingleAsync();

        Assert.Equal("Corrected.", only.Note);
    }

    // The whole reason the module reads a schedule. Nigeria's default day starts at
    // eight with ten minutes' grace, so ten past is fine and quarter past is not.
    [SkippableFact]
    public async Task LatenessIsJudgedAgainstTheirSchedule()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        // A Monday in the past, so the day is over and the status is settled.
        DateOnly monday = LastMonday();

        AttendanceDayDto late = (await Resolve<IAttendanceService>(provider)
            .LogAsync(Day(monday, new TimeOnly(9, 30), new TimeOnly(17, 0)))).Day!;

        Assert.Equal(AttendanceStatus.Late, late.Status);
        Assert.Equal("Standard", late.ScheduleName);
    }

    [SkippableFact]
    public async Task ArrivingWithinTheGracePeriodIsNotLate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        AttendanceDayDto day = (await Resolve<IAttendanceService>(provider)
            .LogAsync(Day(LastMonday(), new TimeOnly(8, 9), new TimeOnly(17, 0)))).Day!;

        Assert.Equal(AttendanceStatus.Present, day.Status);
    }

    [SkippableFact]
    public async Task LeavingEarlyIsRecordedAsSuch()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        AttendanceDayDto day = (await Resolve<IAttendanceService>(provider)
            .LogAsync(Day(LastMonday(), new TimeOnly(8, 0), new TimeOnly(15, 0)))).Day!;

        Assert.Equal(AttendanceStatus.EarlyDeparture, day.Status);
    }

    // Overtime is measured against what the contract asked for, not a flat eight.
    [SkippableFact]
    public async Task OvertimeIsBeyondTheScheduledHours()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        AttendanceDayDto day = (await Resolve<IAttendanceService>(provider)
            .LogAsync(Day(LastMonday(), new TimeOnly(8, 0), new TimeOnly(19, 0)))).Day!;

        // Eleven hours on the clock, less an unpaid hour, against eight contracted.
        Assert.Equal(8m, day.ScheduledHours);
        Assert.Equal(11m, day.WorkedHours);
        Assert.Equal(3m, day.OvertimeHours);
    }

    // A calendar with gaps where somebody was absent is a calendar that hides
    // absences, so every day in the range comes back whether or not it has punches.
    [SkippableFact]
    public async Task ARangeReturnsEveryDayIncludingTheEmptyOnes()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly monday = LastMonday();

        await Resolve<IAttendanceService>(provider)
            .LogAsync(Day(monday, new TimeOnly(8, 0), new TimeOnly(17, 0)));

        IReadOnlyList<AttendanceDayDto> week = await Resolve<IAttendanceService>(provider)
            .MineBetweenAsync(monday, monday.AddDays(6));

        Assert.Equal(7, week.Count);
        Assert.Equal(monday, week[0].Date);

        // The four weekdays nobody punched are absences, and the weekend is not.
        Assert.Equal(4, week.Count(day => day.Status == AttendanceStatus.Absent));
        Assert.DoesNotContain(
            week.Where(day => day.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday),
            day => day.Status == AttendanceStatus.Absent);
    }

    [SkippableFact]
    public async Task ABackwardsRangeIsEmptyRatherThanAnError()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly monday = LastMonday();

        Assert.Empty(await Resolve<IAttendanceService>(provider)
            .MineBetweenAsync(monday, monday.AddDays(-3)));
    }

    // The figures above the calendar, from the same derivation as the rows under it.
    [SkippableFact]
    public async Task TheSummaryCountsTheWeekAsTheRowsDo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceService attendance = Resolve<IAttendanceService>(provider);
        DateOnly monday = LastMonday();

        await attendance.LogAsync(Day(monday, new TimeOnly(8, 0), new TimeOnly(17, 0)));
        await attendance.LogAsync(
            Day(monday.AddDays(1), new TimeOnly(9, 30), new TimeOnly(19, 0)));

        AttendanceSummaryDto summary =
            await attendance.MineSummaryAsync(monday, monday.AddDays(6));

        Assert.Equal(2, summary.DaysPresent);
        Assert.Equal(1, summary.DaysLate);
        Assert.Equal(3, summary.DaysAbsent);

        // Nine hours on Monday and nine and a half on Tuesday — neither recorded a
        // break, and the schedule's assumed hour is not deducted from somebody who
        // did not take one. Each is an hour or more over the eight they are
        // contracted, so the overtime is the two together.
        Assert.Equal(18.5m, summary.TotalHours);
        Assert.Equal(2.5m, summary.OvertimeHours);

        // One of two attended days was on time.
        Assert.Equal(50, summary.PunctualityRate);
    }

    // A new joiner has not been late. Nought per cent punctual on their first screen
    // is a number somebody would have to explain.
    [SkippableFact]
    public async Task PunctualityIsFullWhenThereIsNothingToJudge()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly monday = LastMonday();

        AttendanceSummaryDto summary = await Resolve<IAttendanceService>(provider)
            .MineSummaryAsync(monday, monday.AddDays(6));

        Assert.Equal(100, summary.PunctualityRate);
        Assert.Equal(0, summary.DaysPresent);
    }

    private LogAttendanceRequest Day(DateOnly date, TimeOnly from, TimeOnly to) => new()
    {
        EmployeeId = _employeeId,
        Date = date,
        ClockInAt = new DateTimeOffset(date.ToDateTime(from), TimeSpan.FromHours(1)),
        ClockOutAt = new DateTimeOffset(date.ToDateTime(to), TimeSpan.FromHours(1)),
    };

    private static DateOnly LastMonday()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        // Strictly in the past, so "the day is over" holds and a status has settled.
        return monday >= today ? monday.AddDays(-7) : monday;
    }

    private async Task<List<AttendanceRecord>> OneDayAsync()
    {
        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        return await context.AttendanceRecords.ToListAsync();
    }
}
