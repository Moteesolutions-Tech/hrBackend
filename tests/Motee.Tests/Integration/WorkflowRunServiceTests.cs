using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Workflows;
using Motee.Domain.Authorization;
using Motee.Domain.Employees;
using Motee.Domain.Identity;
using Motee.Domain.Organisation;
using Motee.Domain.Workflows;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Workflows that are running.
//
// The things worth proving here are the ones the client gets wrong. A draft
// does not fire. Blocked and overdue are worked out on read rather than written
// into the rows, so a task unblocks the moment what it waited on is settled and
// a run left overnight reads correctly in the morning. Work that does not apply
// to this hire is settled from the start, so nothing waits on it forever. And
// signing off work nobody handed in is refused.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class WorkflowRunServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly Today = new(2026, 6, 15);

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _adaId;
    private Guid _bolaId;
    private Guid _adaUserId;
    private Guid _hrAdminLevelId;
    private Guid _financeLevelId;

    // ── starting one ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task StartsARunAndTurnsTheTemplateIntoRealDates()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        RunResult result = await Runs(provider).StartAsync(Starting(workflowId));

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Reason}");

        RunDto run = result.Run!;

        Assert.Equal(RunState.Active, run.Run.State);
        Assert.Equal(Today, run.Run.AnchorOn);
        Assert.Equal("Ada Okafor", run.Run.SubjectName);

        // The day offset on the template becomes a date measured from the
        // run's anchor.
        Assert.Equal(Today.AddDays(1), run.Tasks[0].DueOn);
        Assert.Equal(Today.AddDays(5), run.Tasks[1].DueOn);

        // And the function becomes a person.
        Assert.Equal("Ada Okafor", run.Tasks[0].AssigneeName);
        Assert.Equal(_adaId, run.Tasks[0].AssigneeEmployeeId);
    }

    // The status exists so a half-written workflow is not live. The client's
    // trigger accepts anything that is not archived.
    [SkippableFact]
    public async Task ADraftDoesNotFire()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await WorkflowAsync(provider, status: WorkflowStatus.Draft);

        RunResult result = await Runs(provider).StartAsync(Starting(workflowId));

        Assert.Equal(WorkflowRefusal.NotRunnable, result.Refusal);
        Assert.Equal("Only an active workflow can be started.", result.Reason);
    }

    [SkippableFact]
    public async Task AnArchivedWorkflowDoesNotFire()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await WorkflowAsync(provider, status: WorkflowStatus.Archived);

        Assert.Equal(
            WorkflowRefusal.NotRunnable,
            (await Runs(provider).StartAsync(Starting(workflowId))).Refusal);
    }

    // The effective date is stored and read by nothing on the client, so a
    // version written for next quarter fires today.
    [SkippableFact]
    public async Task AWorkflowDoesNotFireBeforeItTakesEffect()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await WorkflowAsync(
            provider, status: WorkflowStatus.Active, effectiveFrom: Today.AddMonths(1));

        RunResult result = await Runs(provider).StartAsync(Starting(workflowId));

        Assert.Equal(WorkflowRefusal.NotRunnable, result.Refusal);
        Assert.Contains("does not take effect until", result.Reason);
    }

    // A trigger that fires twice for the same hire should not duplicate
    // somebody's work.
    [SkippableFact]
    public async Task StartingTheSameRunTwiceHandsBackTheOneAlreadyThere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        RunResult first = await Runs(provider).StartAsync(Starting(workflowId));
        RunResult again = await Runs(provider).StartAsync(Starting(workflowId));

        Assert.True(again.Succeeded, $"{again.Outcome} / {again.Reason}");
        Assert.Equal(first.Run!.Run.Id, again.Run!.Run.Id);

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        Assert.Single(await read.WorkflowRuns.ToListAsync());
    }

    [SkippableFact]
    public async Task ARunCapturesTheVersionItWasPlannedAgainst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        Assert.Equal(1, run.Run.WorkflowVersion);
        Assert.Equal("Contractor onboarding", run.Run.WorkflowTitle);
    }

    // ── conditional work ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task WorkThatDoesNotApplyIsSettledFromTheStart()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await ConditionalAsync(provider);

        // An office-based hire, so the shipping step does not apply.
        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        RunTaskDto shipping = run.Tasks.First(task => task.Title == "Ship the laptop");

        Assert.Equal(RunTaskState.Skipped, shipping.State);
        Assert.Equal(RunTaskStatus.Skipped, shipping.Status);

        // And the step waiting on it is free rather than stuck forever.
        RunTaskDto next = run.Tasks.First(task => task.Title == "Confirm it arrived");

        Assert.Equal(RunTaskStatus.NotStarted, next.Status);
        Assert.Empty(next.WaitingOn);
    }

    [SkippableFact]
    public async Task WorkThatAppliesToThisHireIsThereToDo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await ConditionalAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(
            Starting(workflowId) with { Context = [WorkflowCondition.RemoteWorker] }))
            .Run!;

        RunTaskDto shipping = run.Tasks.First(task => task.Title == "Ship the laptop");

        Assert.Equal(RunTaskState.NotStarted, shipping.State);

        // Now the step behind it waits.
        Assert.Equal(
            ["Ship the laptop"],
            run.Tasks.First(task => task.Title == "Confirm it arrived").WaitingOn);
    }

    // ── blocked, which is never stored ──────────────────────────────────────

    [SkippableFact]
    public async Task WorkWaitingOnSomethingElseCannotBeStarted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (RunDto run, Guid first, Guid second) = await ChainAsync(provider);

        Assert.Equal(
            RunTaskStatus.Blocked,
            run.Tasks.First(task => task.Id == second).Status);

        RunResult refused = await Runs(provider).StartTaskAsync(run.Run.Id, second);

        Assert.False(refused.Succeeded);
        Assert.Contains("waiting on", refused.Reason);

        // The screen is told, so it can withhold the button.
        Assert.DoesNotContain(
            RunTaskAction.Start, run.Tasks.First(task => task.Id == second).Available);

        Assert.Contains(
            RunTaskAction.Start, run.Tasks.First(task => task.Id == first).Available);
    }

    [SkippableFact]
    public async Task WorkUnblocksTheMomentWhatItWaitedOnIsDone()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (RunDto run, Guid first, Guid second) = await ChainAsync(provider);

        RunResult done = await Runs(provider).SubmitTaskAsync(run.Run.Id, first, null);

        Assert.True(done.Succeeded, $"{done.Outcome} / {done.Reason}");

        // Nothing wrote a cascade: the status is read off the other rows.
        Assert.Equal(
            RunTaskStatus.NotStarted,
            done.Run!.Tasks.First(task => task.Id == second).Status);

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        RunTask stored = await read.RunTasks.FirstAsync(task => task.Id == second);

        // And the row itself was never touched.
        Assert.Equal(RunTaskState.NotStarted, stored.State);
    }

    [SkippableFact]
    public async Task SkippingWorkAlsoReleasesWhatWaitedOnIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (RunDto run, Guid first, Guid second) = await ChainAsync(provider);

        RunResult skipped = await Runs(provider).SkipTaskAsync(
            run.Run.Id, first, "Not needed for this hire.");

        Assert.True(skipped.Succeeded, $"{skipped.Outcome} / {skipped.Reason}");

        Assert.Equal(
            RunTaskStatus.NotStarted,
            skipped.Run!.Tasks.First(task => task.Id == second).Status);
    }

    // ── overdue, which is never stored either ───────────────────────────────

    [SkippableFact]
    public async Task WorkGoesOverdueOnItsOwnAsTheDaysPass()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        Assert.Equal(RunTaskStatus.NotStarted, run.Tasks[0].Status);

        // Nothing is written. The same rows read differently a week later.
        _clock.Advance(TimeSpan.FromDays(4));

        RunDto later = (await Runs(provider).GetAsync(run.Run.Id))!;

        Assert.Equal(RunTaskStatus.Overdue, later.Tasks[0].Status);
        Assert.Equal(3, later.Tasks[0].DaysOverdue);
        Assert.Equal(1, later.Run.Tally.Overdue);
    }

    [SkippableFact]
    public async Task WorkEscalatesOnceItIsLateEnough()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        _clock.Advance(TimeSpan.FromDays(3));

        Assert.False((await Runs(provider).GetAsync(run.Run.Id))!.Tasks[0].HasEscalated);

        _clock.Advance(TimeSpan.FromDays(2));

        Assert.True((await Runs(provider).GetAsync(run.Run.Id))!.Tasks[0].HasEscalated);
    }

    // ── handing work in ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task WorkWithNoReviewerCompletesOutright()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        RunResult done = await Runs(provider).SubmitTaskAsync(
            run.Run.Id, run.Tasks[0].Id, "Filed.");

        RunTaskDto task = done.Run!.Tasks[0];

        // Making somebody approve their own work is how a checklist becomes
        // theatre.
        Assert.Equal(RunTaskState.Completed, task.State);
        Assert.Equal(_adaUserId, task.CompletedByUserId);
        Assert.Equal("Filed.", task.Note);
    }

    [SkippableFact]
    public async Task WorkWithAReviewerWaitsForTheirSignOff()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await ReviewedAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        RunResult submitted = await Runs(provider).SubmitTaskAsync(
            run.Run.Id, run.Tasks[0].Id, null);

        RunTaskDto task = submitted.Run!.Tasks[0];

        Assert.Equal(RunTaskState.AwaitingApproval, task.State);
        Assert.NotNull(task.SubmittedAt);
        Assert.Null(task.CompletedAt);
        Assert.Equal("Bola Okafor", task.ReviewerName);
    }

    // The client guards only against approving something already completed, so
    // a reviewer can sign off work nobody started.
    [SkippableFact]
    public async Task WorkCannotBeSignedOffBeforeItIsHandedIn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await ReviewedAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        RunResult refused = await Runs(provider).ApproveTaskAsync(
            run.Run.Id, run.Tasks[0].Id, null);

        Assert.False(refused.Succeeded);
        Assert.Equal(
            "This has not been handed in yet, so there is nothing to sign off.",
            refused.Reason);
    }

    [SkippableFact]
    public async Task SendingWorkBackPutsItWithTheDoerAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await ReviewedAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        await Runs(provider).SubmitTaskAsync(run.Run.Id, run.Tasks[0].Id, null);

        RunResult back = await Runs(provider).RejectTaskAsync(
            run.Run.Id, run.Tasks[0].Id, "The dates do not match the contract.");

        RunTaskDto task = back.Run!.Tasks[0];

        Assert.Equal(RunTaskState.InProgress, task.State);

        // Cleared, so it does not read as still waiting on a reviewer.
        Assert.Null(task.SubmittedAt);
        Assert.Equal("The dates do not match the contract.", task.Note);
    }

    [SkippableFact]
    public async Task FinishedWorkIsNotReopened()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        await Runs(provider).SubmitTaskAsync(run.Run.Id, run.Tasks[0].Id, null);

        RunResult again = await Runs(provider).SubmitTaskAsync(
            run.Run.Id, run.Tasks[0].Id, null);

        Assert.False(again.Succeeded);
        Assert.Contains("already been signed off", again.Reason);
    }

    // ── moving work ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task MovingWorkToAnotherDeskSaysWhereItCameFrom()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        RunResult moved = await Runs(provider).ReassignTaskAsync(
            run.Run.Id, run.Tasks[0].Id, _bolaId);

        RunTaskDto task = moved.Run!.Tasks[0];

        Assert.Equal(_bolaId, task.AssigneeEmployeeId);
        Assert.Equal("Bola Okafor", task.AssigneeName);

        // Visible as a change rather than looking like the template always said
        // so.
        Assert.Equal("Ada Okafor", task.ReassignedFrom);
        Assert.Equal(AssigneeKind.Employee, task.AssigneeKind);
    }

    [SkippableFact]
    public async Task RefusesToMoveWorkToSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        Assert.Equal(
            WorkflowOutcome.EmployeeNotFound,
            (await Runs(provider).ReassignTaskAsync(
                run.Run.Id, run.Tasks[0].Id, Guid.NewGuid())).Outcome);
    }

    // ── finishing and cancelling ────────────────────────────────────────────

    // Derived, not written by a cascade.
    [SkippableFact]
    public async Task ARunFinishesWhenTheLastPieceOfWorkIsSettled()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (RunDto run, Guid first, Guid second) = await ChainAsync(provider);

        await Runs(provider).SubmitTaskAsync(run.Run.Id, first, null);

        RunResult finished = await Runs(provider).SubmitTaskAsync(run.Run.Id, second, null);

        Assert.Equal(RunState.Completed, finished.Run!.Run.State);
        Assert.Equal(100, finished.Run.Run.Tally.PercentComplete);
    }

    [SkippableFact]
    public async Task CancellingARunStopsTheWorkOnIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        RunResult cancelled = await Runs(provider).CancelAsync(run.Run.Id);

        Assert.Equal(RunState.Cancelled, cancelled.Run!.Run.State);

        // Nothing left to do on it, so no buttons are offered.
        Assert.All(cancelled.Run.Tasks, task => Assert.Empty(task.Available));

        RunResult refused = await Runs(provider).SubmitTaskAsync(
            run.Run.Id, run.Tasks[0].Id, null);

        Assert.Equal("This run was cancelled.", refused.Reason);
    }

    [SkippableFact]
    public async Task ARunCancelledOnceIsNotCancelledTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        await Runs(provider).CancelAsync(run.Run.Id);

        Assert.Contains(
            "already been cancelled", (await Runs(provider).CancelAsync(run.Run.Id)).Reason);
    }

    // ── one person's work ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task GathersEverythingOnePersonOwes()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await ReviewedAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        MyWorkDto mine = await Runs(provider).MyWorkAsync();

        // Ada holds HR Admin, so both steps are hers.
        Assert.Equal(_adaId, mine.EmployeeId);
        Assert.Equal(
            ["Collect the signed contract", "Set up payroll"],
            mine.Todo.Select(item => item.Title));

        Assert.Empty(mine.Review);

        await Runs(provider).SubmitTaskAsync(run.Run.Id, run.Tasks[0].Id, null);

        // Handed in, so it leaves her list and lands on the reviewer's. She can
        // still see it waiting, which is not the same as being told to do it.
        MyWorkDto after = await Runs(provider).MyWorkAsync();

        Assert.Equal(["Set up payroll"], after.Todo.Select(item => item.Title));
        Assert.Equal(
            ["Collect the signed contract"], after.Others.Select(item => item.Title));

        ActAs(provider, await UserForAsync(_bolaId));

        MyWorkDto theirs = await Runs(provider).MyWorkAsync();

        Assert.Equal(
            ["Collect the signed contract"], theirs.Review.Select(item => item.Title));

        // Bola is not doing the payroll step, but the run is waiting on it —
        // which is what the third list is for.
        Assert.Equal(["Set up payroll"], theirs.Others.Select(item => item.Title));
    }

    [SkippableFact]
    public async Task TheMostUrgentThingComesFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await PrioritisedAsync(provider);

        await Runs(provider).StartAsync(Starting(workflowId));

        _clock.Advance(TimeSpan.FromDays(10));

        MyWorkDto mine = await Runs(provider).MyWorkAsync();

        // Overdue first, then the most urgent: the top of the list should
        // always be the thing to do next.
        Assert.Equal(
            ["Late and critical", "Late and low", "Not due yet"],
            mine.Todo.Select(item => item.Title));
    }

    [SkippableFact]
    public async Task AnAccountWithNoEmployeeRecordOwesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        await Runs(provider).StartAsync(Starting(workflowId));

        // The owner's account, which has no employee record behind it.
        ActAs(provider, Guid.NewGuid());

        MyWorkDto mine = await Runs(provider).MyWorkAsync();

        Assert.Null(mine.EmployeeId);
        Assert.Empty(mine.Todo);
    }

    // ── the list ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ListsRunsByWhetherTheyAreStillGoing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        RunDto running = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        RunDto finished = (await Runs(provider).StartAsync(
            Starting(workflowId) with { SubjectId = _bolaId, SubjectName = "Bola Okafor" }))
            .Run!;

        await Runs(provider).SubmitTaskAsync(finished.Run.Id, finished.Tasks[0].Id, null);
        await Runs(provider).SubmitTaskAsync(finished.Run.Id, finished.Tasks[1].Id, null);

        Assert.Equal(
            [running.Run.Id],
            (await Runs(provider).ListAsync(new RunQuery { State = RunState.Active }))
                .Items.Select(row => row.Id));

        Assert.Equal(
            [finished.Run.Id],
            (await Runs(provider).ListAsync(new RunQuery { State = RunState.Completed }))
                .Items.Select(row => row.Id));
    }

    [SkippableFact]
    public async Task ARunKnowsWhatIsDueNext()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        Assert.Equal(Today.AddDays(1), run.Run.NextDueOn);

        RunResult done = await Runs(provider).SubmitTaskAsync(
            run.Run.Id, run.Tasks[0].Id, null);

        Assert.Equal(Today.AddDays(5), done.Run!.Run.NextDueOn);
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersRuns()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid workflowId = await LiveAsync(provider);

        await Runs(provider).StartAsync(Starting(workflowId));

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

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId =
            other.TenantId;

        Assert.Empty((await Runs(provider).ListAsync(new RunQuery())).Items);
    }

    // ── arrange ─────────────────────────────────────────────────────────────

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

        _adaId = await HireAsync(provider, "Ada");
        _bolaId = await HireAsync(provider, "Bola");

        _hrAdminLevelId = await LevelAsync(Role.HrAdmin);
        _financeLevelId = await LevelAsync(Role.Finance);

        // Ada does the work, Bola reviews it.
        _adaUserId = await AccountAsync(_adaId, "ada", _hrAdminLevelId);
        await AccountAsync(_bolaId, "bola", _financeLevelId);

        ActAs(provider, _adaUserId);

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IWorkflowRunService Runs(ServiceProvider provider) =>
        Resolve<IWorkflowRunService>(provider);

    private static IWorkflowService Workflows(ServiceProvider provider) =>
        Resolve<IWorkflowService>(provider);

    private static void ActAs(ServiceProvider provider, Guid userId) =>
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

    private StartRunRequest Starting(Guid workflowId) => new()
    {
        WorkflowId = workflowId,
        SubjectKind = RunSubjectKind.Employee,
        SubjectId = _adaId,
    };

    private async Task<Guid> HireAsync(ServiceProvider provider, string first) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    private async Task<Guid> AccountAsync(Guid employeeId, string handle, Guid accessLevelId)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid userId = Guid.NewGuid();

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = userId,
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = char.ToUpperInvariant(handle[0]) + handle[1..],
            LastName = "Okafor",
            Email = $"{handle}-login@acme.com",
            NormalizedEmail = $"{handle.ToUpperInvariant()}-LOGIN@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        seed.UserAccessLevels.Add(new UserAccessLevel
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            UserId = userId,
            AccessLevelId = accessLevelId,
            AssignedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        return userId;
    }

    private async Task<Guid> UserForAsync(Guid employeeId)
    {
        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        return await read.Users
            .IgnoreQueryFilters()
            .Where(user => user.EmployeeId == employeeId && user.TenantId == _tenantId)
            .Select(user => user.Id)
            .FirstAsync();
    }

    private async Task<Guid> LevelAsync(Role role)
    {
        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        return await read.AccessLevels
            .Where(level => level.TemplateSlug == Roles.ToSlug(role))
            .Select(level => level.Id)
            .FirstAsync();
    }

    // Two steps, the second waiting on the first.
    private async Task<Guid> WorkflowAsync(
        ServiceProvider provider,
        WorkflowStatus status = WorkflowStatus.Active,
        DateOnly? effectiveFrom = null,
        bool reviewed = false)
    {
        WorkflowResult result = await Workflows(provider).CreateAsync(new WorkflowRequest
        {
            Title = "Contractor onboarding",
            TriggerMode = WorkflowTriggerMode.Manual,
            ScopeKind = WorkflowScopeKind.All,
            Status = status,
            EffectiveFrom = effectiveFrom,
            Tasks =
            [
                new WorkflowTaskRequest
                {
                    Title = "Collect the signed contract",
                    AssigneeKind = AssigneeKind.AccessLevel,
                    AssigneeAccessLevelId = _hrAdminLevelId,
                    HasReviewer = reviewed,
                    ReviewerAccessLevelId = reviewed ? _financeLevelId : null,
                    DueDayOffset = 1,
                    EscalateAfterDays = 3,
                    Priority = WorkflowTaskPriority.High,
                },
                new WorkflowTaskRequest
                {
                    Title = "Set up payroll",
                    AssigneeKind = AssigneeKind.AccessLevel,
                    AssigneeAccessLevelId = _hrAdminLevelId,
                    DueDayOffset = 5,
                    Priority = WorkflowTaskPriority.Normal,
                },
            ],
        });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Workflow!.Workflow.Id;
    }

    private Task<Guid> LiveAsync(ServiceProvider provider) =>
        WorkflowAsync(provider, WorkflowStatus.Active);

    private Task<Guid> ReviewedAsync(ServiceProvider provider) =>
        WorkflowAsync(provider, WorkflowStatus.Active, reviewed: true);

    private async Task<(RunDto Run, Guid First, Guid Second)> ChainAsync(
        ServiceProvider provider)
    {
        Guid workflowId = await ChainedAsync(provider);

        RunDto run = (await Runs(provider).StartAsync(Starting(workflowId))).Run!;

        return (run, run.Tasks[0].Id, run.Tasks[1].Id);
    }

    private async Task<Guid> ChainedAsync(ServiceProvider provider)
    {
        WorkflowResult result = await Workflows(provider).CreateAsync(new WorkflowRequest
        {
            Title = "Account setup",
            TriggerMode = WorkflowTriggerMode.Manual,
            ScopeKind = WorkflowScopeKind.All,
            Status = WorkflowStatus.Active,
            Tasks =
            [
                Step("Approve the record", _hrAdminLevelId, dueIn: 1),
                Step("Activate the account", _hrAdminLevelId, dueIn: 3, dependsOn: [0]),
            ],
        });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Workflow!.Workflow.Id;
    }

    private async Task<Guid> ConditionalAsync(ServiceProvider provider)
    {
        WorkflowResult result = await Workflows(provider).CreateAsync(new WorkflowRequest
        {
            Title = "Remote setup",
            TriggerMode = WorkflowTriggerMode.Manual,
            ScopeKind = WorkflowScopeKind.All,
            Status = WorkflowStatus.Active,
            Tasks =
            [
                Step("Ship the laptop", _hrAdminLevelId, dueIn: 2)
                    with { Condition = WorkflowCondition.RemoteWorker },
                Step("Confirm it arrived", _hrAdminLevelId, dueIn: 4, dependsOn: [0]),
            ],
        });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Workflow!.Workflow.Id;
    }

    private async Task<Guid> PrioritisedAsync(ServiceProvider provider)
    {
        WorkflowResult result = await Workflows(provider).CreateAsync(new WorkflowRequest
        {
            Title = "A mixed bag",
            TriggerMode = WorkflowTriggerMode.Manual,
            ScopeKind = WorkflowScopeKind.All,
            Status = WorkflowStatus.Active,
            Tasks =
            [
                Step("Late and low", _hrAdminLevelId, dueIn: 2)
                    with { Priority = WorkflowTaskPriority.Low },
                Step("Not due yet", _hrAdminLevelId, dueIn: 40)
                    with { Priority = WorkflowTaskPriority.Critical },
                Step("Late and critical", _hrAdminLevelId, dueIn: 1)
                    with { Priority = WorkflowTaskPriority.Critical },
            ],
        });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Workflow!.Workflow.Id;
    }

    private static WorkflowTaskRequest Step(
        string title,
        Guid accessLevelId,
        int dueIn = 1,
        IReadOnlyList<int>? dependsOn = null) => new()
        {
            Title = title,
            AssigneeKind = AssigneeKind.AccessLevel,
            AssigneeAccessLevelId = accessLevelId,
            DueDayOffset = dueIn,
            Priority = WorkflowTaskPriority.Normal,
            DependsOnPositions = dependsOn,
        };
}
