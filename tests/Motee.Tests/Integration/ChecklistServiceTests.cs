using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Checklists;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Checklists;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The onboarding checklist, out of the database.
//
// What these prove is that a starter's checklist is never materialised: it is
// the template resolved against their start date, so adding a task reaches
// everybody still onboarding and moving somebody's start date moves every
// deadline with it. There is no per-hire copy to go stale.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class ChecklistServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(new DateOnly(2026, 2, 1).ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;

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

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IChecklistService Checklist(ServiceProvider provider) =>
        Resolve<IChecklistService>(provider);

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        DateOnly? startDate = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Mobile Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = startDate ?? new DateOnly(2026, 1, 15),
        })).Employee!.Id;

    private static ChecklistItemRequest Task(
        string title = "Order a security pass",
        int offset = 2,
        bool required = true,
        ResponsibleParty party = ResponsibleParty.Hr,
        string? category = "Facilities") => new()
        {
            Title = title,
            Description = "Raise the pass with building reception.",
            Category = category,
            ResponsibleParty = party,
            DueDayOffset = offset,
            IsRequired = required,
        };

    private static async Task<ChecklistItemDto> AddedAsync(
        ServiceProvider provider,
        ChecklistItemRequest request)
    {
        ChecklistResult result = await Checklist(provider).CreateItemAsync(request);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Item!;
    }

    private static async Task<ChecklistItemDto> SeededAsync(ServiceProvider provider, string title) =>
        (await Checklist(provider).ListItemsAsync())
            .First(item => item.Title == title);

    // ── the template a tenant starts with ───────────────────────────────────

    [SkippableFact]
    public async Task SeedsTheShippedTemplateLiveAndInOrder()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<ChecklistItemDto> items = await Checklist(provider).ListItemsAsync();

        Assert.Equal(ChecklistSeeds.All.Count, items.Count);

        // In the client's order, and live — unlike the benefits catalogue,
        // nothing here asserts a contract or a figure that could be wrong.
        Assert.Equal(
            ChecklistSeeds.All.Select(seed => seed.Title),
            items.Select(item => item.Title));

        Assert.All(items, item => Assert.True(item.IsActive));

        // Nobody has done anything yet, so every task is deletable.
        Assert.All(items, item =>
        {
            Assert.Equal(0, item.Completions);
            Assert.True(item.CanDelete);
        });
    }

    [SkippableFact]
    public async Task SeedsTheTaskThatHasToHappenBeforeSomebodyArrives()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        ChecklistItemDto paperwork =
            await SeededAsync(provider, "Complete employment paperwork");

        Assert.Equal(-7, paperwork.DueDayOffset);
    }

    // ── editing the template ────────────────────────────────────────────────

    [SkippableFact]
    public async Task AddsATaskAtTheEndOfTheTemplate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        ChecklistItemDto added = await AddedAsync(provider, Task());

        Assert.Equal(
            added.Id,
            (await Checklist(provider).ListItemsAsync())[^1].Id);
    }

    [SkippableFact]
    public async Task UpdatesEveryFieldTheFormCarries()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        ChecklistItemDto added = await AddedAsync(provider, Task());

        ChecklistResult updated = await Checklist(provider).UpdateItemAsync(
            added.Id,
            Task(
                title: "Order a security pass and locker key",
                offset: -3,
                required: false,
                party: ResponsibleParty.It,
                category: "IT Setup"));

        Assert.True(updated.Succeeded, $"{updated.Outcome} / {updated.Refusal}");

        ChecklistItemDto saved = updated.Item!;

        Assert.Equal("Order a security pass and locker key", saved.Title);
        Assert.Equal(-3, saved.DueDayOffset);
        Assert.False(saved.IsRequired);
        Assert.Equal(ResponsibleParty.It, saved.ResponsibleParty);
        Assert.Equal("IT Setup", saved.Category);
    }

    [SkippableFact]
    public async Task RefusesATaskWithNoUsableName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ChecklistRefusal.TitleRequired,
            (await Checklist(provider).CreateItemAsync(Task(title: " "))).Refusal);
    }

    [SkippableFact]
    public async Task RefusesAnOffsetBeyondAnyRealOnboarding()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ChecklistRefusal.OffsetOutOfRange,
            (await Checklist(provider).CreateItemAsync(Task(offset: 400))).Refusal);
    }

    [SkippableFact]
    public async Task MovesATaskUpAndDownOnePlace()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<ChecklistItemDto> before = await Checklist(provider).ListItemsAsync();

        Guid second = before[1].Id;
        Guid first = before[0].Id;

        Assert.True((await Checklist(provider).MoveItemAsync(second, up: true)).Succeeded);

        IReadOnlyList<ChecklistItemDto> after = await Checklist(provider).ListItemsAsync();

        Assert.Equal(second, after[0].Id);
        Assert.Equal(first, after[1].Id);

        // And back again.
        Assert.True((await Checklist(provider).MoveItemAsync(second, up: false)).Succeeded);

        Assert.Equal(first, (await Checklist(provider).ListItemsAsync())[0].Id);
    }

    [SkippableFact]
    public async Task MovingTheFirstTaskUpChangesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<ChecklistItemDto> before = await Checklist(provider).ListItemsAsync();

        Assert.True((await Checklist(provider).MoveItemAsync(before[0].Id, up: true)).Succeeded);

        Assert.Equal(
            before.Select(item => item.Id),
            (await Checklist(provider).ListItemsAsync()).Select(item => item.Id));
    }

    [SkippableFact]
    public async Task RetiresAndRestoresATask()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        ChecklistItemDto added = await AddedAsync(provider, Task());

        Assert.False((await Checklist(provider).RetireItemAsync(added.Id)).Item!.IsActive);

        // Still listed, because the screen that brings it back has to see it.
        Assert.Contains(
            await Checklist(provider).ListItemsAsync(),
            item => item.Id == added.Id);

        Assert.DoesNotContain(
            await Checklist(provider).ListItemsAsync(includeRetired: false),
            item => item.Id == added.Id);

        Assert.True((await Checklist(provider).RestoreItemAsync(added.Id)).Item!.IsActive);
    }

    [SkippableFact]
    public async Task DeletesATaskNobodyHasDone()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        ChecklistItemDto added = await AddedAsync(provider, Task());

        Assert.True((await Checklist(provider).DeleteItemAsync(added.Id)).Succeeded);

        Assert.DoesNotContain(
            await Checklist(provider).ListItemsAsync(),
            item => item.Id == added.Id);
    }

    // Deleting it would take the record of what was asked with it.
    [SkippableFact]
    public async Task WillNotDeleteATaskSomebodyHasCompleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Emeka");
        ChecklistItemDto added = await AddedAsync(provider, Task());

        await Checklist(provider).CompleteAsync(
            employeeId, added.Id, new ChecklistCompletionRequest());

        ChecklistResult result = await Checklist(provider).DeleteItemAsync(added.Id);

        Assert.Equal(ChecklistRefusal.InUse, result.Refusal);

        ChecklistItemDto after = (await Checklist(provider).ListItemsAsync())
            .First(item => item.Id == added.Id);

        Assert.Equal(1, after.Completions);
        Assert.False(after.CanDelete);
    }

    // ── a starter's checklist ───────────────────────────────────────────────

    [SkippableFact]
    public async Task ResolvesEveryDeadlineAgainstTheStartDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Emeka", new DateOnly(2026, 1, 15));

        EmployeeChecklistDto? checklist = await Checklist(provider).ForEmployeeAsync(employeeId);

        Assert.NotNull(checklist);
        Assert.Equal(ChecklistSeeds.All.Count, checklist.Tasks.Count);

        ChecklistTaskDto paperwork = checklist.Tasks
            .First(task => task.Item.Title == "Complete employment paperwork");

        // Seven days before they started.
        Assert.Equal(new DateOnly(2026, 1, 8), paperwork.DueOn);

        ChecklistTaskDto probation = checklist.Tasks
            .First(task => task.Item.Title == "Complete probation review form");

        Assert.Equal(new DateOnly(2026, 4, 15), probation.DueOn);
    }

    // Nothing stored per hire, so moving the start date moves every deadline.
    [SkippableFact]
    public async Task MovingTheStartDateMovesEveryDeadlineWithIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Emeka", new DateOnly(2026, 1, 15));

        await using (MoteeDbContext update = fixture.CreateContext(_tenantId))
        {
            Employee employee = await update.Employees.FirstAsync(row => row.Id == employeeId);

            employee.StartDate = new DateOnly(2026, 3, 2);

            await update.SaveChangesAsync();
        }

        EmployeeChecklistDto? checklist = await Checklist(provider).ForEmployeeAsync(employeeId);

        Assert.Equal(
            new DateOnly(2026, 2, 23),
            checklist!.Tasks.First(task => task.Item.Title == "Complete employment paperwork").DueOn);
    }

    // And adding a task reaches everybody still onboarding.
    [SkippableFact]
    public async Task AddingATaskReachesAStarterAlreadyOnboarding()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Emeka");

        int before = (await Checklist(provider).ForEmployeeAsync(employeeId))!.Tasks.Count;

        await AddedAsync(provider, Task());

        Assert.Equal(
            before + 1,
            (await Checklist(provider).ForEmployeeAsync(employeeId))!.Tasks.Count);
    }

    [SkippableFact]
    public async Task ARetiredTaskDropsOffAStartersChecklist()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Emeka");

        ChecklistItemDto added = await AddedAsync(provider, Task());

        await Checklist(provider).RetireItemAsync(added.Id);

        Assert.DoesNotContain(
            (await Checklist(provider).ForEmployeeAsync(employeeId))!.Tasks,
            task => task.Item.Id == added.Id);
    }

    [SkippableFact]
    public async Task ReportsNothingForSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Null(await Checklist(provider).ForEmployeeAsync(Guid.NewGuid()));
    }

    // Overdue needs a deadline to be late against, and a starter with no start
    // date has none — blaming them for a date nobody set would be worse than
    // reporting the task as simply outstanding.
    [SkippableFact]
    public async Task AStarterWithNoStartDateHasTasksButNoDeadlines()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Emeka");

        await using (MoteeDbContext update = fixture.CreateContext(_tenantId))
        {
            Employee employee = await update.Employees.FirstAsync(row => row.Id == employeeId);

            employee.StartDate = null;

            await update.SaveChangesAsync();
        }

        EmployeeChecklistDto? checklist = await Checklist(provider).ForEmployeeAsync(employeeId);

        Assert.All(checklist!.Tasks, task =>
        {
            Assert.Null(task.DueOn);
            Assert.Equal(ChecklistTaskState.Outstanding, task.State);
        });

        Assert.Equal(ChecklistProgressState.NotStarted, checklist.Hire.Progress.State);
        Assert.Equal(0, checklist.Hire.Progress.Overdue);
    }

    // ── ticking tasks off ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task CompletesATaskAndReportsTheNewProgress()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Emeka");

        ChecklistItemDto orientation = await SeededAsync(provider, "HR orientation session");

        ChecklistResult result = await Checklist(provider).CompleteAsync(
            employeeId,
            orientation.Id,
            new ChecklistCompletionRequest { Note = "Ran through the handbook." });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        EmployeeChecklistDto checklist = result.Checklist!;

        ChecklistTaskDto done = checklist.Tasks
            .First(task => task.Item.Id == orientation.Id);

        Assert.Equal(ChecklistTaskState.Done, done.State);
        Assert.Equal(new DateOnly(2026, 2, 1), done.CompletedOn);
        Assert.Equal("Ran through the handbook.", done.Note);
        Assert.Equal(1, checklist.Hire.Progress.Completed);
    }

    [SkippableFact]
    public async Task RecordsACompletionThatHappenedEarlier()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Emeka");

        ChecklistItemDto item = await SeededAsync(provider, "Set up workstation and accounts");

        ChecklistResult result = await Checklist(provider).CompleteAsync(
            employeeId,
            item.Id,
            new ChecklistCompletionRequest { CompletedOn = new DateOnly(2026, 1, 15) });

        Assert.Equal(
            new DateOnly(2026, 1, 15),
            result.Checklist!.Tasks.First(task => task.Item.Id == item.Id).CompletedOn);
    }

    // Ticking twice is one completion, not two — otherwise the progress count
    // overtakes the number of tasks.
    [SkippableFact]
    public async Task CompletingATaskTwiceLeavesOneCompletion()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Emeka");

        ChecklistItemDto item = await SeededAsync(provider, "HR orientation session");

        await Checklist(provider).CompleteAsync(
            employeeId, item.Id, new ChecklistCompletionRequest());

        ChecklistResult again = await Checklist(provider).CompleteAsync(
            employeeId, item.Id, new ChecklistCompletionRequest { Note = "Second pass." });

        Assert.True(again.Succeeded);
        Assert.Equal(1, again.Checklist!.Hire.Progress.Completed);

        // And the later note wins, rather than the first being frozen.
        Assert.Equal(
            "Second pass.",
            again.Checklist.Tasks.First(task => task.Item.Id == item.Id).Note);
    }

    [SkippableFact]
    public async Task UnticksATaskMarkedDoneByMistake()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Emeka");

        ChecklistItemDto item = await SeededAsync(provider, "HR orientation session");

        await Checklist(provider).CompleteAsync(
            employeeId, item.Id, new ChecklistCompletionRequest());

        ChecklistResult cleared = await Checklist(provider).ClearAsync(employeeId, item.Id);

        Assert.True(cleared.Succeeded);
        Assert.Equal(0, cleared.Checklist!.Hire.Progress.Completed);
    }

    [SkippableFact]
    public async Task WillNotCompleteATaskForSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        ChecklistItemDto item = await SeededAsync(provider, "HR orientation session");

        Assert.Equal(
            ChecklistOutcome.EmployeeNotFound,
            (await Checklist(provider).CompleteAsync(
                Guid.NewGuid(), item.Id, new ChecklistCompletionRequest())).Outcome);
    }

    [SkippableFact]
    public async Task EveryRequiredTaskDoneCompletesTheChecklist()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Emeka");

        IReadOnlyList<ChecklistItemDto> items = await Checklist(provider).ListItemsAsync();

        foreach (ChecklistItemDto item in items.Where(row => row.IsRequired))
        {
            await Checklist(provider).CompleteAsync(
                employeeId, item.Id, new ChecklistCompletionRequest());
        }

        EmployeeChecklistDto checklist = (await Checklist(provider).ForEmployeeAsync(employeeId))!;

        Assert.Equal(ChecklistProgressState.Completed, checklist.Hire.Progress.State);

        // The optional 30-day check-in is still outstanding, and reported.
        Assert.Equal(0, checklist.Hire.Progress.RequiredOutstanding);
        Assert.Equal(items.Count - 1, checklist.Hire.Progress.Completed);
    }

    // ── the new hire list ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task ListsStartersWithTheirProgress()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid employeeId = await HireAsync(provider, "Emeka", new DateOnly(2026, 1, 15));

        ChecklistItemDto item = await SeededAsync(provider, "HR orientation session");

        await Checklist(provider).CompleteAsync(
            employeeId, item.Id, new ChecklistCompletionRequest());

        ChecklistHireDto hire = Assert.Single(
            (await Checklist(provider).HiresAsync(new ChecklistHireQuery())).Items);

        Assert.Equal("Emeka Okafor", hire.EmployeeName);
        Assert.Equal("Engineering", hire.DepartmentName);
        Assert.Equal("Mobile Engineer", hire.JobTitle);
        Assert.Equal(new DateOnly(2026, 1, 15), hire.StartDate);
        Assert.Equal(1, hire.Progress.Completed);

        // Tasks that fell due before today and were not done.
        Assert.Equal(ChecklistProgressState.Overdue, hire.Progress.State);
    }

    // Explicit rather than hidden: a company that has just imported three
    // hundred existing staff has three hundred untouched checklists, and
    // whether those belong on an onboarding screen is their call.
    [SkippableFact]
    public async Task LeavesOutSomebodyWhoStartedLongBeforeTheWindow()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Emeka", new DateOnly(2026, 1, 15));
        await HireAsync(provider, "Yetunde", new DateOnly(2019, 3, 1));

        PagedResult<ChecklistHireDto> recent = await Checklist(provider)
            .HiresAsync(new ChecklistHireQuery());

        Assert.Equal(["Emeka Okafor"], recent.Items.Select(hire => hire.EmployeeName));

        // And asking for everybody returns them both.
        PagedResult<ChecklistHireDto> everybody = await Checklist(provider)
            .HiresAsync(new ChecklistHireQuery { StartedWithinDays = null });

        Assert.Equal(2, everybody.TotalItems);
    }

    [SkippableFact]
    public async Task FiltersTheListByProgress()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid finished = await HireAsync(provider, "Aisha", new DateOnly(2026, 1, 20));
        await HireAsync(provider, "Emeka", new DateOnly(2026, 1, 15));

        foreach (ChecklistItemDto item in
            (await Checklist(provider).ListItemsAsync()).Where(row => row.IsRequired))
        {
            await Checklist(provider).CompleteAsync(
                finished, item.Id, new ChecklistCompletionRequest());
        }

        PagedResult<ChecklistHireDto> done = await Checklist(provider).HiresAsync(
            new ChecklistHireQuery { State = ChecklistProgressState.Completed });

        Assert.Equal(["Aisha Okafor"], done.Items.Select(hire => hire.EmployeeName));
    }

    [SkippableFact]
    public async Task SearchesTheListByName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Emeka", new DateOnly(2026, 1, 15));
        await HireAsync(provider, "Aisha", new DateOnly(2026, 1, 20));

        PagedResult<ChecklistHireDto> found = await Checklist(provider)
            .HiresAsync(new ChecklistHireQuery { Search = "aisha" });

        Assert.Equal(["Aisha Okafor"], found.Items.Select(hire => hire.EmployeeName));
    }

    [SkippableFact]
    public async Task LeavesOutSomebodyWhoHasLeft()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid leaverId = await HireAsync(provider, "Emeka", new DateOnly(2026, 1, 15));

        await using (MoteeDbContext update = fixture.CreateContext(_tenantId))
        {
            Employee leaver = await update.Employees.FirstAsync(row => row.Id == leaverId);

            leaver.Status = EmployeeStatus.Inactive;

            await update.SaveChangesAsync();
        }

        Assert.Empty((await Checklist(provider).HiresAsync(new ChecklistHireQuery())).Items);
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersTemplate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await AddedAsync(provider, Task(title: "Order a security pass"));

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

        IReadOnlyList<ChecklistItemDto> theirs = await Checklist(provider).ListItemsAsync();

        Assert.DoesNotContain(theirs, item => item.Title == "Order a security pass");

        // Their own template is there, seeded independently.
        Assert.Equal(ChecklistSeeds.All.Count, theirs.Count);
    }
}
