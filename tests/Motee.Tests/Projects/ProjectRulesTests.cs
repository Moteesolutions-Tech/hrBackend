using Motee.Domain.Projects;

namespace Motee.Tests.Projects;

// What a project, a task, a milestone, an allocation and a logged hour will
// accept.
//
// The pairs that must not drift are the point of most of this: a task that is
// "100% complete" but still In Progress, and a milestone that is "completed"
// but not reached. The client's store patches each field independently and
// then tries to repair the pair afterwards, which only works when the patch
// happens to name the field it watches for.
//
// Written before the rules.
public class ProjectRulesTests
{
    private static readonly DateOnly Start = new(2026, 6, 15);

    private static Project Of(
        string name = "HRIS Implementation",
        string code = "PRJ-001",
        DateOnly? startOn = null,
        DateOnly? endOn = null) => new()
        {
            Name = name,
            Code = code,
            StartOn = startOn ?? Start,
            EndOn = endOn ?? Start.AddMonths(6),
        };

    private static ProjectTask Task(
        string name = "Data migration mapping",
        ProjectTaskStatus status = ProjectTaskStatus.NotStarted,
        int percentComplete = 0,
        DateOnly? startOn = null,
        DateOnly? endOn = null) => new()
        {
            Name = name,
            Status = status,
            PercentComplete = percentComplete,
            StartOn = startOn ?? Start,
            EndOn = endOn ?? Start.AddDays(10),
        };

    private static ProjectMilestone Milestone(
        string name = "UAT sign-off",
        MilestoneStatus status = MilestoneStatus.NotStarted,
        int percentComplete = 0) => new()
        {
            Name = name,
            Status = status,
            PercentComplete = percentComplete,
            TargetOn = Start.AddMonths(2),
        };

    private static ProjectAllocation Allocation(decimal percent = 50m) => new()
    {
        EmployeeId = Guid.NewGuid(),
        ProjectRole = "Implementation lead",
        AllocationPercent = percent,
        StartOn = Start,
    };

    private static ProjectTimeEntry Entry(decimal hours = 7.5m) => new()
    {
        EmployeeId = Guid.NewGuid(),
        WorkedOn = Start,
        Hours = hours,
    };

    private static ProjectRisk Risk(string title = "Payroll parallel run may slip") => new()
    {
        Title = title,
        RiskKind = RiskKind.Risk,
        Impact = RiskImpact.High,
        Status = RiskStatus.Open,
    };

    // ── the project ─────────────────────────────────────────────────────────

