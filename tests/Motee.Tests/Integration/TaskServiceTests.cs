using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Work;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Work;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Tasks, out of the database.
//
// Every route is about the signed-in person, so most of these are about who
// can see and touch what: your own list, the work you gave out, and nothing
// else. The other half is the derivations — done is a date, the tab is worked
// out from who wrote the task, and overdue is answered against the company's
// clock rather than the hardcoded date the client compares to.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class TaskServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(new DateOnly(2026, 4, 23).ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _managerId;
    private Guid _reportId;
    private Guid _strangerId;

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

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _managerId = await HireAsync(provider, "Bola");
        _reportId = await HireAsync(provider, "Ngozi", managerId: _managerId);
        _strangerId = await HireAsync(provider, "Tunde");

        await AccountAsync(_managerId, "bola");
        await AccountAsync(_reportId, "ngozi");
        await AccountAsync(_strangerId, "tunde");

        await ActAsync(provider, _managerId);

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static ITaskService Tasks(ServiceProvider provider) =>
        Resolve<ITaskService>(provider);

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid? managerId = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            ManagerId = managerId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    private async Task AccountAsync(Guid employeeId, string handle)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = char.ToUpperInvariant(handle[0]) + handle[1..],
            LastName = "Okafor",
            Email = $"{handle}-login@acme.com",
            NormalizedEmail = $"{handle.ToUpperInvariant()}-LOGIN@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    // Signs in as the person behind an employee record.
    private async Task ActAsync(ServiceProvider provider, Guid employeeId)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid userId = await seed.Users
            .IgnoreQueryFilters()
            .Where(user => user.EmployeeId == employeeId && user.TenantId == _tenantId)
            .Select(user => user.Id)
            .FirstAsync();

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();
    }

    private static TaskItemRequest Asking(
        string title = "Review the Q1 headcount plan",
        TaskPriority priority = TaskPriority.Medium,
        DateOnly? dueOn = null,
        Guid? assignee = null,
        string? category = "HR",
        string? notes = null) => new()
        {
            Title = title,
            Notes = notes,
            AssigneeEmployeeId = assignee,
            Priority = priority,
            Category = category,
            DueOn = dueOn,
        };

    private static async Task<TaskItemDto> AddedAsync(
        ServiceProvider provider,
        TaskItemRequest request)
    {
        TaskResult result = await Tasks(provider).CreateAsync(request);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Task!;
    }

    // ── writing one for yourself ────────────────────────────────────────────

    [SkippableFact]
    public async Task ATaskWithNoAssigneeIsOnesOwn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto task = await AddedAsync(provider, Asking(title: "Renew car insurance"));

        Assert.Equal(_managerId, task.AssigneeEmployeeId);
        Assert.Equal(_managerId, task.CreatedByEmployeeId);
        Assert.Equal(TaskSource.Personal, task.Source);
        Assert.False(task.Done);
    }

    [SkippableFact]
    public async Task AcceptsATaskWithNoDueDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto task = await AddedAsync(provider, Asking(dueOn: null));

        Assert.Null(task.DueOn);
        Assert.Equal(TaskDueState.NotDue, task.DueState);
    }

    [SkippableFact]
    public async Task RefusesATaskWithNothingToDo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            TaskRefusal.TitleRequired,
            (await Tasks(provider).CreateAsync(Asking(title: " "))).Refusal);
    }

    // ── giving work to somebody else ────────────────────────────────────────

    [SkippableFact]
    public async Task AManagerMayPutWorkOnTheirReportsList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto task = await AddedAsync(provider, Asking(assignee: _reportId));

        Assert.Equal(_reportId, task.AssigneeEmployeeId);
        Assert.Equal(_managerId, task.CreatedByEmployeeId);
        Assert.Equal("Ngozi Okafor", task.AssigneeName);
        Assert.Equal("Bola Okafor", task.CreatedByName);

        // From the manager's own side it is work they gave out.
        Assert.Equal(TaskSource.Assigned, task.Source);
    }

    // Handing work to somebody who does not report to you is a request, not a
    // task — nothing in the org chart says it can be made to stick.
    [SkippableFact]
    public async Task NobodyMayPutWorkOnAStrangersList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskResult result = await Tasks(provider).CreateAsync(Asking(assignee: _strangerId));

        Assert.Equal(TaskRefusal.NotYoursToAssign, result.Refusal);
    }

    [SkippableFact]
    public async Task RefusesATaskForSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            TaskOutcome.EmployeeNotFound,
            (await Tasks(provider).CreateAsync(Asking(assignee: Guid.NewGuid()))).Outcome);
    }

    // The same row, read by the person who has to do it.
    [SkippableFact]
    public async Task AnAssignedTaskLandsOnTheReportsAssignedTab()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AddedAsync(provider, Asking(assignee: _reportId));

        await ActAsync(provider, _reportId);

        TaskItemDto theirs = Assert.Single(
            (await Tasks(provider).MineAsync(new TaskQuery())).Items);

        Assert.Equal(TaskSource.Assigned, theirs.Source);
    }

    // ── whose list it is ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OnesOwnListHasNobodyElsesTasksOnIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AddedAsync(provider, Asking(title: "Mine"));
        await AddedAsync(provider, Asking(title: "Theirs", assignee: _reportId));

        Assert.Equal(
            ["Mine"],
            (await Tasks(provider).MineAsync(new TaskQuery())).Items.Select(task => task.Title));
    }

    [SkippableFact]
    public async Task WhatAManagerGaveOutIsItsOwnList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AddedAsync(provider, Asking(title: "Mine"));
        await AddedAsync(provider, Asking(title: "Theirs", assignee: _reportId));

        Assert.Equal(
            ["Theirs"],
            (await Tasks(provider).AssignedByMeAsync(new TaskQuery()))
                .Items.Select(task => task.Title));
    }

    [SkippableFact]
    public async Task AStrangerCannotReadSomebodyElsesTask()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto task = await AddedAsync(provider, Asking());

        await ActAsync(provider, _strangerId);

        Assert.Null(await Tasks(provider).GetAsync(task.Id));
    }

    [SkippableFact]
    public async Task AStrangerCannotChangeSomebodyElsesTask()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto task = await AddedAsync(provider, Asking());

        await ActAsync(provider, _strangerId);

        Assert.Equal(
            TaskOutcome.Forbidden,
            (await Tasks(provider).CompleteAsync(task.Id)).Outcome);

        Assert.Equal(
            TaskOutcome.Forbidden,
            (await Tasks(provider).DeleteAsync(task.Id)).Outcome);
    }

    // The manager who raised it keeps access, which is what lets them chase it.
    [SkippableFact]
    public async Task TheManagerWhoRaisedATaskCanStillSeeAndCloseIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto task = await AddedAsync(provider, Asking(assignee: _reportId));

        Assert.NotNull(await Tasks(provider).GetAsync(task.Id));
        Assert.True((await Tasks(provider).CompleteAsync(task.Id)).Succeeded);
    }

    // ── done, and when ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CompletingATaskRecordsTheDay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto task = await AddedAsync(provider, Asking());

        TaskResult done = await Tasks(provider).CompleteAsync(task.Id);

        Assert.True(done.Task!.Done);
        Assert.Equal(new DateOnly(2026, 4, 23), done.Task.CompletedOn);
    }

    [SkippableFact]
    public async Task RecordsACompletionThatHappenedEarlier()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto task = await AddedAsync(provider, Asking());

        TaskResult done = await Tasks(provider)
            .CompleteAsync(task.Id, new DateOnly(2026, 4, 20));

        Assert.Equal(new DateOnly(2026, 4, 20), done.Task!.CompletedOn);
    }

    [SkippableFact]
    public async Task ReopeningATaskClearsTheCompletion()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto task = await AddedAsync(provider, Asking());

        await Tasks(provider).CompleteAsync(task.Id);

        TaskResult reopened = await Tasks(provider).ReopenAsync(task.Id);

        Assert.False(reopened.Task!.Done);
        Assert.Null(reopened.Task.CompletedOn);
    }

    // ── when it is due ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnswersOverdueAgainstTheCompanysClock()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto late = await AddedAsync(
            provider, Asking(title: "Late", dueOn: new DateOnly(2026, 4, 22)));

        TaskItemDto soon = await AddedAsync(
            provider, Asking(title: "Soon", dueOn: new DateOnly(2026, 4, 26)));

        TaskItemDto later = await AddedAsync(
            provider, Asking(title: "Later", dueOn: new DateOnly(2026, 5, 20)));

        Assert.Equal(TaskDueState.Overdue, late.DueState);
        Assert.Equal(TaskDueState.DueSoon, soon.DueState);
        Assert.Equal(TaskDueState.NotDue, later.DueState);
    }

    [SkippableFact]
    public async Task ADoneTaskIsNoLongerOverdue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto task = await AddedAsync(
            provider, Asking(dueOn: new DateOnly(2026, 1, 1)));

        Assert.Equal(TaskDueState.Overdue, task.DueState);

        TaskResult done = await Tasks(provider).CompleteAsync(task.Id);

        Assert.Equal(TaskDueState.NotDue, done.Task!.DueState);
    }

    // ── the list ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task FiltersByWhatIsStillToDo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto done = await AddedAsync(provider, Asking(title: "Done"));
        await AddedAsync(provider, Asking(title: "To do"));

        await Tasks(provider).CompleteAsync(done.Id);

        Assert.Equal(
            ["To do"],
            (await Tasks(provider).MineAsync(new TaskQuery { Done = false }))
                .Items.Select(task => task.Title));

        Assert.Equal(
            ["Done"],
            (await Tasks(provider).MineAsync(new TaskQuery { Done = true }))
                .Items.Select(task => task.Title));
    }

    [SkippableFact]
    public async Task FiltersByPriority()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AddedAsync(provider, Asking(title: "Urgent", priority: TaskPriority.High));
        await AddedAsync(provider, Asking(title: "Whenever", priority: TaskPriority.Low));

        Assert.Equal(
            ["Urgent"],
            (await Tasks(provider).MineAsync(new TaskQuery { Priority = TaskPriority.High }))
                .Items.Select(task => task.Title));
    }

    [SkippableFact]
    public async Task FiltersToWhatIsOverdue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AddedAsync(provider, Asking(title: "Late", dueOn: new DateOnly(2026, 4, 1)));
        await AddedAsync(provider, Asking(title: "Fine", dueOn: new DateOnly(2026, 6, 1)));

        Assert.Equal(
            ["Late"],
            (await Tasks(provider).MineAsync(new TaskQuery { OverdueOnly = true }))
                .Items.Select(task => task.Title));
    }

    [SkippableFact]
    public async Task FiltersByTab()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AddedAsync(provider, Asking(title: "Mine"));

        await ActAsync(provider, _reportId);
        await AddedAsync(provider, Asking(title: "Also mine"));

        await ActAsync(provider, _managerId);
        await AddedAsync(provider, Asking(title: "Given out", assignee: _reportId));

        await ActAsync(provider, _reportId);

        Assert.Equal(
            ["Also mine"],
            (await Tasks(provider).MineAsync(new TaskQuery { Source = TaskSource.Personal }))
                .Items.Select(task => task.Title));

        Assert.Equal(
            ["Given out"],
            (await Tasks(provider).MineAsync(new TaskQuery { Source = TaskSource.Assigned }))
                .Items.Select(task => task.Title));
    }

    [SkippableFact]
    public async Task SearchesTitleAndNotes()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AddedAsync(provider, Asking(
            title: "Buy a birthday gift for Tunde",
            notes: "He likes tech accessories."));

        foreach (string term in (string[])["birthday", "ACCESSORIES"])
        {
            Assert.NotEmpty(
                (await Tasks(provider).MineAsync(new TaskQuery { Search = term })).Items);
        }
    }

    [SkippableFact]
    public async Task DoesNotTreatASearchTermAsAWildcard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AddedAsync(provider, Asking(title: "Hit 100% of the target"));
        await AddedAsync(provider, Asking(title: "Something else"));

        Assert.Equal(
            ["Hit 100% of the target"],
            (await Tasks(provider).MineAsync(new TaskQuery { Search = "100%" }))
                .Items.Select(task => task.Title));
    }

    // ── reordering ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ReordersTheListTheWayItWasDragged()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto first = await AddedAsync(provider, Asking(title: "First"));
        TaskItemDto second = await AddedAsync(provider, Asking(title: "Second"));
        TaskItemDto third = await AddedAsync(provider, Asking(title: "Third"));

        Assert.True((await Tasks(provider)
            .ReorderAsync([third.Id, first.Id, second.Id])).Succeeded);

        Assert.Equal(
            ["Third", "First", "Second"],
            (await Tasks(provider).MineAsync(new TaskQuery())).Items.Select(task => task.Title));
    }

    // One stale id in a drag must not throw the whole reorder away, and it
    // certainly must not move somebody else's task.
    [SkippableFact]
    public async Task ReorderingIgnoresATaskThatIsNotYours()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto mine = await AddedAsync(provider, Asking(title: "Mine"));
        TaskItemDto alsoMine = await AddedAsync(provider, Asking(title: "Also mine"));

        await ActAsync(provider, _strangerId);
        TaskItemDto theirs = await AddedAsync(provider, Asking(title: "Theirs"));

        await ActAsync(provider, _managerId);

        Assert.True((await Tasks(provider)
            .ReorderAsync([alsoMine.Id, theirs.Id, mine.Id])).Succeeded);

        Assert.Equal(
            ["Also mine", "Mine"],
            (await Tasks(provider).MineAsync(new TaskQuery())).Items.Select(task => task.Title));

        // And the stranger's task kept its place.
        await ActAsync(provider, _strangerId);

        Assert.Equal(
            ["Theirs"],
            (await Tasks(provider).MineAsync(new TaskQuery())).Items.Select(task => task.Title));
    }

    // ── the dashboard counts ────────────────────────────────────────────────

    [SkippableFact]
    public async Task CountsTheListForTheDashboardWidgets()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto done = await AddedAsync(provider, Asking(title: "Done"));
        await AddedAsync(provider, Asking(title: "Late", dueOn: new DateOnly(2026, 4, 1)));
        await AddedAsync(provider, Asking(title: "Soon", dueOn: new DateOnly(2026, 4, 25)));

        await Tasks(provider).CompleteAsync(done.Id);

        // One given to them by somebody else.
        await ActAsync(provider, _reportId);
        await AddedAsync(provider, Asking(title: "Mine to do"));

        await ActAsync(provider, _managerId);
        await AddedAsync(provider, Asking(title: "Given out", assignee: _reportId));

        await ActAsync(provider, _reportId);

        TaskTally tally = await Tasks(provider).SummaryAsync();

        Assert.Equal(2, tally.Total);
        Assert.Equal(1, tally.Personal);
        Assert.Equal(1, tally.Assigned);
        Assert.Equal(0, tally.Done);
        Assert.Equal(2, tally.Outstanding);
    }

    [SkippableFact]
    public async Task AnAccountWithNoEmployeeRecordHasNoList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        // The account owner runs the company rather than working there.
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            Guid.NewGuid().ToString();

        Assert.Empty((await Tasks(provider).MineAsync(new TaskQuery())).Items);

        Assert.Equal(
            TaskOutcome.NoEmployeeRecord,
            (await Tasks(provider).CreateAsync(Asking())).Outcome);
    }

    // ── deleting ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task DeletesOnesOwnTask()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        TaskItemDto task = await AddedAsync(provider, Asking());

        Assert.True((await Tasks(provider).DeleteAsync(task.Id)).Succeeded);
        Assert.Null(await Tasks(provider).GetAsync(task.Id));
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersTasks()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AddedAsync(provider, Asking(title: "Ours"));

        RegisterTenantResult other = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ola",
            LastName = "Bello",
            Email = "ola@other.com",
            CompanyName = "Other Limited",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(other.Succeeded, string.Join("; ", other.Errors));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = other.TenantId;

        Assert.Empty((await Tasks(provider).MineAsync(new TaskQuery())).Items);
    }
}
