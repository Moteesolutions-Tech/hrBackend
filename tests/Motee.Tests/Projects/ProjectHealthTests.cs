using Motee.Domain.Projects;

namespace Motee.Tests.Projects;

// The three derived views the detail screen leads with: a traffic light per
// area, the alert when the critical path is stuck, and what is due next.
//
// Today is passed in rather than read from the machine. The client takes it
// from the browser, so a project is green or red depending on where the person
// looking at it happens to be sitting.
//
// Written before the health check.
public class ProjectHealthTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    private static ProjectTask Task(
        string name = "Task",
        ProjectTaskStatus status = ProjectTaskStatus.NotStarted,
        int endDay = 30,
        int startDay = 0,
        Guid? assignee = null,
        ProjectPriority? priority = null,
        params Guid[] dependsOn) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Status = status,
            StartOn = Today.AddDays(startDay),
            EndOn = Today.AddDays(endDay),
            AssigneeEmployeeId = assignee,
            Priority = priority,
            DependsOn = [.. dependsOn],
        };

    private static ProjectMilestone Milestone(
        string name = "UAT sign-off",
        MilestoneStatus status = MilestoneStatus.NotStarted,
        int targetDay = 30,
        Guid? responsible = null,
        params Guid[] taskIds) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Status = status,
            TargetOn = Today.AddDays(targetDay),
            ResponsibleEmployeeId = responsible,
            TaskIds = [.. taskIds],
        };

    private static ProjectHealth HealthOf(
        IReadOnlyList<ProjectTask> tasks,
        IReadOnlyList<ProjectMilestone>? milestones = null,
        decimal? budget = 1_000m,
        decimal forecast = 0m,
        decimal worstOverAllocation = 0m) =>
        ProjectHealthCheck.Of(
            tasks, milestones ?? [], budget, forecast, worstOverAllocation, Today);

    // ── schedule ────────────────────────────────────────────────────────────

    [Fact]
    public void AProjectWithNothingOverdueIsGreen()
    {
        Assert.Equal(HealthStatus.Green, HealthOf([Task()]).Schedule);
    }

    [Fact]
    public void AMissedMilestoneIsRed()
    {
        Assert.Equal(
            HealthStatus.Red,
            HealthOf([Task()], [Milestone(targetDay: -1)]).Schedule);
    }

    [Fact]
    public void AMilestoneAlreadyReachedIsNotOverdue()
    {
        Assert.Equal(
            HealthStatus.Green,
            HealthOf(
                [Task()],
                [Milestone(status: MilestoneStatus.Completed, targetDay: -1)]).Schedule);
    }

    // Dates that contradict the dependency arrows are a planning error, not a
    // slipping date — it is the same severity as a missed milestone because
    // the plan cannot be delivered as drawn.
    [Fact]
    public void DatesThatContradictTheDependenciesAreRed()
    {
        ProjectTask first = Task("Discovery", endDay: 20);
        ProjectTask second = Task("Build", startDay: 10, endDay: 40, dependsOn: first.Id);

        Assert.Equal(HealthStatus.Red, HealthOf([first, second]).Schedule);
    }

    [Fact]
    public void AnOverdueTaskOnItsOwnIsAmber()
    {
        Assert.Equal(HealthStatus.Amber, HealthOf([Task(endDay: -1)]).Schedule);
    }

    [Theory]
    [InlineData(ProjectTaskStatus.Completed)]
    [InlineData(ProjectTaskStatus.Cancelled)]
    public void AFinishedOrDroppedTaskIsNeverOverdue(ProjectTaskStatus status)
    {
        Assert.Equal(HealthStatus.Green, HealthOf([Task(status: status, endDay: -1)]).Schedule);
    }

    [Fact]
    public void ATaskDueTodayIsNotYetOverdue()
    {
        Assert.Equal(HealthStatus.Green, HealthOf([Task(endDay: 0)]).Schedule);
    }

    // ── budget ──────────────────────────────────────────────────────────────

    [Fact]
    public void WithoutABudgetThereIsNothingToJudge()
    {
        // Unknown rather than green: no budget is not the same as on budget.
        Assert.Equal(HealthStatus.Unknown, HealthOf([Task()], budget: null).Budget);
    }

    [Fact]
    public void AForecastPastTheBudgetIsRed()
    {
        Assert.Equal(
            HealthStatus.Red,
            HealthOf([Task()], budget: 1_000m, forecast: 1_001m).Budget);
    }

    [Fact]
    public void AForecastCloseToTheBudgetIsAmber()
    {
        Assert.Equal(
            HealthStatus.Amber,
            HealthOf([Task()], budget: 1_000m, forecast: 900m).Budget);
    }

    [Fact]
    public void AForecastWellWithinTheBudgetIsGreen()
    {
        Assert.Equal(
            HealthStatus.Green,
            HealthOf([Task()], budget: 1_000m, forecast: 500m).Budget);
    }

    // ── resources ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, HealthStatus.Green)]
    [InlineData(100, HealthStatus.Green)]
    [InlineData(110, HealthStatus.Amber)]
    [InlineData(130, HealthStatus.Red)]
    public void ResourceHealthFollowsTheWorstCommitmentOnTheTeam(
        int worst, HealthStatus expected)
    {
        Assert.Equal(
            expected,
            HealthOf([Task()], worstOverAllocation: worst).Resources);
    }

    // ── the critical path ───────────────────────────────────────────────────

    [Fact]
    public void ABlockedCriticalTaskIsRed()
    {
        ProjectTask first = Task("Discovery", endDay: 40);
        ProjectTask second = Task("Build", startDay: 40, endDay: 80, dependsOn: first.Id);

        Assert.Equal(HealthStatus.Red, HealthOf([first, second]).CriticalPath);
    }

    [Fact]
    public void AnOverdueCriticalTaskIsAmber()
    {
        Assert.Equal(HealthStatus.Amber, HealthOf([Task(endDay: -1)]).CriticalPath);
    }

    [Fact]
    public void AnOnTrackCriticalPathIsGreen()
    {
        Assert.Equal(HealthStatus.Green, HealthOf([Task()]).CriticalPath);
    }

    // ── overall ─────────────────────────────────────────────────────────────

    // Nothing tracks scope creep yet: change requests are not built. Left
    // honest rather than reported green.
    [Fact]
    public void ScopeIsNotSomethingThisKnowsAbout()
    {
        Assert.Equal(HealthStatus.Unknown, HealthOf([Task()]).Scope);
    }

    [Fact]
    public void OverallIsGreenWhenEveryKnownAreaIs()
    {
        // Scope is permanently unknown, so counting it would leave every
        // project in the product unable to be green.
        Assert.Equal(HealthStatus.Green, HealthOf([Task()]).Overall);
    }

    [Fact]
    public void OverallTakesTheWorstOfTheAreasItKnows()
    {
        Assert.Equal(
            HealthStatus.Amber,
            HealthOf([Task()], worstOverAllocation: 110m).Overall);

        Assert.Equal(
            HealthStatus.Red,
            HealthOf([Task()], [Milestone(targetDay: -1)], worstOverAllocation: 110m).Overall);
    }

    [Fact]
    public void AnUnknownBudgetDoesNotDragTheProjectDown()
    {
        Assert.Equal(HealthStatus.Green, HealthOf([Task()], budget: null).Overall);
    }

    // ── the critical-path alert ─────────────────────────────────────────────

    [Fact]
    public void ThereIsNoAlertWhileTheCriticalPathMoves()
    {
        Assert.Null(CriticalPathAlerts.Of([Task()], []));
    }

    [Fact]
    public void ThereIsNoAlertWithoutAnyTasks()
    {
        Assert.Null(CriticalPathAlerts.Of([], [Milestone()]));
    }

    [Fact]
    public void TheAlertNamesTheTaskHoldingEverythingUp()
    {
        Guid ada = Guid.NewGuid();
        ProjectTask first = Task("Discovery", endDay: 40);
        ProjectTask second = Task(
            "Build", startDay: 40, endDay: 80, assignee: ada, dependsOn: first.Id);

        CriticalPathAlert? alert = CriticalPathAlerts.Of([first, second], []);

        Assert.NotNull(alert);
        Assert.Equal(2, alert.TaskCount);
        Assert.Equal(second.Id, alert.BlockerTaskId);
        Assert.Equal("Build", alert.BlockerTaskName);
        Assert.Equal(ada, alert.OwnerEmployeeId);
        Assert.Equal(Today.AddDays(80), alert.DueOn);
    }

    // The milestone that actually waits on the stuck task, where one does. The
    // client reports the earliest unmet milestone whatever it depends on,
    // which names an unrelated date as the impact.
    [Fact]
    public void TheAlertPrefersTheMilestoneThatWaitsOnTheStuckTask()
    {
        ProjectTask first = Task("Discovery", endDay: 40);
        ProjectTask second = Task("Build", startDay: 40, endDay: 80, dependsOn: first.Id);

        ProjectMilestone unrelated = Milestone("Branding sign-off", targetDay: 10);
        ProjectMilestone waiting = Milestone("Go live", targetDay: 90, taskIds: second.Id);

        CriticalPathAlert? alert = CriticalPathAlerts.Of(
            [first, second], [unrelated, waiting]);

        Assert.NotNull(alert);
        Assert.Equal(waiting.Id, alert.ImpactMilestoneId);
        Assert.Equal("Go live", alert.ImpactMilestoneName);
    }

    [Fact]
    public void TheAlertFallsBackToTheNextMilestoneDue()
    {
        ProjectTask first = Task("Discovery", endDay: 40);
        ProjectTask second = Task("Build", startDay: 40, endDay: 80, dependsOn: first.Id);

        ProjectMilestone reached =
            Milestone("Kick-off", status: MilestoneStatus.Completed, targetDay: 5);
        ProjectMilestone next = Milestone("Go live", targetDay: 90);

        CriticalPathAlert? alert = CriticalPathAlerts.Of(
            [first, second], [reached, next]);

        Assert.NotNull(alert);
        Assert.Equal(next.Id, alert.ImpactMilestoneId);
    }

    [Fact]
    public void TheAlertStandsWithNoMilestoneToPointAt()
    {
        ProjectTask first = Task("Discovery", endDay: 40);
        ProjectTask second = Task("Build", startDay: 40, endDay: 80, dependsOn: first.Id);

        CriticalPathAlert? alert = CriticalPathAlerts.Of([first, second], []);

        Assert.NotNull(alert);
        Assert.Null(alert.ImpactMilestoneId);
    }

    // ── next actions ────────────────────────────────────────────────────────

    [Fact]
    public void SortsWhatIsDueIntoOverdueTodayAndThisWeek()
    {
        IReadOnlyList<NextAction> actions = NextActions.Of(
            [
                Task("Late", endDay: -2),
                Task("Due today", endDay: 0),
                Task("Due Friday", endDay: 4),
                Task("Next month", endDay: 30),
            ],
            [],
            Today);

        Assert.Equal(
            ["Late", "Due today", "Due Friday"],
            actions.Select(action => action.Label));

        Assert.Equal(
            [NextActionBucket.Overdue, NextActionBucket.Today, NextActionBucket.ThisWeek],
            actions.Select(action => action.Bucket));
    }

    [Fact]
    public void AWeekOutIsStillThisWeek()
    {
        NextAction action = Assert.Single(
            NextActions.Of([Task("Due next Monday", endDay: 7)], [], Today));

        Assert.Equal(NextActionBucket.ThisWeek, action.Bucket);
    }

    [Theory]
    [InlineData(ProjectTaskStatus.Completed)]
    [InlineData(ProjectTaskStatus.Cancelled)]
    public void FinishedAndDroppedWorkIsNotAnAction(ProjectTaskStatus status)
    {
        Assert.Empty(NextActions.Of([Task(status: status, endDay: -2)], [], Today));
    }

    [Fact]
    public void AReachedMilestoneIsNotAnAction()
    {
        Assert.Empty(
            NextActions.Of(
                [],
                [Milestone(status: MilestoneStatus.Completed, targetDay: -2)],
                Today));
    }

    [Fact]
    public void MilestonesAndTasksAreListedTogetherInDateOrder()
    {
        IReadOnlyList<NextAction> actions = NextActions.Of(
            [Task("Task Friday", endDay: 4)],
            [Milestone("Milestone Wednesday", targetDay: 2)],
            Today);

        Assert.Equal(
            ["Milestone Wednesday", "Task Friday"],
            actions.Select(action => action.Label));

        Assert.Equal(
            [NextActionKind.Milestone, NextActionKind.Task],
            actions.Select(action => action.Kind));
    }

    [Fact]
    public void AnActionCarriesWhoOwnsItAndHowUrgentItIs()
    {
        Guid ada = Guid.NewGuid();

        NextAction action = Assert.Single(
            NextActions.Of(
                [Task("Payroll mapping", endDay: 1, assignee: ada,
                    priority: ProjectPriority.Critical)],
                [],
                Today));

        Assert.Equal(ada, action.OwnerEmployeeId);
        Assert.Equal(ProjectPriority.Critical, action.Priority);
        Assert.Equal("NotStarted", action.Status);
    }
}
