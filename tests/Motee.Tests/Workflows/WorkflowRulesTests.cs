using Motee.Domain.Workflows;

namespace Motee.Tests.Workflows;

// What a workflow will accept, and when it is allowed to run.
//
// Two of these guard live defects in the client. A workflow's status exists so
// that drafts are not live by accident, but the listener that starts runs
// accepts anything that is not archived — so a draft fires. And a workflow
// carries an effective date that nothing ever reads, so a version dated next
// quarter runs today.
//
// Written before the rules.
public class WorkflowRulesTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    private static readonly Guid HrAccessLevel = Guid.NewGuid();
    private static readonly Guid Ada = Guid.NewGuid();

    private static Workflow Of(
        string title = "Onboarding",
        WorkflowTriggerMode triggerMode = WorkflowTriggerMode.Manual,
        ScheduleKind? scheduleKind = null,
        DateOnly? scheduleOn = null,
        TimeOnly? scheduleAt = null,
        WorkflowTriggerEvent? triggerEvent = null,
        int? offsetValue = null,
        ScheduleOffsetUnit? offsetUnit = null,
        WorkflowScopeKind scope = WorkflowScopeKind.All,
        Guid? scopeDepartmentId = null,
        WorkflowStatus status = WorkflowStatus.Draft,
        WorkflowKind kind = WorkflowKind.Custom,
        DateOnly? effectiveFrom = null) => new()
        {
            Title = title,
            TriggerMode = triggerMode,
            ScheduleKind = scheduleKind,
            ScheduleOn = scheduleOn,
            ScheduleAt = scheduleAt,
            TriggerEvent = triggerEvent,
            OffsetValue = offsetValue,
            OffsetUnit = offsetUnit,
            ScopeKind = scope,
            ScopeDepartmentId = scopeDepartmentId,
            Status = status,
            WorkflowKind = kind,
            EffectiveFrom = effectiveFrom,
        };

    private static WorkflowTask Task(
        string title = "Collect signed contract",
        AssigneeKind assigneeKind = AssigneeKind.AccessLevel,
        Guid? assigneeAccessLevelId = null,
        Guid? assigneeEmployeeId = null,
        bool hasReviewer = false,
        Guid? reviewerAccessLevelId = null,
        int sequence = 1) => new()
        {
            Title = title,
            Sequence = sequence,
            AssigneeKind = assigneeKind,
            AssigneeAccessLevelId = assigneeKind == AssigneeKind.AccessLevel
                ? assigneeAccessLevelId ?? HrAccessLevel
                : null,
            AssigneeEmployeeId = assigneeKind == AssigneeKind.Employee
                ? assigneeEmployeeId ?? Ada
                : null,
            HasReviewer = hasReviewer,
            ReviewerAccessLevelId = reviewerAccessLevelId,
        };

    private static IReadOnlyList<WorkflowTask> OneTask() => [Task()];

    // ── the workflow ────────────────────────────────────────────────────────

    [Fact]
    public void AcceptsAWorkflow()
    {
        Assert.Equal(WorkflowRefusal.None, WorkflowRules.Refusal(Of(), OneTask()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("On")]
    public void RefusesATitleTooShortToIdentifyIt(string title)
    {
        Assert.Equal(
            WorkflowRefusal.TitleTooShort,
            WorkflowRules.Refusal(Of(title: title), OneTask()));
    }

    [Fact]
    public void RefusesADepartmentScopeWithNoDepartment()
    {
        Assert.Equal(
            WorkflowRefusal.DepartmentRequired,
            WorkflowRules.Refusal(
                Of(scope: WorkflowScopeKind.Department), OneTask()));
    }

    [Fact]
    public void AcceptsADepartmentScopeWithADepartment()
    {
        Assert.Equal(
            WorkflowRefusal.None,
            WorkflowRules.Refusal(
                Of(scope: WorkflowScopeKind.Department, scopeDepartmentId: Guid.NewGuid()),
                OneTask()));
    }

    [Fact]
    public void RefusesAWorkflowWithNoWork()
    {
        Assert.Equal(WorkflowRefusal.NoTasks, WorkflowRules.Refusal(Of(), []));
    }

    // ── when it fires ───────────────────────────────────────────────────────

    [Fact]
    public void RefusesAnAutomaticWorkflowWithNoSchedule()
    {
        Assert.Equal(
            WorkflowRefusal.ScheduleRequired,
            WorkflowRules.Refusal(
                Of(triggerMode: WorkflowTriggerMode.Automatic), OneTask()));
    }

    [Fact]
    public void RefusesAFixedScheduleWithNoDate()
    {
        Assert.Equal(
            WorkflowRefusal.ScheduleDateRequired,
            WorkflowRules.Refusal(
                Of(
                    triggerMode: WorkflowTriggerMode.Automatic,
                    scheduleKind: ScheduleKind.Fixed),
                OneTask()));
    }

    [Fact]
    public void AcceptsAFixedScheduleWithADateAndATime()
    {
        Assert.Equal(
            WorkflowRefusal.None,
            WorkflowRules.Refusal(
                Of(
                    triggerMode: WorkflowTriggerMode.Automatic,
                    scheduleKind: ScheduleKind.Fixed,
                    scheduleOn: Today.AddDays(7),
                    scheduleAt: new TimeOnly(9, 0)),
                OneTask()));
    }

    [Fact]
    public void RefusesARelativeScheduleWithNoEvent()
    {
        Assert.Equal(
            WorkflowRefusal.TriggerEventRequired,
            WorkflowRules.Refusal(
                Of(
                    triggerMode: WorkflowTriggerMode.Automatic,
                    scheduleKind: ScheduleKind.Relative,
                    offsetValue: 2,
                    offsetUnit: ScheduleOffsetUnit.Days),
                OneTask()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public void RefusesARelativeScheduleWithNoUsableOffset(int? offset)
    {
        Assert.Equal(
            WorkflowRefusal.OffsetRequired,
            WorkflowRules.Refusal(
                Of(
                    triggerMode: WorkflowTriggerMode.Automatic,
                    scheduleKind: ScheduleKind.Relative,
                    triggerEvent: WorkflowTriggerEvent.OnboardingInitiated,
                    offsetValue: offset,
                    offsetUnit: ScheduleOffsetUnit.Days),
                OneTask()));
    }

    // Nought is a legitimate offset: fire as the event lands.
    [Fact]
    public void AcceptsARelativeScheduleThatFiresImmediately()
    {
        Assert.Equal(
            WorkflowRefusal.None,
            WorkflowRules.Refusal(
                Of(
                    triggerMode: WorkflowTriggerMode.Automatic,
                    scheduleKind: ScheduleKind.Relative,
                    triggerEvent: WorkflowTriggerEvent.OnboardingInitiated,
                    offsetValue: 0,
                    offsetUnit: ScheduleOffsetUnit.Days),
                OneTask()));
    }

    // A manual workflow is started by hand, so leftover schedule fields from an
    // earlier edit are not a reason to refuse it.
    [Fact]
    public void AManualWorkflowIgnoresASchedule()
    {
        Assert.Equal(
            WorkflowRefusal.None,
            WorkflowRules.Refusal(
                Of(
                    triggerMode: WorkflowTriggerMode.Manual,
                    scheduleKind: ScheduleKind.Fixed),
                OneTask()));
    }

    // ── tasks ───────────────────────────────────────────────────────────────

    [Fact]
    public void RefusesATaskWithNoTitle()
    {
        Assert.Equal(
            WorkflowRefusal.TaskTitleRequired,
            WorkflowRules.Refusal(Of(), [Task(title: " ")]));
    }

    [Fact]
    public void RefusesARoleTaskWithNoRole()
    {
        Assert.Equal(
            WorkflowRefusal.AssigneeRequired,
            WorkflowRules.Refusal(
                Of(),
                [
                    new WorkflowTask
                    {
                        Title = "Collect signed contract",
                        Sequence = 1,
                        AssigneeKind = AssigneeKind.AccessLevel,
                    },
                ]));
    }

    [Fact]
    public void RefusesAPersonTaskWithNoPerson()
    {
        Assert.Equal(
            WorkflowRefusal.AssigneeRequired,
            WorkflowRules.Refusal(
                Of(),
                [
                    new WorkflowTask
                    {
                        Title = "Collect signed contract",
                        Sequence = 1,
                        AssigneeKind = AssigneeKind.Employee,
                    },
                ]));
    }

    // The reviewer toggle being on with nobody behind it leaves work that can
    // be submitted and never signed off.
    [Fact]
    public void RefusesAReviewerToggleWithNoReviewer()
    {
        Assert.Equal(
            WorkflowRefusal.ReviewerRequired,
            WorkflowRules.Refusal(Of(), [Task(hasReviewer: true)]));
    }

    [Fact]
    public void AcceptsATaskWithAReviewer()
    {
        Assert.Equal(
            WorkflowRefusal.None,
            WorkflowRules.Refusal(
                Of(),
                [Task(hasReviewer: true, reviewerAccessLevelId: Guid.NewGuid())]));
    }

    // ── dependencies ────────────────────────────────────────────────────────

    // Dependencies arrive as positions in the submitted list, because the ids
    // are minted on save. A dependency on a later task would deadlock the run.
    [Fact]
    public void RefusesADependencyOnWorkThatComesLater()
    {
        Assert.Equal(
            WorkflowRefusal.DependencyComesLater,
            WorkflowRules.DependencyRefusal([[1], []]));
    }

    [Fact]
    public void RefusesADependencyOnItself()
    {
        Assert.Equal(
            WorkflowRefusal.DependencyComesLater,
            WorkflowRules.DependencyRefusal([[], [1]]));
    }

    [Fact]
    public void RefusesADependencyOnNothing()
    {
        Assert.Equal(
            WorkflowRefusal.UnknownDependency,
            WorkflowRules.DependencyRefusal([[], [7]]));
    }

    [Fact]
    public void AcceptsADependencyOnEarlierWork()
    {
        Assert.Equal(
            WorkflowRefusal.None,
            WorkflowRules.DependencyRefusal([[], [0], [0, 1]]));
    }

    // ── the seeded workflows ────────────────────────────────────────────────

    [Fact]
    public void ASeededWorkflowCannotBeRewrittenOrRemoved()
    {
        Workflow seeded = Of(kind: WorkflowKind.System);

        Assert.False(WorkflowRules.CanEdit(seeded));
        Assert.False(WorkflowRules.CanDelete(seeded));
    }

    [Fact]
    public void AWorkflowSomebodyWroteCanBeRewrittenAndRemoved()
    {
        Workflow mine = Of(kind: WorkflowKind.Custom);

        Assert.True(WorkflowRules.CanEdit(mine));
        Assert.True(WorkflowRules.CanDelete(mine));
    }

    // ── when it is allowed to run ───────────────────────────────────────────

    [Fact]
    public void AnActiveWorkflowRuns()
    {
        Assert.True(WorkflowRules.IsRunnable(Of(status: WorkflowStatus.Active), Today));
    }

    // The status exists precisely so that a half-written workflow is not live.
    // The client's listener accepts anything that is not archived.
    [Fact]
    public void ADraftDoesNotRun()
    {
        Assert.False(WorkflowRules.IsRunnable(Of(status: WorkflowStatus.Draft), Today));
    }

    [Fact]
    public void AnArchivedWorkflowDoesNotRun()
    {
        Assert.False(WorkflowRules.IsRunnable(Of(status: WorkflowStatus.Archived), Today));
    }

    // The effective date is stored and read by nothing, so a version dated next
    // quarter fires today.
    [Fact]
    public void AWorkflowDoesNotRunBeforeItTakesEffect()
    {
        Assert.False(
            WorkflowRules.IsRunnable(
                Of(status: WorkflowStatus.Active, effectiveFrom: Today.AddDays(1)), Today));
    }

    [Fact]
    public void AWorkflowRunsOnTheDayItTakesEffect()
    {
        Assert.True(
            WorkflowRules.IsRunnable(
                Of(status: WorkflowStatus.Active, effectiveFrom: Today), Today));
    }

    [Fact]
    public void AWorkflowWithNoEffectiveDateIsEffectiveNow()
    {
        Assert.True(WorkflowRules.IsRunnable(Of(status: WorkflowStatus.Active), Today));
    }

    // ── versioning ──────────────────────────────────────────────────────────

    // The version tracks what a workflow does, so a live run is not invalidated
    // by somebody fixing a typo in the title.
    [Fact]
    public void RetitlingAWorkflowIsNotANewVersion()
    {
        Assert.False(WorkflowRules.DiffersMaterially(OneTask(), OneTask()));
    }

    [Fact]
    public void AddingWorkIsANewVersion()
    {
        Assert.True(
            WorkflowRules.DiffersMaterially(
                OneTask(), [Task(), Task(title: "And another thing", sequence: 2)]));
    }

    [Fact]
    public void ChangingWhoDoesTheWorkIsANewVersion()
    {
        Assert.True(
            WorkflowRules.DiffersMaterially(
                OneTask(), [Task(assigneeKind: AssigneeKind.Employee)]));
    }

    [Fact]
    public void ChangingWhenWorkIsDueIsANewVersion()
    {
        WorkflowTask moved = Task();
        moved.DueDayOffset = 5;

        Assert.True(WorkflowRules.DiffersMaterially(OneTask(), [moved]));
    }

    [Fact]
    public void RewordingATasksDescriptionIsNotANewVersion()
    {
        // The description is guidance for whoever picks the task up, not part
        // of what the workflow does.
        WorkflowTask reworded = Task();
        reworded.Description = "Chase it if it has not arrived by Friday.";

        Assert.False(WorkflowRules.DiffersMaterially(OneTask(), [reworded]));
    }
}
