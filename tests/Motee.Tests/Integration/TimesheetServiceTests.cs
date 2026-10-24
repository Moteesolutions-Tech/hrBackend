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

// A week, handed in and signed off.
//
// Two things are worth holding here. The lifecycle, because a week that can be
// approved twice or handed in while already approved is a pay figure nobody can
// trust. And the snapshot: a timesheet derives its hours from the days underneath, so
// without capturing what was approved, a correction next week silently rewrites a
// number somebody already signed.
[Collection(PostgresCollection.Name)]
public class TimesheetServiceTests(PostgresFixture fixture)
{
    private Guid _tenantId;
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

        Guid departmentId = (await Resolve<IDepartmentService>(provider)
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
                DepartmentId = departmentId,
                EmploymentType = EmploymentType.FullTime,
                Status = EmployeeStatus.Active,
            });

        _employeeId = hired.Employee!.Id;

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

    // A week that finished, so it can legitimately be handed in.
    private static DateOnly LastWeek()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        return today.AddDays(-(((int)today.DayOfWeek + 6) % 7)).AddDays(-7);
    }

    private async Task WorkAsync(ServiceProvider provider, DateOnly day, TimeOnly to)
    {
        AttendanceResult logged = await Resolve<IAttendanceService>(provider)
            .LogAsync(new LogAttendanceRequest
            {
                EmployeeId = _employeeId,
                Date = day,
                ClockInAt = new DateTimeOffset(
                    day.ToDateTime(new TimeOnly(8, 0)), TimeSpan.FromHours(1)),
                ClockOutAt = new DateTimeOffset(
                    day.ToDateTime(to), TimeSpan.FromHours(1)),
            });

        Assert.True(logged.Succeeded, logged.Outcome.ToString());
    }

    // The week exists before anybody touches it, because the screen draws it either
    // way. Creating a row on read would write one per employee per week never worked.
    [SkippableFact]
    public async Task AWeekNobodyHasHandedInIsPendingRatherThanAbsent()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        TimesheetDto? sheet = await Resolve<ITimesheetService>(provider).MineAsync(LastWeek());

        Assert.NotNull(sheet);
        Assert.Equal(TimesheetStatus.Pending, sheet.Status);
        Assert.Equal(Guid.Empty, sheet.Id);
        Assert.Equal(7, sheet.Days.Count);
    }

    [SkippableFact]
    public async Task TheWeekRollsUpTheDaysUnderneathIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly monday = LastWeek();

        await WorkAsync(provider, monday, new TimeOnly(17, 0));
        await WorkAsync(provider, monday.AddDays(1), new TimeOnly(18, 0));

        TimesheetDto sheet = (await Resolve<ITimesheetService>(provider).MineAsync(monday))!;

        // Nine hours and ten, neither recording a break.
        Assert.Equal(19m, sheet.TotalHours);
        Assert.Equal(2, sheet.DaysPresent);
        Assert.Equal(3, sheet.DaysAbsent);
    }

    // Handing in Thursday's week on Thursday asks somebody to approve days that have
    // not happened.
    [SkippableFact]
    public async Task AWeekStillRunningCannotBeHandedIn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        DateOnly thisWeek = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.Equal(
            TimesheetOutcome.WeekNotOver,
            (await Resolve<ITimesheetService>(provider).SubmitAsync(thisWeek)).Outcome);
    }

    [SkippableFact]
    public async Task SubmittingMovesItIntoTheQueue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ITimesheetService timesheets = Resolve<ITimesheetService>(provider);
        DateOnly monday = LastWeek();

        await WorkAsync(provider, monday, new TimeOnly(17, 0));

        TimesheetResult submitted = await timesheets.SubmitAsync(monday);

        Assert.True(submitted.Succeeded, submitted.Outcome.ToString());
        Assert.Equal(TimesheetStatus.Submitted, submitted.Timesheet!.Status);
        Assert.NotNull(submitted.Timesheet.SubmittedAt);

        Assert.Single((await timesheets.ListAsync(
            new TimesheetQuery { Status = TimesheetStatus.Submitted })).Items);
    }

    [SkippableFact]
    public async Task AWeekAlreadyWithSomebodyCannotBeHandedInAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ITimesheetService timesheets = Resolve<ITimesheetService>(provider);
        DateOnly monday = LastWeek();

        await timesheets.SubmitAsync(monday);

        TimesheetResult again = await timesheets.SubmitAsync(monday);

        Assert.Equal(TimesheetOutcome.NotAllowed, again.Outcome);
        Assert.Contains("already been handed in", again.Reason!, StringComparison.Ordinal);
    }

    // Rejecting rather than deleting exists so the week can go back round.
    [SkippableFact]
    public async Task ARejectedWeekCanBeFixedAndHandedInAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ITimesheetService timesheets = Resolve<ITimesheetService>(provider);
        DateOnly monday = LastWeek();

        Guid id = (await timesheets.SubmitAsync(monday)).Timesheet!.Id;

        TimesheetResult rejected = await timesheets.RejectAsync(id, "Tuesday is missing.");

        Assert.Equal(TimesheetStatus.Rejected, rejected.Timesheet!.Status);
        Assert.Equal("Tuesday is missing.", rejected.Timesheet.RejectionReason);

        TimesheetResult resubmitted = await timesheets.SubmitAsync(monday);

        Assert.Equal(TimesheetStatus.Submitted, resubmitted.Timesheet!.Status);

        // Cleared, or the week reads as though it were rejected a second time.
        Assert.Null(resubmitted.Timesheet.RejectionReason);
    }

    // The person has to guess otherwise, and will hand the same week back.
    [SkippableFact]
    public async Task SendingAWeekBackNeedsAReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ITimesheetService timesheets = Resolve<ITimesheetService>(provider);

        Guid id = (await timesheets.SubmitAsync(LastWeek())).Timesheet!.Id;

        Assert.Equal(
            TimesheetOutcome.ReasonRequired,
            (await timesheets.RejectAsync(id, "   ")).Outcome);
    }

    [SkippableFact]
    public async Task AWeekNobodyHandedInCannotBeApproved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ITimesheetService timesheets = Resolve<ITimesheetService>(provider);
        DateOnly monday = LastWeek();

        Guid id = (await timesheets.SubmitAsync(monday)).Timesheet!.Id;
        await timesheets.RejectAsync(id, "Wrong week.");

        TimesheetResult refused = await timesheets.ApproveAsync(id);

        Assert.Equal(TimesheetOutcome.NotAllowed, refused.Outcome);
        Assert.Contains("sent back", refused.Reason!, StringComparison.Ordinal);
    }

    // Terminal. Reopening an approved week is a correction to the days inside it, not
    // a button that quietly unapproves somebody's pay.
    [SkippableFact]
    public async Task AnApprovedWeekIsFinished()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ITimesheetService timesheets = Resolve<ITimesheetService>(provider);
        DateOnly monday = LastWeek();

        Guid id = (await timesheets.SubmitAsync(monday)).Timesheet!.Id;
        await timesheets.ApproveAsync(id);

        Assert.Equal(TimesheetOutcome.NotAllowed, (await timesheets.ApproveAsync(id)).Outcome);
        Assert.Equal(
            TimesheetOutcome.NotAllowed, (await timesheets.RejectAsync(id, "Changed mind.")).Outcome);
        Assert.Equal(TimesheetOutcome.NotAllowed, (await timesheets.SubmitAsync(monday)).Outcome);
    }

    // The reason the snapshot exists. Without it, correcting a day next week reaches
    // back and rewrites the figure somebody already signed.
    [SkippableFact]
    public async Task ApprovingCapturesTheHoursAsTheyWere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ITimesheetService timesheets = Resolve<ITimesheetService>(provider);
        DateOnly monday = LastWeek();

        await WorkAsync(provider, monday, new TimeOnly(17, 0));

        Guid id = (await timesheets.SubmitAsync(monday)).Timesheet!.Id;

        TimesheetDto approved = (await timesheets.ApproveAsync(id)).Timesheet!;

        Assert.Equal(9m, approved.TotalHours);
        Assert.Equal(9m, approved.ApprovedTotalHours);
        Assert.False(approved.ChangedSinceApproval);
    }

    // And the flag payroll needs: a week approved at nine hours that now reads twelve
    // has had a day corrected underneath the approval.
    [SkippableFact]
    public async Task ACorrectionAfterApprovalIsVisibleRatherThanSilent()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ITimesheetService timesheets = Resolve<ITimesheetService>(provider);
        DateOnly monday = LastWeek();

        await WorkAsync(provider, monday, new TimeOnly(17, 0));

        Guid id = (await timesheets.SubmitAsync(monday)).Timesheet!.Id;
        await timesheets.ApproveAsync(id);

        // HR corrects the day after it was signed off.
        await WorkAsync(provider, monday, new TimeOnly(20, 0));

        TimesheetDto after = (await timesheets.GetAsync(id))!;

        Assert.Equal(12m, after.TotalHours);

        // What was approved has not moved, and the difference is surfaced.
        Assert.Equal(9m, after.ApprovedTotalHours);
        Assert.True(after.ChangedSinceApproval);
    }

    // Any day in the week is accepted and snapped to its Monday, so a client does not
    // have to agree with the server about where a week begins.
    [SkippableFact]
    public async Task AnyDayOfTheWeekIdentifiesTheSameWeek()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ITimesheetService timesheets = Resolve<ITimesheetService>(provider);
        DateOnly monday = LastWeek();

        Guid first = (await timesheets.SubmitAsync(monday.AddDays(3))).Timesheet!.Id;

        TimesheetDto sheet = (await timesheets.MineAsync(monday))!;

        Assert.Equal(first, sheet.Id);
        Assert.Equal(monday, sheet.WeekStart);
    }
}
