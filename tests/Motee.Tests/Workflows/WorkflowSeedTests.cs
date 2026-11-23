using Motee.Domain.Identity;
using Motee.Domain.Workflows;

namespace Motee.Tests.Workflows;

// The four workflows the product drives itself.
//
// These are ported from the client's own seeds, so the test that matters is
// that the dependency graph they describe is actually runnable: every step
// number resolves, nothing waits on later work, and no run can deadlock.
//
// Written before the seeds.
public class WorkflowSeedTests
{
    [Fact]
    public void SeedsTheFourFlowsTheProductFires()
    {
        Assert.Equal(
            ["recruitment", "preboarding", "onboarding", "offboarding"],
            WorkflowSeeds.All.Select(seed => seed.Key));
    }

    [Fact]
    public void EachFlowIsAnchoredToItsOwnEvent()
    {
        Assert.Equal(
            Enum.GetValues<WorkflowTriggerEvent>(),
            WorkflowSeeds.All.Select(seed => seed.TriggerEvent).Distinct().Order());
    }

    [Fact]
    public void EveryFlowHasWorkInIt()
    {
        Assert.All(WorkflowSeeds.All, seed => Assert.NotEmpty(seed.Tasks));
    }

    [Fact]
    public void EveryStepSaysWhatItIsAndWhoDoesIt()
    {
        foreach (WorkflowSeed seed in WorkflowSeeds.All)
        {
            Assert.All(seed.Tasks, task =>
            {
                Assert.False(string.IsNullOrWhiteSpace(task.Title));
                Assert.False(string.IsNullOrWhiteSpace(task.Description));
                Assert.True(Enum.IsDefined(task.Assignee));
            });
        }
    }

    // A reviewer who is also the doer is somebody approving their own work.
    [Fact]
    public void NobodyReviewsTheirOwnWork()
    {
        foreach (WorkflowSeed seed in WorkflowSeeds.All)
        {
            Assert.All(
                seed.Tasks.Where(task => task.Reviewer is not null),
                task => Assert.NotEqual(task.Assignee, task.Reviewer));
        }
    }

    // The step numbers are what makes the seeds readable, and the one thing
    // that can silently be wrong.
    [Fact]
    public void EveryStepWaitsOnlyOnWorkThatComesBefore()
    {
        foreach (WorkflowSeed seed in WorkflowSeeds.All)
        {
            IReadOnlyList<IReadOnlyList<int>> positions =
            [
                .. seed.Tasks.Select(task =>
                    (IReadOnlyList<int>)[.. task.DependsOnSteps.Select(step => step - 1)]),
            ];

            Assert.Equal(
                WorkflowRefusal.None,
                WorkflowRules.DependencyRefusal(positions));
        }
    }

    [Fact]
    public void EverySeededFlowWouldBeAccepted()
    {
        foreach (WorkflowSeed seed in WorkflowSeeds.All)
        {
            Workflow workflow = new()
            {
                Title = seed.Title,
                TriggerMode = WorkflowTriggerMode.Automatic,
                ScheduleKind = ScheduleKind.Relative,
                TriggerEvent = seed.TriggerEvent,
                OffsetValue = 0,
                OffsetUnit = ScheduleOffsetUnit.Days,
                Status = WorkflowStatus.Active,
                WorkflowKind = WorkflowKind.System,
            };

            IReadOnlyList<WorkflowTask> tasks =
            [
                .. seed.Tasks.Select((task, index) => new WorkflowTask
                {
                    Id = Guid.NewGuid(),
                    Title = task.Title,
                    Sequence = index + 1,
                    AssigneeKind = AssigneeKind.AccessLevel,
                    AssigneeAccessLevelId = Guid.NewGuid(),
                    HasReviewer = task.Reviewer is not null,
                    ReviewerAccessLevelId = task.Reviewer is null ? null : Guid.NewGuid(),
                }),
            ];

            Assert.Equal(
                WorkflowRefusal.None,
                WorkflowRules.Refusal(workflow, tasks));
        }
    }

