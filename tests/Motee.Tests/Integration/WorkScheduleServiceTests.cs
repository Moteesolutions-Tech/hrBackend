using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Attendance;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Working hours, and who is held to which.
//
// The module's foundation: every attendance judgement is relative to a schedule, so
// the question that matters is not "can I create one" but "what is this person held
// to, always" — which has to have an answer for everybody, on day one, before anybody
// has configured anything.
[Collection(PostgresCollection.Name)]
public class WorkScheduleServiceTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _departmentId;

    private async Task<ServiceProvider> ArrangeAsync(string country = "NG")
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = country,
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private async Task<Guid> HireAsync(ServiceProvider provider, string email = "bola@acme.com")
    {
        EmployeeResult result = await Resolve<IEmployeeService>(provider)
            .CreateAsync(new EmployeeRequest
            {
                FirstName = "Bola",
                LastName = "Okafor",
                Email = email,
                Phone = "08012345678",
                JobTitle = "Engineer",
                DepartmentId = _departmentId,
                EmploymentType = EmploymentType.FullTime,
                Status = EmployeeStatus.Active,
            });

        Assert.True(result.Succeeded, result.Outcome.ToString());

        return result.Employee!.Id;
    }

    private static WorkScheduleRequest Nights(string name = "Night Shift") => new()
    {
        Name = name,
        Days = new Dictionary<string, WorkingHoursDto>(StringComparer.Ordinal)
        {
            ["monday"] = new() { Start = new TimeOnly(22, 0), End = new TimeOnly(6, 0) },
            ["tuesday"] = new() { Start = new TimeOnly(22, 0), End = new TimeOnly(6, 0) },
        },
        BreakMinutes = 30,
        GraceMinutes = 15,
    };

    // Without this, attendance has nothing to judge a punch against on day one and
    // every row reads "present, lateness unknown".
    [SkippableFact]
    public async Task ANewCompanyStartsWithADefaultSchedule()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        WorkScheduleDto schedule = Assert.Single(await Resolve<IWorkScheduleService>(provider)
            .ListAsync());

        Assert.True(schedule.IsDefault);

        // 08:00–17:00 is a nine-hour span; an hour of it is unpaid lunch.
        Assert.Equal(40m, schedule.WeeklyHours);
    }

    // Country dependent for the same reason leave is. A UK company's standard day is
    // not a Nigerian one's, and a shared default would be wrong for one of them from
    // the day it was written.
    [SkippableFact]
    public async Task TheDefaultHoursFollowTheCountry()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(country: "GB");

        WorkScheduleDto schedule = Assert.Single(
            await Resolve<IWorkScheduleService>(provider).ListAsync());

        Assert.Equal(37.5m, schedule.WeeklyHours);
        Assert.Equal(new TimeOnly(9, 0), schedule.Days["monday"].Start);
    }

    // The question attendance asks for every judgement it makes. It must have an
    // answer for somebody nobody has configured, which is nearly everybody.
    [SkippableFact]
    public async Task SomebodyWithNoScheduleOfTheirOwnFollowsTheDefault()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider);

        WorkScheduleDto? effective = await Resolve<IWorkScheduleService>(provider)
            .EffectiveForAsync(employeeId);

        Assert.NotNull(effective);
        Assert.True(effective.IsDefault);
    }

    [SkippableFact]
    public async Task SomebodyOnTheirOwnScheduleFollowsThatOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IWorkScheduleService schedules = Resolve<IWorkScheduleService>(provider);

        Guid employeeId = await HireAsync(provider);
        Guid nightsId = (await schedules.CreateAsync(Nights())).Schedule!.Id;

        Assert.Equal(
            WorkScheduleOutcome.Succeeded,
            await schedules.AssignAsync(employeeId, nightsId));

        WorkScheduleDto effective = (await schedules.EffectiveForAsync(employeeId))!;

        Assert.Equal(nightsId, effective.Id);

        // Two nights of 22:00–06:00 less a half-hour break each.
        Assert.Equal(15m, effective.WeeklyHours);
    }

    // A retired schedule must not keep judging people. Left as it was, somebody would
    // be held to hours the company has withdrawn, invisibly — the row still reads fine.
    [SkippableFact]
    public async Task ADeactivatedScheduleFallsBackToTheDefault()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IWorkScheduleService schedules = Resolve<IWorkScheduleService>(provider);

        Guid employeeId = await HireAsync(provider);
        Guid nightsId = (await schedules.CreateAsync(Nights())).Schedule!.Id;

        await schedules.AssignAsync(employeeId, nightsId);

        await using (MoteeDbContext retire = fixture.CreateContext(_tenantId))
        {
            Domain.Attendance.WorkSchedule nights = await retire.WorkSchedules
                .FirstAsync(schedule => schedule.Id == nightsId);

            nights.IsActive = false;
            await retire.SaveChangesAsync();
        }

        Assert.True((await schedules.EffectiveForAsync(employeeId))!.IsDefault);
    }

    // Null is a choice, not an omission: it is how somebody comes off a night shift.
    [SkippableFact]
    public async Task AssigningNullPutsThemBackOnTheDefault()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IWorkScheduleService schedules = Resolve<IWorkScheduleService>(provider);

        Guid employeeId = await HireAsync(provider);
        Guid nightsId = (await schedules.CreateAsync(Nights())).Schedule!.Id;

        await schedules.AssignAsync(employeeId, nightsId);
        await schedules.AssignAsync(employeeId, null);

        Assert.True((await schedules.EffectiveForAsync(employeeId))!.IsDefault);
    }

    // Adding a night shift must not quietly change what every new hire is held to.
    [SkippableFact]
    public async Task CreatingOneNeverMakesItTheDefault()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.False((await Resolve<IWorkScheduleService>(provider)
            .CreateAsync(Nights())).Schedule!.IsDefault);
    }

    // Exactly one default, so the move clears the old one in the same breath.
    [SkippableFact]
    public async Task MakingOneTheDefaultMovesItRatherThanAddingASecond()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IWorkScheduleService schedules = Resolve<IWorkScheduleService>(provider);

        Guid nightsId = (await schedules.CreateAsync(Nights())).Schedule!.Id;

        Assert.Equal(
            WorkScheduleOutcome.Succeeded,
            await schedules.MakeDefaultAsync(nightsId));

        IReadOnlyList<WorkScheduleDto> all = await schedules.ListAsync();

        Assert.Equal(nightsId, Assert.Single(all, schedule => schedule.IsDefault).Id);
    }

    // Something has to catch the next hire. A company that deletes its only schedule
    // finds out one punch at a time.
    [SkippableFact]
    public async Task TheDefaultCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IWorkScheduleService schedules = Resolve<IWorkScheduleService>(provider);

        Guid defaultId = (await schedules.ListAsync()).Single(s => s.IsDefault).Id;

        Assert.Equal(WorkScheduleOutcome.IsDefault, await schedules.DeleteAsync(defaultId));
    }

    // Refused rather than cascading them onto the default. Moving people between
    // schedules changes what counts as late for them, and doing it as a side effect of
    // a delete is a change nobody would find afterwards.
    [SkippableFact]
    public async Task AScheduleWithPeopleOnItCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IWorkScheduleService schedules = Resolve<IWorkScheduleService>(provider);

        Guid employeeId = await HireAsync(provider);
        Guid nightsId = (await schedules.CreateAsync(Nights())).Schedule!.Id;

        await schedules.AssignAsync(employeeId, nightsId);

        Assert.Equal(WorkScheduleOutcome.InUse, await schedules.DeleteAsync(nightsId));
    }

    [SkippableFact]
    public async Task AnEmptyScheduleIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            WorkScheduleOutcome.NoWorkingDays,
            (await Resolve<IWorkScheduleService>(provider).CreateAsync(new WorkScheduleRequest
            {
                Name = "Never",
                Days = new Dictionary<string, WorkingHoursDto>(StringComparer.Ordinal),
            })).Outcome);
    }

    // A break longer than the shift leaves negative paid hours, which would read as a
    // day somebody owes the company.
    [SkippableFact]
    public async Task ABreakLongerThanTheShiftIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            WorkScheduleOutcome.BreakLongerThanShift,
            (await Resolve<IWorkScheduleService>(provider).CreateAsync(new WorkScheduleRequest
            {
                Name = "Lunch Only",
                Days = new Dictionary<string, WorkingHoursDto>(StringComparer.Ordinal)
                {
                    ["monday"] = new() { Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0) },
                },
                BreakMinutes = 90,
            })).Outcome);
    }

    [SkippableFact]
    public async Task TwoSchedulesCannotShareAName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IWorkScheduleService schedules = Resolve<IWorkScheduleService>(provider);

        await schedules.CreateAsync(Nights());

        Assert.Equal(
            WorkScheduleOutcome.DuplicateName,
            (await schedules.CreateAsync(Nights())).Outcome);
    }

    // Answers "who would I have to move before deleting this", so it counts the people
    // explicitly on it rather than everybody the default happens to catch.
    [SkippableFact]
    public async Task TheAssignedCountIsThePeopleExplicitlyOnIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IWorkScheduleService schedules = Resolve<IWorkScheduleService>(provider);

        Guid first = await HireAsync(provider, "bola@acme.com");
        await HireAsync(provider, "cara@acme.com");

        Guid nightsId = (await schedules.CreateAsync(Nights())).Schedule!.Id;
        await schedules.AssignAsync(first, nightsId);

        IReadOnlyList<WorkScheduleDto> all = await schedules.ListAsync();

        Assert.Equal(1, all.Single(schedule => schedule.Id == nightsId).AssignedCount);
        Assert.Equal(0, all.Single(schedule => schedule.IsDefault).AssignedCount);
    }
}