    [Fact]
    public void AcceptsAProject()
    {
        Assert.Equal(ProjectRefusal.None, ProjectRules.Refusal(Of()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HR")]
    public void RefusesANameTooShortToIdentifyTheProject(string name)
    {
        Assert.Equal(ProjectRefusal.NameTooShort, ProjectRules.Refusal(Of(name: name)));
    }

    [Fact]
    public void RefusesAProjectWithNoCode()
    {
        // The code is how the project is referenced everywhere else.
        Assert.Equal(ProjectRefusal.CodeRequired, ProjectRules.Refusal(Of(code: "  ")));
    }

    [Fact]
    public void RefusesAnEndDateBeforeTheStart()
    {
        Assert.Equal(
            ProjectRefusal.EndBeforeStart,
            ProjectRules.Refusal(Of(startOn: Start, endOn: Start.AddDays(-1))));
    }

    [Fact]
    public void AcceptsAProjectThatStartsAndEndsOnOneDay()
    {
        Assert.Equal(
            ProjectRefusal.None,
            ProjectRules.Refusal(Of(startOn: Start, endOn: Start)));
    }

    [Fact]
    public void UpperCasesAndTrimsTheCode()
    {
        Assert.Equal("PRJ-001", ProjectRules.NormaliseCode("  prj-001 "));
    }

    // ── tasks ───────────────────────────────────────────────────────────────

    [Fact]
    public void AcceptsATask()
    {
        Assert.Equal(ProjectRefusal.None, ProjectRules.TaskRefusal(Task()));
    }

    [Fact]
    public void RefusesATaskWithNoName()
    {
        Assert.Equal(
            ProjectRefusal.TaskNameTooShort,
            ProjectRules.TaskRefusal(Task(name: "a")));
    }

    [Fact]
    public void RefusesATaskThatEndsBeforeItStarts()
    {
        Assert.Equal(
            ProjectRefusal.TaskEndBeforeStart,
            ProjectRules.TaskRefusal(
                Task(startOn: Start, endOn: Start.AddDays(-1))));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void RefusesProgressOutsideAHundredPercent(int percent)
    {
        Assert.Equal(
            ProjectRefusal.PercentOutOfRange,
            ProjectRules.TaskRefusal(Task(percentComplete: percent)));
    }

    // Completion and progress are two views of one fact. Rather than patch one
    // and repair the other afterwards, a change that states both is refused
    // when they disagree.
    [Fact]
    public void RefusesATaskCompleteButNotWhole()
    {
        Assert.Equal(
            ProjectRefusal.CompletedMustBeWhole,
            ProjectRules.TaskRefusal(
                Task(status: ProjectTaskStatus.Completed, percentComplete: 80)));
    }

    [Fact]
    public void RefusesATaskWholeButNotComplete()
    {
        Assert.Equal(
            ProjectRefusal.WholeMustBeCompleted,
            ProjectRules.TaskRefusal(
                Task(status: ProjectTaskStatus.InProgress, percentComplete: 100)));
    }

    // Cancelled is the one exception: work dropped at 100% logged is out of
    // scope, not finished.
    [Fact]
    public void ACancelledTaskMayStandAtAnyProgress()
    {
        Assert.Equal(
            ProjectRefusal.None,
            ProjectRules.TaskRefusal(
                Task(status: ProjectTaskStatus.Cancelled, percentComplete: 100)));
    }

    // The status-only change: progress follows it.
    [Fact]
    public void CompletingATaskMakesItWhole()
    {
        ProjectTask task = Task(percentComplete: 40);

        ProjectRules.Moved(task, ProjectTaskStatus.Completed);

        Assert.Equal(100, task.PercentComplete);
    }

    [Fact]
    public void ReopeningACompletedTaskLeavesItShortOfWhole()
    {
        ProjectTask task = Task(status: ProjectTaskStatus.Completed, percentComplete: 100);

        ProjectRules.Moved(task, ProjectTaskStatus.InProgress);

        // Not 100: a task in progress at a hundred per cent is the drift this
        // is here to prevent. How much is left is unknown, so it reads as
        // nearly done rather than inventing a figure.
        Assert.Equal(ProjectTaskStatus.InProgress, task.Status);
        Assert.Equal(99, task.PercentComplete);
    }

    [Fact]
    public void CancellingATaskLeavesItsProgressAlone()
    {
        ProjectTask task = Task(status: ProjectTaskStatus.InProgress, percentComplete: 60);

        ProjectRules.Moved(task, ProjectTaskStatus.Cancelled);

        Assert.Equal(60, task.PercentComplete);
    }

    // The progress-only change: the status follows it.
    [Fact]
    public void ReachingAHundredPercentCompletesTheTask()
    {
        ProjectTask task = Task(status: ProjectTaskStatus.InProgress, percentComplete: 90);

        ProjectRules.Progressed(task, 100);

        Assert.Equal(ProjectTaskStatus.Completed, task.Status);
    }

    [Fact]
    public void DroppingBelowAHundredPutsACompletedTaskBackInProgress()
    {
        ProjectTask task = Task(status: ProjectTaskStatus.Completed, percentComplete: 100);

        ProjectRules.Progressed(task, 70);

        Assert.Equal(ProjectTaskStatus.InProgress, task.Status);
        Assert.Equal(70, task.PercentComplete);
    }

    [Fact]
    public void ProgressOnAnUnstartedTaskStartsIt()
    {
        ProjectTask task = Task();

        ProjectRules.Progressed(task, 25);

        Assert.Equal(ProjectTaskStatus.InProgress, task.Status);
    }

    [Fact]
    public void ProgressOnACancelledTaskDoesNotReviveIt()
    {
        ProjectTask task = Task(status: ProjectTaskStatus.Cancelled);

        ProjectRules.Progressed(task, 25);

        Assert.Equal(ProjectTaskStatus.Cancelled, task.Status);
    }

    // ── milestones ──────────────────────────────────────────────────────────

    [Fact]
    public void AcceptsAMilestone()
    {
        Assert.Equal(ProjectRefusal.None, ProjectRules.MilestoneRefusal(Milestone()));
    }

    [Fact]
    public void RefusesAMilestoneWithNoName()
    {
        Assert.Equal(
            ProjectRefusal.MilestoneNameTooShort,
            ProjectRules.MilestoneRefusal(Milestone(name: "x")));
    }

    // Reached is not stored. A milestone is reached when it is complete, which
    // is one fact rather than two that can disagree.
    [Fact]
    public void AMilestoneIsReachedWhenItIsComplete()
    {
        Assert.True(Milestones.Reached(Milestone(status: MilestoneStatus.Completed)));
        Assert.False(Milestones.Reached(Milestone(status: MilestoneStatus.AtRisk)));
    }

    [Fact]
    public void RefusesAMilestoneCompleteButNotWhole()
    {
        Assert.Equal(
            ProjectRefusal.CompletedMustBeWhole,
            ProjectRules.MilestoneRefusal(
                Milestone(status: MilestoneStatus.Completed, percentComplete: 60)));
    }

    [Fact]
    public void RefusesMilestoneProgressOutsideAHundredPercent()
    {
        Assert.Equal(
            ProjectRefusal.PercentOutOfRange,
            ProjectRules.MilestoneRefusal(Milestone(percentComplete: 101)));
    }

    [Fact]
    public void TogglingAnUnreachedMilestoneCompletesIt()
    {
        ProjectMilestone milestone = Milestone();

        Milestones.Toggle(milestone);

        Assert.Equal(MilestoneStatus.Completed, milestone.Status);
        Assert.Equal(100, milestone.PercentComplete);
    }

    [Fact]
    public void TogglingACompletedMilestoneWithProgressLeavesItInProgress()
    {
        ProjectMilestone milestone =
            Milestone(status: MilestoneStatus.Completed, percentComplete: 100);

        Milestones.Toggle(milestone);

        // Un-reaching says the work is not finished after all. It cannot say
        // how much is left, so it reads as in progress rather than untouched.
        Assert.Equal(MilestoneStatus.InProgress, milestone.Status);
        Assert.Equal(99, milestone.PercentComplete);
    }

    [Fact]
    public void ApprovalIsWhetherSomebodySignedIt()
    {
        ProjectMilestone unsigned = Milestone();
        ProjectMilestone signed = Milestone();
        signed.ApprovedAt = new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero);

        Assert.False(Milestones.IsApproved(unsigned));
        Assert.True(Milestones.IsApproved(signed));
    }

    // ── resourcing ──────────────────────────────────────────────────────────

    [Fact]
    public void AcceptsAnAllocation()
    {
        Assert.Equal(ProjectRefusal.None, ProjectRules.AllocationRefusal(Allocation()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(101)]
    public void RefusesAnAllocationOutsideOneToAHundred(decimal percent)
    {
        Assert.Equal(
            ProjectRefusal.AllocationOutOfRange,
            ProjectRules.AllocationRefusal(Allocation(percent)));
    }

    [Fact]
    public void RefusesAnAllocationThatEndsBeforeItStarts()
    {
        ProjectAllocation allocation = Allocation();
        allocation.EndOn = Start.AddDays(-1);

        Assert.Equal(
            ProjectRefusal.EndBeforeStart,
            ProjectRules.AllocationRefusal(allocation));
    }

    // ── logged time ─────────────────────────────────────────────────────────

    [Fact]
    public void AcceptsLoggedTime()
    {
        Assert.Equal(
            ProjectRefusal.None,
            ProjectRules.TimeRefusal(Entry(), allocated: true, otherHoursThatDay: 0m));
    }

    [Fact]
    public void RefusesTimeFromSomebodyNotOnTheProject()
    {
        Assert.Equal(
            ProjectRefusal.NotAllocated,
            ProjectRules.TimeRefusal(Entry(), allocated: false, otherHoursThatDay: 0m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RefusesTimeThatIsNotTime(decimal hours)
    {
        Assert.Equal(
            ProjectRefusal.HoursNotPositive,
            ProjectRules.TimeRefusal(
                Entry(hours), allocated: true, otherHoursThatDay: 0m));
    }

    [Fact]
    public void RefusesMoreHoursThanADayHas()
    {
        Assert.Equal(
            ProjectRefusal.HoursBeyondADay,
            ProjectRules.TimeRefusal(
                Entry(25m), allocated: true, otherHoursThatDay: 0m));
    }

    // The client guards one entry at a time, so twenty hours on each of three
    // projects passes it. The day is the limit, not the entry.
    [Fact]
    public void RefusesADayThatAddsUpToMoreThanADay()
    {
        Assert.Equal(
            ProjectRefusal.DayBeyondADay,
            ProjectRules.TimeRefusal(
                Entry(8m), allocated: true, otherHoursThatDay: 17m));
    }

    [Fact]
    public void AcceptsADayThatFillsExactlyTwentyFourHours()
    {
        Assert.Equal(
            ProjectRefusal.None,
            ProjectRules.TimeRefusal(
                Entry(7m), allocated: true, otherHoursThatDay: 17m));
    }

    // ── risks ───────────────────────────────────────────────────────────────

    [Fact]
    public void AcceptsARisk()
    {
        Assert.Equal(ProjectRefusal.None, ProjectRules.RiskRefusal(Risk()));
    }

    [Fact]
    public void RefusesARiskWithNoTitle()
    {
        Assert.Equal(ProjectRefusal.RiskTitleTooShort, ProjectRules.RiskRefusal(Risk("x")));
    }
}