    // Whatever facts a run is given, some work has to start — a flow where
    // everything waits is a flow nobody can begin.
    [Theory]
    [InlineData("recruitment")]
    [InlineData("preboarding")]
    [InlineData("onboarding")]
    [InlineData("offboarding")]
    public void SomethingAlwaysStartsOnDayOne(string key)
    {
        WorkflowSeed seed = WorkflowSeeds.All.First(row => row.Key == key);

        IReadOnlyList<WorkflowTask> tasks = Build(seed);

        Assert.NotEmpty(WorkflowPlan.Of(tasks, []).Activated);

        Assert.NotEmpty(
            WorkflowPlan.Of(tasks, [.. Enum.GetValues<WorkflowCondition>()]).Activated);
    }

    // Working the graph through to the end proves no step is unreachable: the
    // combination of dependencies, parallel groups and conditions cannot leave
    // work stranded.
    [Theory]
    [InlineData("recruitment")]
    [InlineData("preboarding")]
    [InlineData("onboarding")]
    [InlineData("offboarding")]
    public void EveryFlowCanBeWorkedThroughToTheEnd(string key)
    {
        WorkflowSeed seed = WorkflowSeeds.All.First(row => row.Key == key);

        IReadOnlyList<WorkflowTask> tasks = Build(seed);

        List<Guid> settled = [];
        int rounds = 0;

        while (settled.Count < tasks.Count && rounds <= tasks.Count)
        {
            WorkflowRunPlan plan = WorkflowPlan.Of(tasks, [], settled);

            IReadOnlyList<Guid> next =
            [
                .. plan.Activated.Select(task => task.Id),
                .. plan.Skipped.Select(task => task.Id),
            ];

            Assert.NotEmpty(next);

            settled.AddRange(next.Where(id => !settled.Contains(id)));
            rounds++;
        }

        Assert.Equal(tasks.Count, settled.Count);
    }

    // Benefits enrolment is the one phrased as "unless": a permanent hire gets
    // it and a contractor does not. The client has it the other way round.
    [Fact]
    public void BenefitsEnrolmentAppliesToStaffAndNotToContractors()
    {
        WorkflowSeedTask benefits = WorkflowSeeds.Onboarding.Tasks
            .First(task => task.Title == "Benefits enrolment");

        Assert.Equal(WorkflowCondition.Contractor, benefits.Condition);
        Assert.False(benefits.ConditionHolds);
    }

    [Fact]
    public void ImmigrationWorkAppliesOnlyToVisaHolders()
    {
        Assert.All(
            WorkflowSeeds.All
                .SelectMany(seed => seed.Tasks)
                .Where(task => task.Condition == WorkflowCondition.VisaHolder),
            task => Assert.True(task.ConditionHolds));
    }

    [Fact]
    public void RecruitmentHandsOverToPreboarding()
    {
        // The seam between the two flows: preboarding's own trigger fires when
        // a candidate is hired, so recruitment has to get that far.
        Assert.Equal(
            "Hand over to preboarding",
            WorkflowSeeds.Recruitment.Tasks[^1].Title);
    }

    private static IReadOnlyList<WorkflowTask> Build(WorkflowSeed seed)
    {
        IReadOnlyList<Guid> ids = [.. seed.Tasks.Select(_ => Guid.NewGuid())];

        return
        [
            .. seed.Tasks.Select((task, index) => new WorkflowTask
            {
                Id = ids[index],
                Title = task.Title,
                Description = task.Description,
                Sequence = index + 1,
                AssigneeKind = AssigneeKind.AccessLevel,
                AssigneeAccessLevelId = Guid.NewGuid(),
                HasReviewer = task.Reviewer is not null,
                ReviewerAccessLevelId = task.Reviewer is null ? null : Guid.NewGuid(),
                DueDayOffset = task.DueDayOffset,
                ExpectedDurationDays = task.ExpectedDurationDays,
                Priority = task.Priority,
                EscalateAfterDays = task.EscalateAfterDays,
                DependsOn = [.. task.DependsOnSteps.Select(step => ids[step - 1])],
                ParallelGroup = task.ParallelGroup,
                Condition = task.Condition,
                ConditionHolds = task.ConditionHolds,
            }),
        ];
    }
}
