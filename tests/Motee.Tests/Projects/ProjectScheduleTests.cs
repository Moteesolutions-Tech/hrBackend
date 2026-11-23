using Motee.Domain.Projects;

namespace Motee.Tests.Projects;

// The dependency graph: what is blocked, what could be worked on now, where
// the dates contradict the arrows, and the longest chain through the project.
//
// Written before the schedule.
public class ProjectScheduleTests
{
    private static readonly DateOnly Start = new(2026, 6, 15);

    private static ProjectTask Task(
        string name,
        ProjectTaskStatus status = ProjectTaskStatus.NotStarted,
        int startDay = 0,
        int days = 10,
        params Guid[] dependsOn) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Status = status,
            StartOn = Start.AddDays(startDay),
            EndOn = Start.AddDays(startDay + days),
            DependsOn = [.. dependsOn],
        };

    // ── blocked ─────────────────────────────────────────────────────────────

    [Fact]
    public void ATaskThatWaitsOnNothingIsNeverBlocked()
    {
        ProjectTask task = Task("Discovery");

        Assert.False(ProjectSchedule.IsBlocked(task, [task]));
    }

    [Fact]
    public void ATaskWaitingOnUnfinishedWorkIsBlocked()
    {
        ProjectTask first = Task("Discovery");
        ProjectTask second = Task("Build", dependsOn: first.Id);

        Assert.True(ProjectSchedule.IsBlocked(second, [first, second]));
    }

    [Fact]
    public void ATaskWaitingOnlyOnFinishedWorkIsFree()
    {
        ProjectTask first = Task("Discovery", ProjectTaskStatus.Completed);
        ProjectTask second = Task("Build", dependsOn: first.Id);

        Assert.False(ProjectSchedule.IsBlocked(second, [first, second]));
    }

    // A cancelled predecessor can never become complete, so treating it as
    // unfinished would block its dependents forever.
    [Fact]
    public void ACancelledPredecessorDoesNotBlockWhatCameAfterIt()
    {
        ProjectTask dropped = Task("Parallel run", ProjectTaskStatus.Cancelled);
        ProjectTask next = Task("Go live", dependsOn: dropped.Id);

        Assert.False(ProjectSchedule.IsBlocked(next, [dropped, next]));
    }

    // Same reasoning one step further: a dependency on a task that no longer
    // exists is a dependency on nothing. The client treats an unknown id as
    // unfinished, which blocks the dependent for good.
    [Fact]
    public void ADependencyOnATaskThatIsGoneDoesNotBlockAnything()
    {
        ProjectTask next = Task("Go live", dependsOn: Guid.NewGuid());

        Assert.False(ProjectSchedule.IsBlocked(next, [next]));
    }

    [Fact]
    public void ATaskIsBlockedWhileAnySinglePredecessorIsOutstanding()
    {
        ProjectTask done = Task("Discovery", ProjectTaskStatus.Completed);
        ProjectTask outstanding = Task("Data mapping");
        ProjectTask next = Task("Build", dependsOn: [done.Id, outstanding.Id]);

        Assert.True(ProjectSchedule.IsBlocked(next, [done, outstanding, next]));
    }

    [Fact]
    public void NamesWhatATaskIsWaitingOn()
    {
        ProjectTask done = Task("Discovery", ProjectTaskStatus.Completed);
        ProjectTask outstanding = Task("Data mapping");
        ProjectTask next = Task("Build", dependsOn: [done.Id, outstanding.Id]);

        Assert.Equal(
            ["Data mapping"],
            ProjectSchedule.Blockers(next, [done, outstanding, next])
                .Select(task => task.Name));
    }

    // ── available ───────────────────────────────────────────────────────────

    [Fact]
    public void WhatCouldBeWorkedOnNowLeavesOutFinishedDroppedAndBlockedWork()
    {
        ProjectTask done = Task("Discovery", ProjectTaskStatus.Completed);
        ProjectTask dropped = Task("Nice to have", ProjectTaskStatus.Cancelled);
        ProjectTask open = Task("Data mapping");
        ProjectTask blocked = Task("Build", dependsOn: open.Id);

        Assert.Equal(
            ["Data mapping"],
            ProjectSchedule.Available([done, dropped, open, blocked])
                .Select(task => task.Name));
    }

    // ── conflicts ───────────────────────────────────────────────────────────

    // The scheduling error a Gantt exists to expose: the arrow says "after",
    // the dates say "during".
    [Fact]
    public void FindsADependencyThatOverrunsItsDependentsStart()
    {
        ProjectTask first = Task("Discovery", startDay: 0, days: 20);
        ProjectTask second = Task("Build", startDay: 10, days: 10, dependsOn: first.Id);

        ScheduleConflict conflict = Assert.Single(
            ProjectSchedule.Conflicts([first, second]));

        Assert.Equal(second.Id, conflict.TaskId);
        Assert.Equal(first.Id, conflict.BlockerId);
        Assert.Equal(10, conflict.OverlapDays);
    }

    [Fact]
    public void AHandoverOnTheSameDayIsNotAConflict()
    {
        ProjectTask first = Task("Discovery", startDay: 0, days: 10);
        ProjectTask second = Task("Build", startDay: 10, days: 10, dependsOn: first.Id);

        Assert.Empty(ProjectSchedule.Conflicts([first, second]));
    }

    [Fact]
    public void ADependencyOnAMissingTaskIsNotAConflict()
    {
        ProjectTask only = Task("Build", dependsOn: Guid.NewGuid());

        Assert.Empty(ProjectSchedule.Conflicts([only]));
    }

    // ── the critical path ───────────────────────────────────────────────────

    [Fact]
    public void NoTasksMeansNoCriticalPath()
    {
        Assert.Empty(ProjectSchedule.CriticalPath([]));
    }

    [Fact]
    public void TheCriticalPathFollowsTheDependencyChain()
    {
        ProjectTask first = Task("Discovery", days: 10);
        ProjectTask second = Task("Build", startDay: 10, days: 10, dependsOn: first.Id);
        ProjectTask third = Task("Test", startDay: 20, days: 10, dependsOn: second.Id);

        Assert.Equal(
            [first.Id, second.Id, third.Id],
            ProjectSchedule.CriticalPath([first, second, third]));
    }

    // Weighted by how long each task runs rather than by how many there are: a
    // two-task chain of ninety-day tasks matters more than four five-day ones,
    // and the Gantt's bars are already date-driven.
    [Fact]
    public void TheLongestChainIsMeasuredInDaysNotInTasks()
    {
        ProjectTask longFirst = Task("Payroll parallel run", days: 90);
        ProjectTask longSecond = Task(
            "Payroll sign-off", startDay: 90, days: 90, dependsOn: longFirst.Id);

        ProjectTask shortFirst = Task("Branding", days: 5);
        ProjectTask shortSecond = Task(
            "Copy review", startDay: 5, days: 5, dependsOn: shortFirst.Id);
        ProjectTask shortThird = Task(
            "Sign-off", startDay: 10, days: 5, dependsOn: shortSecond.Id);
        ProjectTask shortFourth = Task(
            "Publish", startDay: 15, days: 5, dependsOn: shortThird.Id);

        Assert.Equal(
            [longFirst.Id, longSecond.Id],
            ProjectSchedule.CriticalPath(
                [longFirst, longSecond, shortFirst, shortSecond, shortThird, shortFourth]));
    }

    [Fact]
    public void ASingleDayTaskStillCountsTowardsTheChain()
    {
        ProjectTask first = Task("Kick-off", days: 0);
        ProjectTask second = Task("Build", startDay: 0, days: 0, dependsOn: first.Id);

        Assert.Equal([first.Id, second.Id], ProjectSchedule.CriticalPath([first, second]));
    }

    // A dependency loop is a corrupt import, not a reason to hang the page.
    [Fact]
    public void ADependencyLoopDoesNotRunForever()
    {
        ProjectTask first = Task("One");
        ProjectTask second = Task("Two", dependsOn: first.Id);
        first.DependsOn = [second.Id];

        IReadOnlyList<Guid> path = ProjectSchedule.CriticalPath([first, second]);

        Assert.NotEmpty(path);
        Assert.Equal(path.Count, path.Distinct().Count());
    }

    [Fact]
    public void TheLongestOfTwoUnrelatedChainsWins()
    {
        ProjectTask shortOne = Task("Branding", days: 5);
        ProjectTask longOne = Task("Data migration", days: 60);

        Assert.Equal([longOne.Id], ProjectSchedule.CriticalPath([shortOne, longOne]));
    }
}
