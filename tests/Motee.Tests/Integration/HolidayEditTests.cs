using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Leave;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Editing a closure day, and the two things a closure day can now say about itself:
// whether the company actually stops, and which departments carry on regardless.
//
// Both exist because the frontend's holiday screen offered them and the API had nowhere
// to put them. The point of the tests below is that neither is a stored flag somebody
// reads back — each one changes what a leave request costs, which is the only reason to
// record them at all.
[Collection(PostgresCollection.Name)]
public class HolidayEditTests(PostgresFixture fixture)
{
    // Pinned for the same reason the blackout suite pins: every range here is measured
    // from "today", and the seeded annual policy accrues monthly. A September window
    // keeps the whole thing inside one leave year with most of the entitlement accrued.
    //
    // 1 September 2026 is a Tuesday, which matters: the Thursday below is a working day,
    // so a closure on it is visible in the day count rather than hidden by a weekend.
    private static readonly DateOnly Pinned = new(2026, 9, 1);

    private readonly MovableClock _clock = new(
        new DateTimeOffset(Pinned.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _employeeId;
    private Guid _annualTypeId;
    private Guid _departmentId;
    private Guid _otherDepartmentId;

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
            CountryCode = "GB",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Warehouse", Code = "WH" }))
            .Department!.Id;

        _otherDepartmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Head Office", Code = "HO" }))
            .Department!.Id;

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            _annualTypeId = await seed.LeaveTypes
                .Where(type => type.Code == "annual").Select(type => type.Id).FirstAsync();

            // The seeded UK calendar would otherwise make every range's cost depend on
            // when the suite runs. Every holiday in these tests is added deliberately.
            seed.PublicHolidays.RemoveRange(seed.PublicHolidays);

            _employeeId = Guid.NewGuid();

            seed.Employees.Add(new Employee
            {
                Id = _employeeId,
                TenantId = _tenantId,
                FirstName = "Ben",
                LastName = "Adeyemi",
                Email = "ben@acme.com",
                DepartmentId = _departmentId,
                Status = EmployeeStatus.Active,
                StartDate = new DateOnly(2020, 1, 1),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    // Wednesday 2 September to Friday 4 September 2026: three working days, no weekend.
    private static readonly DateOnly RangeStart = new(2026, 9, 2);
    private static readonly DateOnly RangeEnd = new(2026, 9, 4);

    // The Thursday in the middle of that range.
    private static readonly DateOnly MidRange = new(2026, 9, 3);

    private Task<PublicHolidayResult> AddAsync(
        ServiceProvider provider,
        DateOnly? date = null,
        string name = "Founders' Day",
        bool isNonWorking = true,
        Guid[]? excluded = null) =>
        Resolve<ILeavePolicyService>(provider).AddHolidayAsync(new PublicHolidayRequest
        {
            Date = date ?? MidRange,
            Name = name,
            CountryCode = "GB",
            IsNonWorking = isNonWorking,
            ExcludedDepartmentIds = excluded ?? [],
        });

    private async Task<decimal> QuotedDaysAsync(ServiceProvider provider)
    {
        LeaveQuoteDto quote = await Resolve<ILeaveRequestService>(provider)
            .QuoteAsync(new LeaveQuoteRequest
            {
                EmployeeId = _employeeId,
                LeaveTypeId = _annualTypeId,
                StartDate = RangeStart,
                EndDate = RangeEnd,
            });

        return quote.TotalDays;
    }

    // ── editing ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AHolidayCanBeRenamedAndMoved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PublicHolidayResult added = await AddAsync(provider);
        Assert.True(added.Succeeded);

        PublicHolidayResult edited = await Resolve<ILeavePolicyService>(provider)
            .UpdateHolidayAsync(added.Holiday!.Id, new PublicHolidayRequest
            {
                Date = new DateOnly(2026, 9, 4),
                Name = "Founders' Day (moved)",
                CountryCode = "GB",
            });

        Assert.True(edited.Succeeded);
        Assert.Equal("Founders' Day (moved)", edited.Holiday!.Name);
        Assert.Equal(new DateOnly(2026, 9, 4), edited.Holiday.Date);

        // The same row, not a second one. An edit that deleted and re-created would
        // break anything holding the id — which is how the frontend got here.
        Assert.Equal(added.Holiday.Id, edited.Holiday.Id);

        IReadOnlyList<PublicHolidayDto> all = await Resolve<ILeavePolicyService>(provider)
            .ListHolidaysAsync(2026);

        Assert.Single(all);
    }

    [SkippableFact]
    public async Task EditingAHolidayOntoAnotherOnesDateIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PublicHolidayResult first = await AddAsync(provider, MidRange, "Founders' Day");
        Assert.True(first.Succeeded);

        PublicHolidayResult second = await AddAsync(
            provider, new DateOnly(2026, 9, 4), "Harvest Day");
        Assert.True(second.Succeeded);

        PublicHolidayResult clash = await Resolve<ILeavePolicyService>(provider)
            .UpdateHolidayAsync(second.Holiday!.Id, new PublicHolidayRequest
            {
                Date = MidRange,
                Name = "Harvest Day",
                CountryCode = "GB",
            });

        Assert.Equal(LeavePolicyOutcome.DuplicateDate, clash.Outcome);
    }

    // The uniqueness check must not fire against the row being edited, or renaming a
    // holiday without moving it would be refused for clashing with itself.
    [SkippableFact]
    public async Task RenamingAHolidayInPlaceIsNotADuplicate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PublicHolidayResult added = await AddAsync(provider);
        Assert.True(added.Succeeded);

        PublicHolidayResult renamed = await Resolve<ILeavePolicyService>(provider)
            .UpdateHolidayAsync(added.Holiday!.Id, new PublicHolidayRequest
            {
                Date = MidRange,
                Name = "Company Foundation Day",
                CountryCode = "GB",
            });

        Assert.True(renamed.Succeeded);
        Assert.Equal("Company Foundation Day", renamed.Holiday!.Name);
    }

    [SkippableFact]
    public async Task EditingAHolidayThatIsNotThereIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PublicHolidayResult missing = await Resolve<ILeavePolicyService>(provider)
            .UpdateHolidayAsync(Guid.NewGuid(), new PublicHolidayRequest
            {
                Date = MidRange,
                Name = "Founders' Day",
            });

        Assert.Equal(LeavePolicyOutcome.NotFound, missing.Outcome);
    }

