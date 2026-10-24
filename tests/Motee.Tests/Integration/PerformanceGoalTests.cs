using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Performance;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Performance;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Goals, set by the person or for them.
//
// The two things worth pinning are the ones that would go wrong quietly: overdue is
// derived rather than stored, so it has to become true at midnight and stop being
// true the moment a goal is closed; and self-service must read only the caller's own
// goals however the query is dressed up.
[Collection(PostgresCollection.Name)]
public class PerformanceGoalTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _employeeId;
    private Guid _colleagueId;

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

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        Guid departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _employeeId = await HireAsync(provider, departmentId, "Bola", "bola@acme.com");
        _colleagueId = await HireAsync(provider, departmentId, "Cara", "cara@acme.com");

        Guid userId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
            {
                Id = userId,
                TenantId = _tenantId,
                EmployeeId = _employeeId,
                FirstName = "Bola",
                LastName = "Okafor",
                Email = "bola-login@acme.com",
                NormalizedEmail = "BOLA-LOGIN@ACME.COM",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

        return provider;
    }

    private static async Task<Guid> HireAsync(
        ServiceProvider provider,
        Guid departmentId,
        string first,
        string email) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
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

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static GoalRequest Goal(string title = "Ship the thing", int inDays = 30) => new()
    {
        Title = title,
        Description = "Something worth doing.",
        Category = GoalCategory.Technical,
        DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(inDays),
        GoalType = "SMART",
    };

    [SkippableFact]
    public async Task SomebodyCanSetTheirOwnGoal()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        GoalResult created = await Resolve<IPerformanceGoalService>(provider)
            .CreateMineAsync(Goal());

        Assert.True(created.Succeeded, created.Outcome.ToString());
        Assert.Equal(_employeeId, created.Goal!.EmployeeId);
        Assert.Equal(GoalStatus.OnTrack, created.Goal.Status);
        Assert.Equal(0, created.Goal.Progress);
        Assert.False(created.Goal.IsOverdue);
    }

    // The door has to stay shut even when the query asks nicely. A client passing a
    // colleague's id through the self-service route must not read their goals.
    [SkippableFact]
    public async Task SelfServiceReadsOnlyTheCallersOwnGoals()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceGoalService goals = Resolve<IPerformanceGoalService>(provider);

        await goals.CreateMineAsync(Goal("Mine"));
        await goals.CreateAsync(_colleagueId, Goal("Theirs"));

        IReadOnlyList<PerformanceGoalDto> mine =
            await goals.MineAsync(new GoalQuery { EmployeeId = _colleagueId });

        Assert.Equal("Mine", Assert.Single(mine).Title);
    }

    [SkippableFact]
    public async Task AGoalNeedsATitle()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            GoalOutcome.TitleRequired,
            (await Resolve<IPerformanceGoalService>(provider)
                .CreateMineAsync(Goal(" "))).Outcome);
    }

    // The story of how something reached sixty per cent is the half worth reading at
    // review time.
    [SkippableFact]
    public async Task CheckingInKeepsTheNoteAsWellAsTheNumber()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceGoalService goals = Resolve<IPerformanceGoalService>(provider);

        Guid id = (await goals.CreateMineAsync(Goal())).Goal!.Id;

        await goals.RecordProgressAsync(id, new ProgressUpdateRequest
        {
            Progress = 30,
            Note = "Design agreed.",
        });

        PerformanceGoalDto after = (await goals.RecordProgressAsync(id,
            new ProgressUpdateRequest { Progress = 60, Note = "Half built." })).Goal!;

        Assert.Equal(60, after.Progress);
        Assert.Equal(2, after.Updates.Count);

        // Newest first, matching how the screen reads them.
        Assert.Equal("Half built.", after.Updates[0].Note);
        Assert.Equal("Bola Okafor", after.Updates[0].ByName);
    }

    [SkippableFact]
    public async Task ProgressOutsideTheRangeIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceGoalService goals = Resolve<IPerformanceGoalService>(provider);

        Guid id = (await goals.CreateMineAsync(Goal())).Goal!.Id;

        Assert.Equal(
            GoalOutcome.ProgressOutOfRange,
            (await goals.RecordProgressAsync(id, new ProgressUpdateRequest { Progress = 140 }))
                .Outcome);
        Assert.Equal(
            GoalOutcome.ProgressOutOfRange,
            (await goals.RecordProgressAsync(id, new ProgressUpdateRequest { Progress = -5 }))
                .Outcome);
    }

    // Reaching a hundred is a claim about progress, not the decision that it is done.
    // A goal can sit there while the work is checked.
    [SkippableFact]
    public async Task ReachingAHundredDoesNotCloseIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceGoalService goals = Resolve<IPerformanceGoalService>(provider);

        Guid id = (await goals.CreateMineAsync(Goal())).Goal!.Id;

        PerformanceGoalDto after = (await goals.RecordProgressAsync(
            id, new ProgressUpdateRequest { Progress = 100 })).Goal!;

        Assert.Equal(GoalStatus.OnTrack, after.Status);
        Assert.Null(after.CompletedAt);
    }

    // And completing carries the number with it, or a finished goal shows an
    // unfinished bar.
    [SkippableFact]
    public async Task CompletingFillsTheBarAndStampsTheDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceGoalService goals = Resolve<IPerformanceGoalService>(provider);

        Guid id = (await goals.CreateMineAsync(Goal())).Goal!.Id;

        await goals.RecordProgressAsync(id, new ProgressUpdateRequest { Progress = 60 });

        PerformanceGoalDto done =
            (await goals.ChangeStatusAsync(id, GoalStatus.Completed)).Goal!;

        Assert.Equal(100, done.Progress);
        Assert.NotNull(done.CompletedAt);
    }

    // A goal that comes back is a new goal. Reopening would leave CompletedAt
    // describing a completion that no longer happened.
    [SkippableFact]
    public async Task AClosedGoalDoesNotReopen()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceGoalService goals = Resolve<IPerformanceGoalService>(provider);

        Guid id = (await goals.CreateMineAsync(Goal())).Goal!.Id;

        await goals.ChangeStatusAsync(id, GoalStatus.Cancelled);

        Assert.Equal(
            GoalOutcome.AlreadyFinal,
            (await goals.ChangeStatusAsync(id, GoalStatus.OnTrack)).Outcome);
        Assert.Equal(
            GoalOutcome.AlreadyFinal,
            (await goals.RecordProgressAsync(id, new ProgressUpdateRequest { Progress = 50 }))
                .Outcome);
    }

    // Derived, so it becomes true without anybody touching the row.
    [SkippableFact]
    public async Task APastDueGoalReadsAsOverdue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceGoalService goals = Resolve<IPerformanceGoalService>(provider);

        PerformanceGoalDto late = (await goals.CreateMineAsync(Goal("Late", inDays: -10))).Goal!;

        Assert.True(late.IsOverdue);
        Assert.Equal(GoalStatus.OnTrack, late.Status);
    }

    // And stops being true the moment it is closed, which a stored flag would get
    // wrong until something flipped it back.
    [SkippableFact]
    public async Task ClosingAPastDueGoalStopsItBeingOverdue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceGoalService goals = Resolve<IPerformanceGoalService>(provider);

        Guid id = (await goals.CreateMineAsync(Goal("Late", inDays: -10))).Goal!.Id;

        Assert.False((await goals.ChangeStatusAsync(id, GoalStatus.Completed)).Goal!.IsOverdue);
    }

    // Filtered in SQL, not after loading: "show me the overdue ones" across eight
    // hundred people must not read every goal to find forty.
    [SkippableFact]
    public async Task TheOverdueFilterFindsOnlyThePastDueUnfinishedOnes()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceGoalService goals = Resolve<IPerformanceGoalService>(provider);

        await goals.CreateMineAsync(Goal("Future", inDays: 30));
        Guid late = (await goals.CreateMineAsync(Goal("Late", inDays: -10))).Goal!.Id;
        Guid closed = (await goals.CreateMineAsync(Goal("Closed late", inDays: -20))).Goal!.Id;

        await goals.ChangeStatusAsync(closed, GoalStatus.Completed);

        PagedResult<PerformanceGoalDto> overdue =
            await goals.ListAsync(new GoalQuery { OverdueOnly = true });

        Assert.Equal(late, Assert.Single(overdue.Items).Id);
    }

    [SkippableFact]
    public async Task AManagerCanSetAGoalOnSomebodyElse()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceGoalService goals = Resolve<IPerformanceGoalService>(provider);

        GoalResult created = await goals.CreateAsync(_colleagueId, Goal("Lead the migration"));

        Assert.True(created.Succeeded, created.Outcome.ToString());
        Assert.Equal(_colleagueId, created.Goal!.EmployeeId);
        Assert.Equal("Cara Okafor", created.Goal.EmployeeName);
    }

    [SkippableFact]
    public async Task AGoalForSomebodyWhoDoesNotWorkHereIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            GoalOutcome.EmployeeNotFound,
            (await Resolve<IPerformanceGoalService>(provider)
                .CreateAsync(Guid.NewGuid(), Goal())).Outcome);
    }
}
