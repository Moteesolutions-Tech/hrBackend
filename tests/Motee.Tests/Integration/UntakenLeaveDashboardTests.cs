using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Dashboard;
using Motee.Application.Employees;
using Motee.Application.Leave;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Who is carrying leave they are about to lose.
//
// The card this serves filtered on "more than ten days remaining", a number
// written into the component. That is the wrong question twice over: somebody
// with twelve days and unlimited carry-over loses nothing and would be chased,
// and somebody with six days and no carry-over at all loses all six and would
// not be. The threshold is the carry-over cap, which the policy already holds.
[Collection(PostgresCollection.Name)]
public class UntakenLeaveDashboardTests(PostgresFixture fixture)
{
    // Mid-year, so the annual policy has accrued most of its entitlement and
    // the figures do not depend on the day the suite runs.
    private static readonly DateOnly Today = new(2026, 9, 22);

    private readonly MovableClock _clock = new(
        new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _annualTypeId;

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

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            _annualTypeId = await seed.LeaveTypes
                .Where(type => type.Code == "annual").Select(type => type.Id).FirstAsync();
        }

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static Task<UntakenLeaveDashboardDto> BoardAsync(ServiceProvider provider) =>
        Resolve<IDashboardService>(provider).UntakenLeaveAsync();

    private async Task<Guid> HireAsync(ServiceProvider provider, string first) =>
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
            StartDate = new DateOnly(2020, 1, 10),
        })).Employee!.Id;

    // Carry-over sets the threshold, so each test says what the company allows.
    private async Task CarryOverAsync(decimal maxDays, bool allowed = true)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        LeavePolicy policy = await seed.LeavePolicies
            .FirstAsync(row => row.LeaveTypeId == _annualTypeId && row.IsActive);

        policy.CarryOverAllowed = allowed;
        policy.MaxCarryOverDays = maxDays;
        policy.UpdatedAt = _clock.GetUtcNow();

        await seed.SaveChangesAsync();
    }

    private async Task<decimal> AvailableAsync(ServiceProvider provider, Guid employeeId)
    {
        LeaveBalanceDto? balance = await Resolve<ILeaveBalanceService>(provider)
            .ForAsync(employeeId, _annualTypeId);

        Assert.NotNull(balance);

        return balance.Available;
    }

    // ── what counts as at risk ──────────────────────────────────────────────

    [SkippableFact]
    public async Task NobodyIsAtRiskWhenEverythingCanBeCarried()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada");

        // More than the whole entitlement, so nothing could ever be lost.
        await CarryOverAsync(await AvailableAsync(provider, ada) + 10m);

        Assert.Empty((await BoardAsync(provider)).AtRisk);
    }

    [SkippableFact]
    public async Task EverythingIsAtRiskWhenNothingCanBeCarried()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada");
        decimal available = await AvailableAsync(provider, ada);

        Assert.True(available > 0m, "The seeded annual policy should have accrued by September.");

        await CarryOverAsync(0m, allowed: false);

        UntakenLeaveRowDto row = Assert.Single((await BoardAsync(provider)).AtRisk);

        Assert.Equal(ada, row.EmployeeId);
        Assert.Equal("Ada Adeyemi", row.EmployeeName);
        Assert.Equal("Engineering", row.Department);
        Assert.Equal(available, row.Remaining);

        // All of it: the policy carries nothing over.
        Assert.Equal(available, row.AtRiskDays);
    }

    // The figure the card is really about. Twenty days remaining with five
    // carried is fifteen lost, not twenty — and "20" next to "at risk of
    // losing" is a number nobody can act on.
    [SkippableFact]
    public async Task OnlyTheDaysBeyondTheCapAreAtRisk()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada");
        decimal available = await AvailableAsync(provider, ada);

        await CarryOverAsync(available - 3m);

        UntakenLeaveRowDto row = Assert.Single((await BoardAsync(provider)).AtRisk);

        Assert.Equal(available, row.Remaining);
        Assert.Equal(3m, row.AtRiskDays);
    }

    // The case the old card got wrong. A flat "more than ten days remaining"
    // would have chased this person; they lose nothing, because the company
    // carries the lot over.
    //
    // The balance is pushed past ten by hand rather than assumed: the seeded
    // annual policy accrues monthly, so by September it has not reached ten on
    // its own, and asserting that it had made this test fail for a reason that
    // had nothing to do with what it was checking.
    [SkippableFact]
    public async Task ALargeBalanceThatCanAllBeCarriedIsNotFlagged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada");

        Assert.True((await Resolve<ILeaveBalanceService>(provider).AdjustAsync(
            new LeaveAdjustmentRequest
            {
                EmployeeId = ada,
                LeaveTypeId = _annualTypeId,
                Days = 15m,
                Reason = "Carried from a previous system.",
            })).Succeeded);

        decimal available = await AvailableAsync(provider, ada);

        Assert.True(available > 10m, "The adjustment should have taken the balance past ten.");

        await CarryOverAsync(available);

        Assert.Empty((await BoardAsync(provider)).AtRisk);
    }

    // ── ordering and the deadline ───────────────────────────────────────────

    [SkippableFact]
    public async Task TheMostAtRiskComesFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada");
        Guid ben = await HireAsync(provider, "Ben");

        await CarryOverAsync(0m, allowed: false);

        // Ben books some leave, so he has less left than Ada.
        Assert.True((await Resolve<ILeaveRequestService>(provider).SubmitAsync(
            new LeaveRequestSubmission
            {
                EmployeeId = ben,
                LeaveTypeId = _annualTypeId,
                StartDate = Today.AddDays(21),
                EndDate = Today.AddDays(25),
                Reason = "Holiday",
            })).Succeeded);

        IReadOnlyList<UntakenLeaveRowDto> atRisk = (await BoardAsync(provider)).AtRisk;

        Assert.Equal(2, atRisk.Count);
        Assert.Equal(ada, atRisk[0].EmployeeId);
        Assert.Equal(ben, atRisk[1].EmployeeId);
        Assert.True(atRisk[0].AtRiskDays > atRisk[1].AtRiskDays);
    }

    [SkippableFact]
    public async Task TheDeadlineIsTheEndOfTheLeaveYear()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada");
        await CarryOverAsync(0m, allowed: false);

        UntakenLeaveDashboardDto board = await BoardAsync(provider);

        Assert.Equal(Today, board.AsOf);

        // A January turnover by default, so the year ends on New Year's Eve.
        // What makes the list urgent, and without it the card is a number with
        // no deadline attached.
        Assert.Equal(new DateOnly(2026, 12, 31), board.LeaveYearEnds);
    }

    [SkippableFact]
    public async Task AnEmptyCompanyHasNobodyAtRiskAndNoDeadline()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        UntakenLeaveDashboardDto board = await BoardAsync(provider);

        Assert.Empty(board.AtRisk);
        Assert.Null(board.LeaveYearEnds);
    }
}
