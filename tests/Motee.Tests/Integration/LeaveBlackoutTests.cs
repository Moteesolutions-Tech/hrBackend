using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Leave;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Periods the company will not approve planned leave over — the Christmas trading run,
// year-end close.
//
// The rule that matters most here is the one about what a blackout may *not* cover: sick
// leave is reported rather than requested, and a blackout blocking it would tell somebody
// they may not be ill until January.
[Collection(PostgresCollection.Name)]
public class LeaveBlackoutTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _employeeId;
    private Guid _annualTypeId;
    private Guid _sickTypeId;
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

        ServiceProvider provider = fixture.BuildProvider();
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

            _sickTypeId = await seed.LeaveTypes
                .Where(type => type.Code == "sick").Select(type => type.Id).FirstAsync();

            // The real UK calendar would otherwise make a range's cost depend on when the
            // suite runs.
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

    // Well clear of the seeded 14-day notice rule.
    private static DateOnly Soon(int addDays) =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(addDays));

    private Task<LeaveBlackoutResult> BlackoutAsync(
        ServiceProvider provider,
        int fromDay = 28,
        int toDay = 42,
        Guid[]? typeIds = null,
        Guid[]? departmentIds = null,
        string? reason = "The warehouse is closed to leave over the Christmas run.") =>
        Resolve<ILeavePolicyService>(provider).SaveBlackoutAsync(null, new LeaveBlackoutRequest
        {
            Name = "Christmas trading period",
            Reason = reason,
            StartDate = Soon(fromDay),
            EndDate = Soon(toDay),
            LeaveTypeIds = typeIds ?? [_annualTypeId],
            DepartmentIds = departmentIds ?? [],
        });

    private Task<LeaveRequestResult> BookAsync(
        ServiceProvider provider,
        int fromDay,
        int toDay,
        Guid? typeId = null) =>
        Resolve<ILeaveRequestService>(provider).SubmitAsync(new LeaveRequestSubmission
        {
            EmployeeId = _employeeId,
            LeaveTypeId = typeId ?? _annualTypeId,
            StartDate = Soon(fromDay),
            EndDate = Soon(toDay),
            Reason = "Holiday",
        });

    [SkippableFact]
    public async Task LeaveInsideABlackoutIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.True((await BlackoutAsync(provider)).Succeeded);

        LeaveRequestResult booked = await BookAsync(provider, 30, 34);

        Assert.Equal(LeaveRequestOutcome.Blackout, booked.Outcome);

        // The reason travels with the refusal. "Refused" on its own sends the person to
        // HR to ask why, which is the cost the message exists to avoid.
        Assert.Equal("The warehouse is closed to leave over the Christmas run.", booked.Reason);
    }

    // Any overlap, not containment. A fortnight booked across the blackout's first day is
    // exactly the booking it exists to prevent.
    [SkippableFact]
    public async Task LeaveOverlappingTheEdgeIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await BlackoutAsync(provider, fromDay: 28, toDay: 42);

        // Starts a week before the blackout and runs into its first days.
        Assert.Equal(LeaveRequestOutcome.Blackout, (await BookAsync(provider, 21, 30)).Outcome);

        // And the other edge.
        Assert.Equal(LeaveRequestOutcome.Blackout, (await BookAsync(provider, 40, 50)).Outcome);
    }

    [SkippableFact]
    public async Task LeaveEitherSideOfABlackoutIsFine()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await BlackoutAsync(provider, fromDay: 28, toDay: 42);

        Assert.True((await BookAsync(provider, 20, 24)).Succeeded);
        Assert.True((await BookAsync(provider, 50, 54)).Succeeded);
    }

    // The rule this whole design exists to protect. Somebody is ill whether or not it is a
    // busy week, and a blackout that blocked sick leave would be telling them otherwise.
    [SkippableFact]
    public async Task SickLeaveIsUnaffectedByAnAnnualLeaveBlackout()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await BlackoutAsync(provider, typeIds: [_annualTypeId]);

        Assert.Equal(LeaveRequestOutcome.Blackout, (await BookAsync(provider, 30, 34)).Outcome);

        LeaveRequestResult sick = await BookAsync(provider, 30, 34, _sickTypeId);

        Assert.True(sick.Succeeded, sick.Outcome.ToString());
    }

    // And it cannot be set up by accident either: there is no "applies to everything"
    // option, so covering sick leave takes somebody explicitly choosing it.
    [SkippableFact]
    public async Task ABlackoutNamingNoLeaveTypeIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        LeaveBlackoutResult result = await BlackoutAsync(provider, typeIds: []);

        Assert.Equal(LeavePolicyOutcome.InvalidRule, result.Outcome);
    }

    // "The warehouse cannot take December off, but head office can" is the ordinary shape
    // of this in retail and logistics.
    [SkippableFact]
    public async Task ABlackoutCanBeScopedToDepartments()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await BlackoutAsync(provider, departmentIds: [_otherDepartmentId]);

        // Ben is in the Warehouse; the blackout covers Head Office.
        LeaveRequestResult booked = await BookAsync(provider, 30, 34);

        Assert.True(booked.Succeeded, booked.Outcome.ToString());
    }

    [SkippableFact]
    public async Task ABlackoutWithNoDepartmentsCoversEveryone()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        LeaveBlackoutResult saved = await BlackoutAsync(provider, departmentIds: []);

        Assert.True(saved.Blackout!.AppliesToEveryone);
        Assert.Equal(LeaveRequestOutcome.Blackout, (await BookAsync(provider, 30, 34)).Outcome);
    }

    // Deactivating is how a company keeps last year's blackout on record without it
    // constraining anything.
    [SkippableFact]
    public async Task AnInactiveBlackoutBlocksNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        LeaveBlackoutResult saved = await BlackoutAsync(provider);

        await Resolve<ILeavePolicyService>(provider).SaveBlackoutAsync(
            saved.Blackout!.Id,
            new LeaveBlackoutRequest
            {
                Name = "Christmas trading period",
                StartDate = Soon(28),
                EndDate = Soon(42),
                LeaveTypeIds = [_annualTypeId],
                IsActive = false,
            });

        Assert.True((await BookAsync(provider, 30, 34)).Succeeded);
    }

    [SkippableFact]
    public async Task EndBeforeStartIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        LeaveBlackoutResult result = await BlackoutAsync(provider, fromDay: 42, toDay: 28);

        Assert.Equal(LeavePolicyOutcome.InvalidRule, result.Outcome);
    }

    // A blackout naming a type that does not exist here would sit in the list blocking
    // nothing while looking like it worked.
    [SkippableFact]
    public async Task ABlackoutNamingAnUnknownLeaveTypeIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        LeaveBlackoutResult result = await BlackoutAsync(provider, typeIds: [Guid.NewGuid()]);

        Assert.Equal(LeavePolicyOutcome.NotFound, result.Outcome);
    }

    // Names are resolved for the screen so it does not have to join the type and
    // department lists itself.
    [SkippableFact]
    public async Task TheListResolvesTypeAndDepartmentNames()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await BlackoutAsync(provider, typeIds: [_annualTypeId], departmentIds: [_departmentId]);

        LeaveBlackoutDto listed = Assert.Single(
            await Resolve<ILeavePolicyService>(provider).ListBlackoutsAsync());

        Assert.Equal(["Warehouse"], listed.DepartmentNames);
        Assert.Single(listed.LeaveTypeNames);
        Assert.False(listed.AppliesToEveryone);
    }

    [SkippableFact]
    public async Task ARemovedBlackoutStopsBlocking()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        LeaveBlackoutResult saved = await BlackoutAsync(provider);

        Assert.Equal(
            LeavePolicyOutcome.Succeeded,
            await Resolve<ILeavePolicyService>(provider).RemoveBlackoutAsync(saved.Blackout!.Id));

        Assert.True((await BookAsync(provider, 30, 34)).Succeeded);
    }

    // One company's blackout must not constrain another's staff.
    [SkippableFact]
    public async Task ABlackoutDoesNotReachAnotherCompany()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await BlackoutAsync(provider);

        await using MoteeDbContext unfiltered = fixture.CreateContext();

        LeaveBlackout stored = Assert.Single(
            await unfiltered.LeaveBlackouts.IgnoreQueryFilters().ToListAsync());

        Assert.Equal(_tenantId, stored.TenantId);
    }
}
