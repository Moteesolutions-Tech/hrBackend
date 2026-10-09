using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Domain.Leave;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// What a company has on day one, and the reason this seeder is country-dependent when
// the others are not: statutory entitlement and the days a country is closed genuinely
// differ, and a shared default would be wrong for one of the two.
[Collection(PostgresCollection.Name)]
public class LeaveSeedTests(PostgresFixture fixture)
{
    private async Task<Guid> RegisterAsync(string countryCode)
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = countryCode,
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        return registration.TenantId;
    }

    private async Task<decimal> AnnualDaysAsync(Guid tenantId)
    {
        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        Guid typeId = await context.LeaveTypes
            .Where(type => type.Code == "annual")
            .Select(type => type.Id)
            .FirstAsync();

        return await context.LeavePolicies
            .Where(policy => policy.LeaveTypeId == typeId)
            .Select(policy => policy.DaysPerYear)
            .FirstAsync();
    }

    // 28 days including bank holidays, per the Working Time Regulations.
    [SkippableFact]
    public async Task AUkCompanyGetsTheUkStatutoryMinimum()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        Assert.Equal(28m, await AnnualDaysAsync(await RegisterAsync("GB")));
    }

    // Six working days after twelve months, per the Labour Act. Low enough that most
    // employers improve on it — which is exactly why it should be visible rather than
    // quietly assumed to be the UK number.
    [SkippableFact]
    public async Task ANigerianCompanyGetsTheNigerianStatutoryMinimum()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        Assert.Equal(6m, await AnnualDaysAsync(await RegisterAsync("NG")));
    }

    [SkippableFact]
    public async Task EveryLeaveTypeGetsAPolicy()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        Guid tenantId = await RegisterAsync("NG");

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        List<LeaveType> types = await context.LeaveTypes.ToListAsync();
        List<LeavePolicy> policies = await context.LeavePolicies.ToListAsync();

        Assert.Equal(6, types.Count);
        Assert.Equal(types.Count, policies.Count);

        // A type with no policy has no entitlement, so nobody could book it — which
        // would present as the type simply not working, with nothing saying why.
        Assert.All(types, type =>
            Assert.Contains(policies, policy => policy.LeaveTypeId == type.Id));
    }

    // Nobody gives notice of falling ill, and a policy demanding it would make every sick
    // request fail validation.
    [SkippableFact]
    public async Task SickLeaveNeedsNoNotice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        Guid tenantId = await RegisterAsync("GB");

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        LeavePolicy sick = await context.LeavePolicies
            .Where(policy => context.LeaveTypes
                .Any(type => type.Id == policy.LeaveTypeId && type.Code == "sick"))
            .FirstAsync();

        Assert.Equal(0, sick.MinNoticeDays);
        Assert.True(sick.RequiresMedicalCertificate);
        Assert.Contains("7 days", sick.AttachmentRequirement!, StringComparison.Ordinal);
    }

    // Unpaid leave has no entitlement to run down — the limit is what a manager agrees
    // to, not days somebody has banked.
    [SkippableFact]
    public async Task UnpaidLeaveIsUnpaidAndHasNoEntitlement()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        Guid tenantId = await RegisterAsync("GB");

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        LeaveType unpaid = await context.LeaveTypes.FirstAsync(type => type.Code == "unpaid");

        Assert.False(unpaid.IsPaid);

        Assert.Equal(
            0m,
            await context.LeavePolicies
                .Where(policy => policy.LeaveTypeId == unpaid.Id)
                .Select(policy => policy.DaysPerYear)
                .FirstAsync());
    }

    // Three years ahead, so somebody booking Christmas in eighteen months gets the right
    // day count rather than being charged for a day the office is shut.
    [SkippableFact]
    public async Task HolidaysAreSeededForThisYearAndTheNextTwo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        Guid tenantId = await RegisterAsync("GB");

        await using MoteeDbContext context = fixture.CreateContext(tenantId);

        List<int> years = await context.PublicHolidays
            .Select(holiday => holiday.Date.Year)
            .Distinct()
            .OrderBy(year => year)
            .ToListAsync();

        int thisYear = DateTime.UtcNow.Year;

        Assert.Equal([thisYear, thisYear + 1, thisYear + 2], years);
    }

    // The two countries genuinely do not share a calendar, which is the whole reason
    // these are seeded per tenant rather than shipped as one list.
    [SkippableFact]
    public async Task EachCountryGetsItsOwnHolidays()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        Guid ukTenantId = await RegisterAsync("GB");

        List<string> ukNames;

        await using (MoteeDbContext context = fixture.CreateContext(ukTenantId))
        {
            ukNames = await context.PublicHolidays
                .Select(holiday => holiday.Name)
                .Distinct()
                .ToListAsync();
        }

        Guid ngTenantId = await RegisterAsync("NG");

        await using MoteeDbContext ngContext = fixture.CreateContext(ngTenantId);

        List<string> ngNames = await ngContext.PublicHolidays
            .Select(holiday => holiday.Name)
            .Distinct()
            .ToListAsync();

        Assert.Contains(ukNames, name => name.Contains("Spring Bank", StringComparison.Ordinal));
        Assert.DoesNotContain(ngNames, name => name.Contains("Spring Bank", StringComparison.Ordinal));

        Assert.Contains(ngNames, name => name.Contains("Democracy", StringComparison.Ordinal));
        Assert.DoesNotContain(ukNames, name => name.Contains("Democracy", StringComparison.Ordinal));
    }

    // Registration runs before any tenant is current, so the context has nothing to stamp
    // these from — the seeder sets it explicitly. Getting it wrong writes rows the query
    // filter can never return, and the leave screens would simply be empty.
    [SkippableFact]
    public async Task EverythingSeededBelongsToTheTenantThatRegistered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        Guid tenantId = await RegisterAsync("NG");

        await using MoteeDbContext unfiltered = fixture.CreateContext();

        Assert.All(
            await unfiltered.LeaveTypes.IgnoreQueryFilters().ToListAsync(),
            type => Assert.Equal(tenantId, type.TenantId));

        Assert.All(
            await unfiltered.LeavePolicies.IgnoreQueryFilters().ToListAsync(),
            policy => Assert.Equal(tenantId, policy.TenantId));

        Assert.All(
            await unfiltered.PublicHolidays.IgnoreQueryFilters().ToListAsync(),
            holiday => Assert.Equal(tenantId, holiday.TenantId));
    }
}
