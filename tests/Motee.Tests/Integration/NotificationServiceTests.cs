using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Notifications;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Notifications;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// The bell.
//
// Written before the service. One property carries the design: raising the same
// notification twice about the same subject does nothing the second time. Without
// it a nightly sweep over expiring contracts would add a row every night for
// ninety nights, and the panel would be unusable within a week.
[Collection(PostgresCollection.Name)]
public class NotificationServiceTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _userId;
    private Guid _otherUserId;

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

        _userId = await AccountAsync(provider, departmentId, "Bola", "bola@acme.com");
        _otherUserId = await AccountAsync(provider, departmentId, "Cara", "cara@acme.com");

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            _userId.ToString();

        return provider;
    }

    private async Task<Guid> AccountAsync(
        ServiceProvider provider,
        Guid departmentId,
        string first,
        string email)
    {
        Guid employeeId = (await Resolve<IEmployeeService>(provider).CreateAsync(
            new EmployeeRequest
            {
                FirstName = first,
                LastName = "Okafor",
                Email = email,
                Phone = "08012345678",
                JobTitle = "Engineer",
                DepartmentId = departmentId,
                EmploymentType = EmploymentType.FullTime,
                Status = EmployeeStatus.Active,
            })).Employee!.Id;

        Guid userId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = userId,
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = first,
            LastName = "Okafor",
            Email = $"login-{email}",
            NormalizedEmail = $"LOGIN-{email.ToUpperInvariant()}",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        return userId;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private RaiseNotificationRequest About(
        Guid subjectId,
        Guid? userId = null,
        NotificationKind kind = NotificationKind.ContractExpiring) => new()
    {
        TenantId = _tenantId,
        UserId = userId ?? _userId,
        Kind = kind,
        Severity = NotificationSeverity.Warning,
        Title = "Contract expiring",
        Body = "Bola Okafor's contract ends in 30 days.",
        Href = "/operations/contracts",
        SubjectType = "contract",
        SubjectId = subjectId,
    };

    [SkippableFact]
    public async Task ARaisedNotificationReachesTheRecipient()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        INotificationService notifications = Resolve<INotificationService>(provider);

        Assert.True(await notifications.RaiseAsync(About(Guid.NewGuid())));

        NotificationDto raised = Assert.Single(await notifications.MineAsync());

        Assert.Equal("Contract expiring", raised.Title);
        Assert.Equal(NotificationSeverity.Warning, raised.Severity);
        Assert.False(raised.Read);
    }

    // The property the whole design rests on. A sweep running nightly for ninety
    // nights must leave one row, not ninety.
    [SkippableFact]
    public async Task RaisingTheSameThingTwiceIsANoOp()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        INotificationService notifications = Resolve<INotificationService>(provider);
        Guid contractId = Guid.NewGuid();

        Assert.True(await notifications.RaiseAsync(About(contractId)));

        // False, so the caller can tell "told them" from "already had" — which is
        // what decides whether an email goes out alongside it.
        Assert.False(await notifications.RaiseAsync(About(contractId)));

        Assert.Single(await notifications.MineAsync());
    }

    // A different subject is a different notification, even of the same kind.
    [SkippableFact]
    public async Task ADifferentSubjectIsADifferentNotification()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        INotificationService notifications = Resolve<INotificationService>(provider);

        await notifications.RaiseAsync(About(Guid.NewGuid()));
        await notifications.RaiseAsync(About(Guid.NewGuid()));

        Assert.Equal(2, (await notifications.MineAsync()).Count);
    }

    // Addressed per person, so one recipient's panel is not everybody's. The
    // frontend filtered client-side, which shipped every notification's text to
    // every browser.
    [SkippableFact]
    public async Task SomebodySeesOnlyTheirOwn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        INotificationService notifications = Resolve<INotificationService>(provider);
        Guid contractId = Guid.NewGuid();

        await notifications.RaiseAsync(About(contractId));
        await notifications.RaiseAsync(About(contractId, userId: _otherUserId));

        Assert.Single(await notifications.MineAsync());
    }

    // Two people can be told about the same thing — that is not a duplicate.
    [SkippableFact]
    public async Task TheSameSubjectCanBeRaisedToTwoPeople()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        INotificationService notifications = Resolve<INotificationService>(provider);
        Guid contractId = Guid.NewGuid();

        Assert.True(await notifications.RaiseAsync(About(contractId)));
        Assert.True(await notifications.RaiseAsync(About(contractId, userId: _otherUserId)));
    }

    [SkippableFact]
    public async Task ReadingOneLeavesItVisibleAndDropsTheCount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        INotificationService notifications = Resolve<INotificationService>(provider);

        await notifications.RaiseAsync(About(Guid.NewGuid()));

        Guid id = (await notifications.MineAsync())[0].Id;

        Assert.Equal(1, await notifications.UnreadCountAsync());
        Assert.True(await notifications.MarkReadAsync(id));
        Assert.Equal(0, await notifications.UnreadCountAsync());

        // Still there. A condition that is still true should not vanish because
        // somebody glanced at it.
        Assert.True(Assert.Single(await notifications.MineAsync()).Read);
    }

    [SkippableFact]
    public async Task UnreadOnlyHidesTheOnesAlreadySeen()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        INotificationService notifications = Resolve<INotificationService>(provider);

        await notifications.RaiseAsync(About(Guid.NewGuid()));
        await notifications.RaiseAsync(About(Guid.NewGuid()));

        await notifications.MarkReadAsync((await notifications.MineAsync())[0].Id);

        Assert.Single(await notifications.MineAsync(unreadOnly: true));
        Assert.Equal(2, (await notifications.MineAsync()).Count);
    }

    [SkippableFact]
    public async Task MarkingAllReadClearsTheCount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        INotificationService notifications = Resolve<INotificationService>(provider);

        await notifications.RaiseAsync(About(Guid.NewGuid()));
        await notifications.RaiseAsync(About(Guid.NewGuid()));

        Assert.Equal(2, await notifications.MarkAllReadAsync());
        Assert.Equal(0, await notifications.UnreadCountAsync());
    }

    // Dismissing is "stop showing me", which read is not.
    [SkippableFact]
    public async Task DismissingTakesItOutOfThePanel()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        INotificationService notifications = Resolve<INotificationService>(provider);

        await notifications.RaiseAsync(About(Guid.NewGuid()));

        Assert.True(await notifications.DismissAsync((await notifications.MineAsync())[0].Id));
        Assert.Empty(await notifications.MineAsync());
    }

    // And it stays dismissed when the sweep runs again, or dismissing would mean
    // nothing for a condition that is still true.
    [SkippableFact]
    public async Task ADismissedNotificationDoesNotComeBack()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        INotificationService notifications = Resolve<INotificationService>(provider);
        Guid contractId = Guid.NewGuid();

        await notifications.RaiseAsync(About(contractId));
        await notifications.DismissAsync((await notifications.MineAsync())[0].Id);

        Assert.False(await notifications.RaiseAsync(About(contractId)));
        Assert.Empty(await notifications.MineAsync());
    }

    [SkippableFact]
    public async Task MarkingSomebodyElsesNotificationReadIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        INotificationService notifications = Resolve<INotificationService>(provider);

        await notifications.RaiseAsync(About(Guid.NewGuid(), userId: _otherUserId));

        await using MoteeDbContext context = fixture.CreateContext(_tenantId);

        Guid theirs = (await context.Notifications.FirstAsync()).Id;

        Assert.False(await notifications.MarkReadAsync(theirs));
        Assert.False(await notifications.DismissAsync(theirs));
    }
}
