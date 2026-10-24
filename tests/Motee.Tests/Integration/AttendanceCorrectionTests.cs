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

namespace Motee.Tests.Integration;

// Correcting a day somebody else recorded.
//
// The thing being protected is the original reading. An employee amending their own
// punches would make the clock a suggestion; approving a correction in place would
// leave the new time indistinguishable from a real one. Both ends matter, and the
// tests that would catch either going wrong are the before-and-after ones.
[Collection(PostgresCollection.Name)]
public class AttendanceCorrectionTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _employeeId;
    private Guid _userId;

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

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        Guid departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _employeeId = (await Resolve<IEmployeeService>(provider)
            .CreateAsync(new EmployeeRequest
            {
                FirstName = "Bola",
                LastName = "Okafor",
                Email = "bola@acme.com",
                Phone = "08012345678",
                JobTitle = "Engineer",
                DepartmentId = departmentId,
                EmploymentType = EmploymentType.FullTime,
                Status = EmployeeStatus.Active,
            })).Employee!.Id;

        _userId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
            {
                Id = _userId,
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
            _userId.ToString();

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static DateOnly Yesterday() => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0) =>
        new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(1));

    private Task LogAsync(ServiceProvider provider, DateOnly day, int from, int to) =>
        Resolve<IAttendanceService>(provider).LogAsync(new LogAttendanceRequest
        {
            EmployeeId = _employeeId,
            Date = day,
            ClockInAt = At(day, from),
            ClockOutAt = At(day, to),
        });

    [SkippableFact]
    public async Task ACorrectionNeedsToSayWhatWasWrong()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            CorrectionOutcome.ReasonRequired,
            (await Resolve<IAttendanceCorrectionService>(provider).RequestAsync(
                new RequestCorrectionRequest
                {
                    Date = Yesterday(),
                    ClockOutAt = At(Yesterday(), 17),
                    Reason = "   ",
                })).Outcome);
    }

    [SkippableFact]
    public async Task ACorrectionThatChangesNothingIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly day = Yesterday();

        await LogAsync(provider, day, 8, 17);

        Assert.Equal(
            CorrectionOutcome.NothingToChange,
            (await Resolve<IAttendanceCorrectionService>(provider).RequestAsync(
                new RequestCorrectionRequest
                {
                    Date = day,
                    ClockInAt = At(day, 8),
                    ClockOutAt = At(day, 17),
                    Reason = "No change at all.",
                })).Outcome);
    }

    // Two open claims on one day are two answers to the same question.
    [SkippableFact]
    public async Task OnlyOneCorrectionCanBeOpenOnADay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceCorrectionService corrections =
            Resolve<IAttendanceCorrectionService>(provider);

        DateOnly day = Yesterday();

        RequestCorrectionRequest request = new()
        {
            Date = day,
            ClockOutAt = At(day, 17),
            Reason = "Forgot to clock out.",
        };

        Assert.True((await corrections.RequestAsync(request)).Succeeded);

        Assert.Equal(
            CorrectionOutcome.AlreadyPending,
            (await corrections.RequestAsync(request with { Reason = "Again." })).Outcome);
    }

    [SkippableFact]
    public async Task ADayThatHasNotHappenedCannotBeCorrected()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);

        Assert.Equal(
            CorrectionOutcome.InTheFuture,
            (await Resolve<IAttendanceCorrectionService>(provider).RequestAsync(
                new RequestCorrectionRequest
                {
                    Date = tomorrow,
                    ClockInAt = At(tomorrow, 8),
                    Reason = "Planning ahead.",
                })).Outcome);
    }

    // The point of the whole design: approving writes the agreed times and keeps
    // what the clock said, so a corrected day never passes for a punch.
    [SkippableFact]
    public async Task ApprovingAppliesTheTimesAndKeepsTheOriginal()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceCorrectionService corrections =
            Resolve<IAttendanceCorrectionService>(provider);

        DateOnly day = Yesterday();

        // Clocked in but forgot to clock out until the correction.
        await LogAsync(provider, day, 8, 12);

        Guid id = (await corrections.RequestAsync(new RequestCorrectionRequest
        {
            Date = day,
            ClockInAt = At(day, 8),
            ClockOutAt = At(day, 17),
            Reason = "Forgot to clock out at five.",
        })).Correction!.Id;

        AttendanceCorrectionDto approved = (await corrections.ApproveAsync(id)).Correction!;

        Assert.Equal(CorrectionStatus.Approved, approved.Status);

        // What the clock said, kept.
        Assert.Equal(At(day, 12).ToUniversalTime(), approved.OriginalClockOutAt);

        // And the day now reads as agreed — marked as typed, never as punched.
        AttendanceDayDto corrected = (await Resolve<IAttendanceService>(provider)
            .DaysForAsync(_employeeId, day, day))[0];

        Assert.Equal(At(day, 17).ToUniversalTime(), corrected.ClockOutAt);
        Assert.Equal(PunchSource.Manual, corrected.Source);
        Assert.True(corrected.RecordedByHand);
        Assert.Equal(9m, corrected.WorkedHours);
    }

    // The commonest correction there is, and the reason this hangs off a date rather
    // than an attendance row: there is nothing to point at until it is approved.
    [SkippableFact]
    public async Task ADayWithNoPunchesAtAllCanBeCorrectedIntoExistence()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceCorrectionService corrections =
            Resolve<IAttendanceCorrectionService>(provider);

        DateOnly day = Yesterday();

        Guid id = (await corrections.RequestAsync(new RequestCorrectionRequest
        {
            Date = day,
            ClockInAt = At(day, 8),
            ClockOutAt = At(day, 17),
            Reason = "Reader was down all day.",
        })).Correction!.Id;

        await corrections.ApproveAsync(id);

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        AttendanceRecord created = await context.AttendanceRecords.SingleAsync();

        Assert.Equal(day, created.Date);
        Assert.Equal(PunchSource.Manual, created.Source);
    }

    // Turning one down must leave the day exactly as it was.
    [SkippableFact]
    public async Task RejectingChangesNothingAboutTheDay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceCorrectionService corrections =
            Resolve<IAttendanceCorrectionService>(provider);

        DateOnly day = Yesterday();

        await LogAsync(provider, day, 8, 12);

        Guid id = (await corrections.RequestAsync(new RequestCorrectionRequest
        {
            Date = day,
            ClockOutAt = At(day, 23),
            Reason = "Worked very late.",
        })).Correction!.Id;

        AttendanceCorrectionDto rejected =
            (await corrections.RejectAsync(id, "No overtime was approved.")).Correction!;

        Assert.Equal(CorrectionStatus.Rejected, rejected.Status);
        Assert.Equal("No overtime was approved.", rejected.ResolutionNote);

        AttendanceDayDto untouched = (await Resolve<IAttendanceService>(provider)
            .DaysForAsync(_employeeId, day, day))[0];

        Assert.Equal(At(day, 12).ToUniversalTime(), untouched.ClockOutAt);
    }

    [SkippableFact]
    public async Task TurningOneDownNeedsAnExplanation()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceCorrectionService corrections =
            Resolve<IAttendanceCorrectionService>(provider);

        DateOnly day = Yesterday();

        Guid id = (await corrections.RequestAsync(new RequestCorrectionRequest
        {
            Date = day,
            ClockInAt = At(day, 8),
            Reason = "Forgot entirely.",
        })).Correction!.Id;

        Assert.Equal(
            CorrectionOutcome.ReasonRequired,
            (await corrections.RejectAsync(id, "  ")).Outcome);
    }

    // Resolved is terminal. Reversing a decision is a new claim about the corrected
    // times, not an edit to the old one.
    [SkippableFact]
    public async Task AResolvedCorrectionCannotBeResolvedAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceCorrectionService corrections =
            Resolve<IAttendanceCorrectionService>(provider);

        DateOnly day = Yesterday();

        Guid id = (await corrections.RequestAsync(new RequestCorrectionRequest
        {
            Date = day,
            ClockInAt = At(day, 8),
            Reason = "Forgot entirely.",
        })).Correction!.Id;

        await corrections.ApproveAsync(id);

        CorrectionResult again = await corrections.RejectAsync(id, "Changed my mind.");

        Assert.Equal(CorrectionOutcome.NotAllowed, again.Outcome);
        Assert.Contains("already been applied", again.Reason!, StringComparison.Ordinal);
    }

    // A day corrected once can be corrected again later — only the open ones are
    // unique, so the history survives.
    [SkippableFact]
    public async Task ADayCanBeCorrectedMoreThanOnceOverTime()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IAttendanceCorrectionService corrections =
            Resolve<IAttendanceCorrectionService>(provider);

        DateOnly day = Yesterday();

        Guid first = (await corrections.RequestAsync(new RequestCorrectionRequest
        {
            Date = day,
            ClockInAt = At(day, 8),
            ClockOutAt = At(day, 16),
            Reason = "Forgot to clock out.",
        })).Correction!.Id;

        await corrections.ApproveAsync(first);

        CorrectionResult second = await corrections.RequestAsync(new RequestCorrectionRequest
        {
            Date = day,
            ClockInAt = At(day, 8),
            ClockOutAt = At(day, 17),
            Reason = "It was five, not four.",
        });

        Assert.True(second.Succeeded, second.Outcome.ToString());
        Assert.Equal(2, (await corrections.MineAsync()).Count);
    }
}
