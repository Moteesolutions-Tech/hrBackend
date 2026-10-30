using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Motee.Api.Jobs;
using Motee.Application.Auth;
using Motee.Application.Contracts;
using Motee.Application.Employees;
using Motee.Application.Notifications;
using Motee.Application.Organisation;
using Motee.Domain.Contracts;
using Motee.Domain.Employees;
using Motee.Domain.Notifications;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The nightly sweep that turns derived conditions into something somebody is told.
//
// Everything it raises is already visible on a screen — a contract expiring, a
// document lapsed. Derived is the right way to store those and the wrong way to
// find out about them, and a contract that lapses because nobody opened the right
// screen is the most expensive bug here.
//
// The property worth protecting: running it twice tells nobody twice.
[Collection(PostgresCollection.Name)]
public class ExpirySweepTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _ownerUserId;
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
        _ownerUserId = registration.UserId;

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        Guid departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _employeeId = (await Resolve<IEmployeeService>(provider).CreateAsync(
            new EmployeeRequest
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

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private async Task<Guid> ExpiringContractAsync(ServiceProvider provider, int inDays)
    {
        IContractService contracts = Resolve<IContractService>(provider);

        ContractResult created = await contracts.CreateAsync(new ContractRequest
        {
            EmployeeId = _employeeId,
            Title = "Employment Contract",
            ContractType = ContractType.Employment,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-6),
            EndDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(inDays),
            NoticePeriodDays = 30,
        });

        Assert.True(created.Succeeded, created.Outcome.ToString());

        Guid id = created.Contract!.Id;

        // Only a contract in force can be expiring — a draft has no end to
        // approach.
        await contracts.SendForSignatureAsync(id);
        await contracts.ActivateAsync(id);

        return id;
    }

    // Built by hand rather than resolved: the job is registered by AddMoteeJobs,
    // which belongs to the Hangfire setup and is not part of the service graph
    // these tests build. Constructing it keeps the test independent of whether
    // Hangfire is hosted.
    private static ExpirySweepJob JobIn(IServiceScope scope) =>
        new(
            scope.ServiceProvider.GetRequiredService<MoteeDbContext>(),
            scope.ServiceProvider.GetRequiredService<Application.Notifications.INotificationService>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>(),
            scope.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger<ExpirySweepJob>());

    // Run as a worker would: with no tenant of its own.
    //
    // MutableTenant mirrors production — an explicit tenant wins and ambient is
    // only the fallback — so leaving one set would make every tenant's sweep
    // read the first tenant's rows. A Hangfire worker has no request and
    // therefore no explicit tenant, which is what makes AmbientTenant.Use the
    // thing that resolves. Clearing it here is the difference between testing
    // the job and testing a coincidence.
    private static async Task SweepAsync(ServiceProvider provider)
    {
        PostgresFixture.MutableTenant current =
            provider.GetRequiredService<PostgresFixture.MutableTenant>();

        Guid? acting = current.TenantId;
        current.TenantId = null;

        try
        {
            using IServiceScope scope = provider.CreateScope();

            await JobIn(scope).RunAsync();
        }
        finally
        {
            current.TenantId = acting;
        }
    }

    private async Task<int> RunAsync(ServiceProvider provider)
    {
        await SweepAsync(provider);

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        return await context.Notifications.CountAsync();
    }

    // The owner holds no access level since the separation, so a query over
    // permissions alone would miss the one person who cannot be locked out.
    [SkippableFact]
    public async Task TheOwnerIsToldAboutAnExpiringContract()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid contractId = await ExpiringContractAsync(provider, inDays: 10);

        Assert.Equal(1, await RunAsync(provider));

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        Notification raised = await context.Notifications.SingleAsync();

        Assert.Equal(_ownerUserId, raised.UserId);
        Assert.Equal(NotificationKind.ContractExpiring, raised.Kind);
        Assert.Equal(contractId, raised.SubjectId);
        Assert.Contains("10 days", raised.Body, StringComparison.Ordinal);
    }

    // The whole reason this is a notification rather than an email. Ninety nights
    // of sweeping leaves one row.
    [SkippableFact]
    public async Task RunningItAgainTellsNobodyTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ExpiringContractAsync(provider, inDays: 10);

        Assert.Equal(1, await RunAsync(provider));
        Assert.Equal(1, await RunAsync(provider));
        Assert.Equal(1, await RunAsync(provider));
    }

    // Outside the notice window there is nothing to act on yet.
    [SkippableFact]
    public async Task AContractWellInTheFutureRaisesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ExpiringContractAsync(provider, inDays: 200);

        Assert.Equal(0, await RunAsync(provider));
    }

    // Dismissing has to stick, or it means nothing for a condition that is still
    // true tomorrow night.
    [SkippableFact]
    public async Task ADismissedWarningIsNotRaisedAgainTheNextNight()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ExpiringContractAsync(provider, inDays: 10);
        await RunAsync(provider);

        await using (MoteeDbContext dismiss = fixture.CreateContext(_tenantId))
        {
            Notification raised = await dismiss.Notifications.SingleAsync();

            raised.DismissedAt = DateTimeOffset.UtcNow;
            await dismiss.SaveChangesAsync();
        }

        await SweepAsync(provider);

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        Assert.NotNull((await context.Notifications.SingleAsync()).DismissedAt);
    }

    // One company's rows must never reach another's panel — the sweep runs across
    // every tenant on a worker that belongs to none, which is exactly where a
    // missing tenant clause would not be noticed.
    [SkippableFact]
    public async Task OneCompanysWarningsStayInThatCompany()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ExpiringContractAsync(provider, inDays: 10);

        RegisterTenantResult other = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Chidi",
            LastName = "Eze",
            Email = "chidi@other.com",
            CompanyName = "Other Limited",
            Password = "correct-horse",
            CountryCode = "GB",
        });

        await SweepAsync(provider);

        await using MoteeDbContext unfiltered = fixture.CreateContext();

        List<Notification> all = await unfiltered.Notifications
            .IgnoreQueryFilters()
            .ToListAsync();

        Assert.Equal(_tenantId, Assert.Single(all).TenantId);
        Assert.NotEqual(other.TenantId, all[0].TenantId);
    }
}