    // ── a day the company marks but still works ────────────────────────────

    [SkippableFact]
    public async Task AClosureDayCostsNothingInALeaveRequest()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(3m, await QuotedDaysAsync(provider));

        Assert.True((await AddAsync(provider)).Succeeded);

        // The closure takes the middle day out of the charge.
        Assert.Equal(2m, await QuotedDaysAsync(provider));
    }

    [SkippableFact]
    public async Task AnObservanceTheCompanyStillWorksIsStillCharged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await AddAsync(provider, isNonWorking: false)).Succeeded);

        // Marked on the calendar, but the office is open — so somebody taking that day
        // off is taking a day of leave.
        Assert.Equal(3m, await QuotedDaysAsync(provider));
    }

    [SkippableFact]
    public async Task AWorkedObservanceIsNotListedAsANonWorkingDay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await AddAsync(provider, isNonWorking: false)).Succeeded);

        LeaveQuoteDto quote = await Resolve<ILeaveRequestService>(provider)
            .QuoteAsync(new LeaveQuoteRequest
            {
                EmployeeId = _employeeId,
                LeaveTypeId = _annualTypeId,
                StartDate = RangeStart,
                EndDate = RangeEnd,
            });

        // The form explains why ten calendar days cost six. Listing a day that was
        // charged for would make that explanation wrong.
        Assert.DoesNotContain(quote.NonWorkingDays, day => day.Date == MidRange);
    }

    // ── departments that carry on regardless ───────────────────────────────

    [SkippableFact]
    public async Task ADepartmentExemptFromAClosureIsStillCharged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        // Ben is in the Warehouse, and the Warehouse works through this one.
        Assert.True((await AddAsync(provider, excluded: [_departmentId])).Succeeded);

        Assert.Equal(3m, await QuotedDaysAsync(provider));
    }

    [SkippableFact]
    public async Task ADepartmentNotNamedInTheExemptionStillGetsTheDayFree()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        // Head Office works through it; Ben is in the Warehouse, which closes.
        Assert.True((await AddAsync(provider, excluded: [_otherDepartmentId])).Succeeded);

        Assert.Equal(2m, await QuotedDaysAsync(provider));
    }

    [SkippableFact]
    public async Task TheExcludedDepartmentsComeBackNamed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await AddAsync(provider, excluded: [_departmentId])).Succeeded);

        PublicHolidayDto holiday = Assert.Single(
            await Resolve<ILeavePolicyService>(provider).ListHolidaysAsync(2026));

        Assert.Equal([_departmentId], holiday.ExcludedDepartmentIds);

        // Resolved here rather than joined by the screen, the way a blackout's
        // departments already are.
        Assert.Equal(["Warehouse"], holiday.ExcludedDepartmentNames);

        Assert.True(holiday.IsNonWorking);
        Assert.False(holiday.ClosesEveryone);
    }

    [SkippableFact]
    public async Task AnOrdinaryClosureClosesEveryone()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await AddAsync(provider)).Succeeded);

        PublicHolidayDto holiday = Assert.Single(
            await Resolve<ILeavePolicyService>(provider).ListHolidaysAsync(2026));

        Assert.Empty(holiday.ExcludedDepartmentIds);
        Assert.True(holiday.ClosesEveryone);
    }

    // An exemption can be edited onto a day that did not have one, which is the whole
    // reason PUT exists — the frontend had to delete and re-create to change anything.
    [SkippableFact]
    public async Task AnExemptionCanBeAddedByEditing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PublicHolidayResult added = await AddAsync(provider);
        Assert.True(added.Succeeded);
        Assert.Equal(2m, await QuotedDaysAsync(provider));

        PublicHolidayResult edited = await Resolve<ILeavePolicyService>(provider)
            .UpdateHolidayAsync(added.Holiday!.Id, new PublicHolidayRequest
            {
                Date = MidRange,
                Name = "Founders' Day",
                CountryCode = "GB",
                ExcludedDepartmentIds = [_departmentId],
            });

        Assert.True(edited.Succeeded);
        Assert.Equal(3m, await QuotedDaysAsync(provider));
    }
}
