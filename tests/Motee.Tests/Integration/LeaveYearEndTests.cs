using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Leave;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Closing a leave year. The job that writes these rows runs once a year, unattended, so
// the things that matter are that it is safe to run twice and that it prices carry-over
// as at the year end rather than as at whenever it happened to run.
[Collection(PostgresCollection.Name)]
public class LeaveYearEndTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _employeeId;
    private Guid _annualTypeId;

    // A year that is safely in the past, so "today" never falls inside it however long
    // this suite lives.
    private static readonly DateOnly ClosingYear = new(2024, 6, 1);

    private async Task<ServiceProvider> ArrangeAsync(
        decimal annualDays = 25m,
        bool carryOverAllowed = true,
        decimal maxCarryOver = 5m,
        int expiryMonths = 3)
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

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            _annualTypeId = await seed.LeaveTypes
                .Where(type => type.Code == "annual").Select(type => type.Id).FirstAsync();

            LeavePolicy annual = await seed.LeavePolicies
                .FirstAsync(policy => policy.LeaveTypeId == _annualTypeId);

            annual.DaysPerYear = annualDays;
            annual.AccruesMonthly = false;
            annual.CarryOverAllowed = carryOverAllowed;
            annual.MaxCarryOverDays = maxCarryOver;
            annual.CarryOverExpiryMonths = expiryMonths;

            // Only annual leave carries in these tests; the rest would add noise to
            // every assertion about counts.
            foreach (LeavePolicy other in await seed.LeavePolicies
                .Where(policy => policy.LeaveTypeId != _annualTypeId).ToListAsync())
            {
                other.CarryOverAllowed = false;
                other.MaxCarryOverDays = 0m;
            }

            _employeeId = Guid.NewGuid();

            seed.Employees.Add(new Employee
            {
                Id = _employeeId,
                TenantId = _tenantId,
                FirstName = "Ben",
                LastName = "Adeyemi",
                Email = "ben@acme.com",
                Status = EmployeeStatus.Active,
                StartDate = new DateOnly(2020, 1, 1),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    // Books leave inside the closing year directly, bypassing the notice and balance
    // rules that would refuse a request dated two years ago.
    private async Task TakeAsync(decimal days)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.LeaveRequests.Add(new LeaveRequest
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = _employeeId,
            LeaveTypeId = _annualTypeId,
            StartDate = new DateOnly(2024, 7, 1),
            EndDate = new DateOnly(2024, 7, 1).AddDays((int)days),
            TotalDays = days,
            LeaveYearStart = new DateOnly(2024, 1, 1),
            Status = LeaveRequestStatus.Approved,
            SubmittedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task WhatIsLeftCarriesForwardUpToTheCap()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(annualDays: 25m, maxCarryOver: 5m);

        await TakeAsync(10m);

        LeaveYearEndResult closed = await Resolve<ILeaveYearEndService>(provider)
            .CloseAsync(ClosingYear);

        LeaveCarryOverDto carried = Assert.Single(closed.CarriedOver);

        // Fifteen left, capped at five.
        Assert.Equal(15m, carried.Available);
        Assert.Equal(5m, carried.Carried);
        Assert.Equal(10m, carried.Lapsed);
        Assert.Equal(new DateOnly(2025, 1, 1), closed.NextYearStart);
    }

    // The number HR open the screen for: how many days the company is about to lose.
    //
    // Counts every type that banks days, which is annual and sick — twenty annual days
    // above the carry-over cap, plus twelve untouched sick days. Sick leave is a yearly
    // bank whose unused days do genuinely lapse, even though nobody laments them.
    [SkippableFact]
    public async Task TheLapsedTotalIsReported()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(annualDays: 25m, maxCarryOver: 5m);

        LeaveYearEndResult closed = await Resolve<ILeaveYearEndService>(provider)
            .CloseAsync(ClosingYear);

        Assert.Equal(32m, closed.DaysLapsed);
    }

    // Per-occasion entitlements are not a bank anybody runs down, so they take no part in
    // a year end. Without this the headline would read 412 days lost, almost all of it
    // maternity leave nobody was ever going to take.
    [SkippableFact]
    public async Task PerOccasionEntitlementsAreNotCountedAsLost()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(annualDays: 25m, maxCarryOver: 5m);

        LeaveYearEndResult closed = await Resolve<ILeaveYearEndService>(provider)
            .CloseAsync(ClosingYear);

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        List<string> banked = await context.LeaveTypes
            .Where(type => context.LeavePolicies
                .Any(policy => policy.LeaveTypeId == type.Id && policy.TracksBalance))
            .Select(type => type.Code!)
            .ToListAsync();

        Assert.Equal(["annual", "sick"], banked.Order());

        // Well under the 412 it would be if maternity and the rest were included.
        Assert.True(closed.DaysLapsed < 50m, $"Lapsed was {closed.DaysLapsed}.");
    }

    [SkippableFact]
    public async Task NothingCarriesWhenThePolicyForbidsIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(carryOverAllowed: false);

        LeaveYearEndResult closed = await Resolve<ILeaveYearEndService>(provider)
            .CloseAsync(ClosingYear);

        Assert.Empty(closed.CarriedOver);

        // Twenty-five annual and twelve sick, all lost.
        Assert.Equal(37m, closed.DaysLapsed);
    }

    // The one that matters for an unattended job: a retry after a partial failure must
    // finish the work, not double everybody's days.
    [SkippableFact]
    public async Task ClosingTwiceDoesNotDoubleAnybodysDays()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ILeaveYearEndService yearEnd = Resolve<ILeaveYearEndService>(provider);

        await yearEnd.CloseAsync(ClosingYear);

        LeaveYearEndResult again = await yearEnd.CloseAsync(ClosingYear);

        Assert.True(again.AlreadyClosed);

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        LeaveCarryOver row = Assert.Single(await context.LeaveCarryOvers.ToListAsync());

        Assert.Equal(5m, row.Days);
    }

    // A second run must not move days somebody has already started spending in the new
    // year, even if their balance has changed since.
    [SkippableFact]
    public async Task ASecondRunLeavesWhatWasAlreadyWritten()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(annualDays: 25m, maxCarryOver: 5m);

        ILeaveYearEndService yearEnd = Resolve<ILeaveYearEndService>(provider);

        await yearEnd.CloseAsync(ClosingYear);

        // Somebody backdates leave into the closed year, changing what was available.
        await TakeAsync(24m);

        await yearEnd.CloseAsync(ClosingYear);

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        LeaveCarryOver row = Assert.Single(await context.LeaveCarryOvers.ToListAsync());

        Assert.Equal(5m, row.Days);
    }

    [SkippableFact]
    public async Task PreviewWritesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        LeaveYearEndResult preview = await Resolve<ILeaveYearEndService>(provider)
            .PreviewAsync(ClosingYear);

        Assert.False(preview.Applied);
        Assert.NotEmpty(preview.CarriedOver);

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        Assert.Empty(await context.LeaveCarryOvers.ToListAsync());
    }

    // Carried days lapse partway through the receiving year, and the expiry has to be
    // stamped from the new year's start rather than the old one's.
    [SkippableFact]
    public async Task CarriedDaysAreStampedWithTheirExpiry()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(expiryMonths: 3);

        await Resolve<ILeaveYearEndService>(provider).CloseAsync(ClosingYear);

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        LeaveCarryOver row = Assert.Single(await context.LeaveCarryOvers.ToListAsync());

        Assert.Equal(new DateOnly(2025, 4, 1), row.ExpiresOn);
    }

    // The point of carrying anything: the days show up in the next year's balance.
    [SkippableFact]
    public async Task CarriedDaysAppearInTheNextYearsBalance()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(annualDays: 25m, maxCarryOver: 5m);

        await Resolve<ILeaveYearEndService>(provider).CloseAsync(ClosingYear);

        LeaveBalanceDto balance = (await Resolve<ILeaveBalanceService>(provider)
            .ForAsync(_employeeId, _annualTypeId, new DateOnly(2025, 2, 1)))!;

        Assert.Equal(5m, balance.CarriedOver);
        Assert.Equal(5m, balance.CarriedOverAvailable);
        Assert.Equal(30m, balance.Available);
    }

    // And they stop counting once they lapse — booking against expired days is exactly
    // what the expiry exists to prevent.
    [SkippableFact]
    public async Task CarriedDaysStopCountingOnceTheyLapse()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync(expiryMonths: 3);

        await Resolve<ILeaveYearEndService>(provider).CloseAsync(ClosingYear);

        LeaveBalanceDto balance = (await Resolve<ILeaveBalanceService>(provider)
            .ForAsync(_employeeId, _annualTypeId, new DateOnly(2025, 6, 1)))!;

        Assert.Equal(5m, balance.CarriedOver);
        Assert.Equal(0m, balance.CarriedOverAvailable);
        Assert.Equal(25m, balance.Available);
    }

    // The daily job asks this for every tenant and the answer is no almost always, so it
    // has to be both cheap and right.
    [SkippableFact]
    public async Task TheFirstDayOfTheYearIsRecognised()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ILeaveYearEndService yearEnd = Resolve<ILeaveYearEndService>(provider);

        Assert.True(await yearEnd.IsFirstDayOfYearAsync(new DateOnly(2025, 1, 1)));
        Assert.False(await yearEnd.IsFirstDayOfYearAsync(new DateOnly(2025, 1, 2)));
        Assert.False(await yearEnd.IsFirstDayOfYearAsync(new DateOnly(2024, 12, 31)));
    }
}
