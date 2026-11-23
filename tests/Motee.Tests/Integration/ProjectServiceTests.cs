using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Projects;
using Motee.Domain.Projects;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// Projects, out of the database.
//
// What these prove, beyond the plumbing: a code identifies one project within a
// company; a task cannot be started while it waits on something else; deleting
// a task does not strand whatever depended on it or quietly reduce the spend;
// and an allocation set twice moves the figure rather than booking somebody
// twice.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class ProjectServiceTests(PostgresFixture fixture)
{
    // ── starting one ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task StartsAProject()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync(
            ProjectScene.Planning(owner: scene.AdaId));

        Assert.Equal("PRJ-001", project.Project.Code);
        Assert.Equal("Ada Okafor", project.Project.OwnerName);
        Assert.Equal(0, project.Project.ProgressPercent);
        Assert.Empty(project.Tasks);
        Assert.Equal(HealthStatus.Green, project.Health.Overall);
    }

    [SkippableFact]
    public async Task UpperCasesTheCodeSoOneProjectIsNotTwo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync(
            ProjectScene.Planning(code: " prj-001 "));

        Assert.Equal("PRJ-001", project.Project.Code);
        Assert.Equal("HR001", project.Project.CostCentreCode);
    }

    [SkippableFact]
    public async Task RefusesASecondProjectOnTheSameCode()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        await scene.StartedAsync();

        Assert.Equal(
            ProjectOutcome.CodeTaken,
            (await scene.Projects.CreateAsync(
                ProjectScene.Planning(code: "prj-001", name: "Something else"))).Outcome);
    }

    [SkippableFact]
    public async Task AProjectKeepsItsOwnCodeOnEdit()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        // The uniqueness check has to let a project keep the code it already
        // has, or no project could ever be edited.
        Assert.True(
            (await scene.Projects.UpdateAsync(
                project.Project.Id,
                ProjectScene.Planning(name: "HRIS Implementation — phase two")))
                .Succeeded);
    }

    [SkippableFact]
    public async Task RefusesAnOwnerWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        Assert.Equal(
            ProjectOutcome.EmployeeNotFound,
            (await scene.Projects.CreateAsync(
                ProjectScene.Planning(owner: Guid.NewGuid()))).Outcome);
    }

    [SkippableFact]
    public async Task RefusesADateRangeThatRunsBackwards()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        Assert.Equal(
            ProjectRefusal.EndBeforeStart,
            (await scene.Projects.CreateAsync(
                ProjectScene.Planning(startsIn: 30, endsIn: 10))).Refusal);
    }

    // ── the list ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ListsTheOpenAndTheClosedSeparately()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        await scene.StartedAsync(ProjectScene.Planning(code: "A", name: "Running now"));
        await scene.StartedAsync(
            ProjectScene.Planning(code: "B", name: "Planned", status: ProjectStatus.Planning));
        await scene.StartedAsync(
            ProjectScene.Planning(code: "C", name: "Done", status: ProjectStatus.Completed));

        Assert.Equal(
            2,
            (await scene.Projects.ListAsync(new ProjectQuery { OpenOnly = true })).TotalItems);

        Assert.Equal(
            1,
            (await scene.Projects.ListAsync(new ProjectQuery { ClosedOnly = true }))
                .TotalItems);
    }

    [SkippableFact]
    public async Task ListsOnlyTheStaffedAndTheBudgetedWhenAsked()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto staffed = await scene.StartedAsync(
            ProjectScene.Planning(code: "A", name: "With a team"));

        await scene.StartedAsync(
            ProjectScene.Planning(code: "B", name: "No budget", budget: null));

        await scene.Projects.SetAllocationAsync(
            staffed.Project.Id, ProjectScene.Booking(scene.AdaId));

        PagedResult<ProjectRowDto> withTeams =
            await scene.Projects.ListAsync(new ProjectQuery { StaffedOnly = true });

        Assert.Equal(["With a team"], withTeams.Items.Select(row => row.Name));

        PagedResult<ProjectRowDto> funded =
            await scene.Projects.ListAsync(new ProjectQuery { BudgetedOnly = true });

        Assert.Equal(["With a team"], funded.Items.Select(row => row.Name));
    }

    [SkippableFact]
    public async Task DoesNotTreatASearchTermAsAWildcard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        await scene.StartedAsync(
            ProjectScene.Planning(code: "A", name: "Phase 1 (100% scope)"));
        await scene.StartedAsync(
            ProjectScene.Planning(code: "B", name: "Something else entirely"));

        Assert.Equal(
            1,
            (await scene.Projects.ListAsync(new ProjectQuery { Search = "100%" })).TotalItems);
    }

    [SkippableFact]
    public async Task TheListCarriesTheFiguresTheTableShows()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        await scene.AddedAsync(
            project.Project.Id,
            ProjectScene.Work(name: "Done", status: ProjectTaskStatus.Completed,
                percentComplete: 100));

        await scene.AddedAsync(project.Project.Id, ProjectScene.Work(name: "Open"));

        await scene.Projects.SetAllocationAsync(
            project.Project.Id, ProjectScene.Booking(scene.AdaId, percent: 75m));

        ProjectRowDto row = (await scene.Projects.ListAsync(new ProjectQuery())).Items[0];

        Assert.Equal(50, row.ProgressPercent);
        Assert.Equal(2, row.TaskCount);
        Assert.Equal(1, row.CompletedTaskCount);
        Assert.Equal(1, row.TeamSize);
        Assert.Equal(0.75m, row.TotalAllocationFte);
    }

    // ── work ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ATaskKnowsWhatItIsWaitingOnAndWhetherItIsCritical()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        ProjectDto withFirst = await scene.AddedAsync(
            project.Project.Id, ProjectScene.Work(name: "Discovery", endsIn: 40));

        Guid firstId = ProjectScene.TaskNamed(withFirst, "Discovery").Id;

        ProjectDto both = await scene.AddedAsync(
            project.Project.Id,
            ProjectScene.Work(name: "Build", startsIn: 40, endsIn: 80, dependsOn: [firstId]));

        ProjectTaskDto build = ProjectScene.TaskNamed(both, "Build");

        Assert.True(build.IsBlocked);
        Assert.Equal(["Discovery"], build.WaitingOn);
        Assert.True(build.IsOnCriticalPath);
        Assert.Equal(2, both.CriticalPathTaskIds.Count);
    }

    [SkippableFact]
    public async Task RefusesADependencyOnATaskFromAnotherProject()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto first = await scene.StartedAsync(ProjectScene.Planning(code: "A"));
        ProjectDto second = await scene.StartedAsync(
            ProjectScene.Planning(code: "B", name: "Another project"));

        ProjectDto withTask = await scene.AddedAsync(
            second.Project.Id, ProjectScene.Work(name: "Elsewhere"));

        Assert.Equal(
            ProjectRefusal.UnknownTask,
            (await scene.Projects.AddTaskAsync(
                first.Project.Id,
                ProjectScene.Work(
                    dependsOn: [ProjectScene.TaskNamed(withTask, "Elsewhere").Id])))
                .Refusal);
    }

    [SkippableFact]
    public async Task RefusesATaskThatWaitsOnItself()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        ProjectDto withTask = await scene.AddedAsync(
            project.Project.Id, ProjectScene.Work(name: "Discovery"));

        Guid taskId = ProjectScene.TaskNamed(withTask, "Discovery").Id;

        // It would wait for itself forever.
        Assert.Equal(
            ProjectRefusal.UnknownTask,
            (await scene.Projects.UpdateTaskAsync(
                project.Project.Id,
                taskId,
                ProjectScene.Work(name: "Discovery", dependsOn: [taskId])))
                .Refusal);
    }

    [SkippableFact]
    public async Task WillNotStartWorkThatIsWaitingOnSomethingElse()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        (ProjectDto project, Guid _, Guid buildId) = await ChainAsync(scene);

        Assert.Equal(
            ProjectRefusal.Blocked,
            (await scene.Projects.SetTaskStatusAsync(
                project.Project.Id, buildId, ProjectTaskStatus.InProgress)).Refusal);
    }

    [SkippableFact]
    public async Task StartsWorkOnceWhatItWaitedOnIsFinished()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        (ProjectDto project, Guid discoveryId, Guid buildId) = await ChainAsync(scene);

        await scene.Projects.SetTaskStatusAsync(
            project.Project.Id, discoveryId, ProjectTaskStatus.Completed);

        ProjectResult result = await scene.Projects.SetTaskStatusAsync(
            project.Project.Id, buildId, ProjectTaskStatus.InProgress);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
    }

    [SkippableFact]
    public async Task CompletingATaskMakesItWhole()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        ProjectDto withTask = await scene.AddedAsync(
            project.Project.Id, ProjectScene.Work(percentComplete: 30,
                status: ProjectTaskStatus.InProgress));

        Guid taskId = withTask.Tasks[0].Id;

        ProjectResult result = await scene.Projects.SetTaskStatusAsync(
            project.Project.Id, taskId, ProjectTaskStatus.Completed);

        Assert.Equal(100, result.Project!.Tasks[0].PercentComplete);
    }

    [SkippableFact]
    public async Task ReachingAHundredPerCentCompletesTheTask()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        ProjectDto withTask = await scene.AddedAsync(project.Project.Id, ProjectScene.Work());

        ProjectResult result = await scene.Projects.SetTaskProgressAsync(
            project.Project.Id, withTask.Tasks[0].Id, 100);

        Assert.Equal(ProjectTaskStatus.Completed, result.Project!.Tasks[0].Status);
    }

    [SkippableFact]
    public async Task RefusesProgressOutsideAHundredPerCent()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();
        ProjectDto withTask = await scene.AddedAsync(project.Project.Id, ProjectScene.Work());

        Assert.Equal(
            ProjectRefusal.PercentOutOfRange,
            (await scene.Projects.SetTaskProgressAsync(
                project.Project.Id, withTask.Tasks[0].Id, 140)).Refusal);
    }

    // Deleting a task leaves a dependency pointing at nothing, a milestone
    // listing it and a risk tied to it. All three are cleared out.
    [SkippableFact]
    public async Task DeletingATaskDoesNotStrandWhatPointedAtIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        (ProjectDto project, Guid discoveryId, Guid buildId) = await ChainAsync(scene);

        await scene.Projects.AddMilestoneAsync(
            project.Project.Id, ProjectScene.Date(taskIds: [discoveryId, buildId]));

        await scene.Risks.AddAsync(
            project.Project.Id, ProjectScene.Threat(taskIds: [discoveryId]));

        ProjectResult result = await scene.Projects.DeleteTaskAsync(
            project.Project.Id, discoveryId);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        ProjectDto after = result.Project!;

        ProjectTaskDto build = ProjectScene.TaskNamed(after, "Build");

        Assert.Empty(build.DependsOn);
        Assert.False(build.IsBlocked);
        Assert.Equal([buildId], ProjectScene.MilestoneNamed(after, "UAT sign-off").TaskIds);
        Assert.Empty((await scene.Risks.ListAsync(project.Project.Id))[0].RelatedTaskIds);
    }

    // The time was still spent on the project. Dropping it with the task would
    // quietly reduce the spend.
    [SkippableFact]
    public async Task DeletingATaskKeepsTheHoursLoggedAgainstIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        await scene.Projects.SetAllocationAsync(
            project.Project.Id, ProjectScene.Booking(scene.AdaId, hourlyRate: 100m));

        ProjectDto withTask = await scene.AddedAsync(project.Project.Id, ProjectScene.Work());

        Guid taskId = withTask.Tasks[0].Id;

        TimeEntryResult logged = await scene.Time.LogAsync(
            project.Project.Id, ProjectScene.Hours(scene.AdaId, 8m, taskId: taskId));

        Assert.True(logged.Succeeded, $"{logged.Outcome} / {logged.Refusal}");

        await scene.Time.SetStatusAsync(
            project.Project.Id, logged.Entry!.Id, TimeEntryStatus.Approved);

        await scene.Projects.DeleteTaskAsync(project.Project.Id, taskId);

        ProjectDto? after = await scene.Projects.GetAsync(project.Project.Id);

        Assert.Equal(8m, after!.ApprovedHours);
        Assert.Equal(800m, after.Project.Spend);

        await using MoteeDbContext read = scene.Read();

        ProjectTimeEntry entry = await read.ProjectTimeEntries.SingleAsync();

        // Still on the project, now against general project work.
        Assert.Null(entry.TaskId);
    }

    // ── dates ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ReachingAMilestoneRecordsTheDayItWasMet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        ProjectResult added = await scene.Projects.AddMilestoneAsync(
            project.Project.Id, ProjectScene.Date(responsible: scene.BolaId));

        Guid milestoneId = added.Project!.Milestones[0].Id;

        ProjectResult reached = await scene.Projects.ToggleMilestoneAsync(
            project.Project.Id, milestoneId);

        ProjectMilestoneDto milestone = reached.Project!.Milestones[0];

        Assert.True(milestone.IsReached);
        Assert.Equal(MilestoneStatus.Completed, milestone.Status);
        Assert.Equal(ProjectScene.Today, milestone.ActualOn);
        Assert.Equal("Bola Okafor", milestone.ResponsibleName);

        ProjectResult unreached = await scene.Projects.ToggleMilestoneAsync(
            project.Project.Id, milestoneId);

        Assert.False(unreached.Project!.Milestones[0].IsReached);
        Assert.Null(unreached.Project.Milestones[0].ActualOn);
    }

    [SkippableFact]
    public async Task SigningOffAMilestoneRecordsWhoDidIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        ProjectResult added = await scene.Projects.AddMilestoneAsync(
            project.Project.Id, ProjectScene.Date());

        Guid milestoneId = added.Project!.Milestones[0].Id;

        ProjectResult signed = await scene.Projects.SetMilestoneApprovalAsync(
            project.Project.Id, milestoneId, approved: true);

        Assert.True(signed.Project!.Milestones[0].IsApproved);
        Assert.Equal(scene.ActorUserId, signed.Project.Milestones[0].ApprovedByUserId);

        ProjectResult withdrawn = await scene.Projects.SetMilestoneApprovalAsync(
            project.Project.Id, milestoneId, approved: false);

        Assert.False(withdrawn.Project!.Milestones[0].IsApproved);
        Assert.Null(withdrawn.Project.Milestones[0].ApprovedByUserId);
    }

    [SkippableFact]
    public async Task AMissedMilestoneTurnsTheProjectRed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        await scene.Projects.AddMilestoneAsync(
            project.Project.Id, ProjectScene.Date(name: "Was due", targetIn: -2));

        ProjectDto? after = await scene.Projects.GetAsync(project.Project.Id);

        Assert.Equal(HealthStatus.Red, after!.Health.Schedule);
        Assert.Equal(HealthStatus.Red, after.Health.Overall);
        Assert.True(after.Milestones[0].IsOverdue);

        // And it is the first thing in the list of what to do next.
        Assert.Equal(NextActionBucket.Overdue, after.NextActions[0].Bucket);
        Assert.Equal("Was due", after.NextActions[0].Label);
    }

    // ── people ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task BookingSomebodyTwiceMovesTheFigure()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        await scene.Projects.SetAllocationAsync(
            project.Project.Id, ProjectScene.Booking(scene.AdaId, percent: 40m));

        ProjectResult again = await scene.Projects.SetAllocationAsync(
            project.Project.Id, ProjectScene.Booking(scene.AdaId, percent: 60m));

        ProjectAllocationDto allocation = Assert.Single(again.Project!.Allocations);

        Assert.Equal(60m, allocation.AllocationPercent);
        Assert.Equal(0.6m, again.Project.Project.TotalAllocationFte);
    }

    [SkippableFact]
    public async Task AnAllocationFallsBackToTheRoleTheyAlreadyHold()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        ProjectResult result = await scene.Projects.SetAllocationAsync(
            project.Project.Id, ProjectScene.Booking(scene.AdaId));

        Assert.Equal("Engineer", result.Project!.Allocations[0].ProjectRole);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(140)]
    public async Task RefusesAnAllocationOutsideOneToAHundred(int percent)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        Assert.Equal(
            ProjectRefusal.AllocationOutOfRange,
            (await scene.Projects.SetAllocationAsync(
                project.Project.Id, ProjectScene.Booking(scene.AdaId, percent))).Refusal);
    }

    [SkippableFact]
    public async Task RefusesToBookSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        Assert.Equal(
            ProjectOutcome.EmployeeNotFound,
            (await scene.Projects.SetAllocationAsync(
                project.Project.Id, ProjectScene.Booking(Guid.NewGuid()))).Outcome);
    }

    [SkippableFact]
    public async Task TakingSomebodyOffAProjectReleasesTheirTime()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        await scene.Projects.SetAllocationAsync(
            project.Project.Id, ProjectScene.Booking(scene.AdaId));

        ProjectResult result = await scene.Projects.RemoveAllocationAsync(
            project.Project.Id, scene.AdaId);

        Assert.Empty(result.Project!.Allocations);
        Assert.Equal(0m, result.Project.Project.TotalAllocationFte);
    }

    // The whole point of recording percentages is being told when they add up
    // to more than a person.
    [SkippableFact]
    public async Task ReportsSomebodyCommittedBeyondFullTime()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto first = await scene.StartedAsync(ProjectScene.Planning(code: "A"));
        ProjectDto second = await scene.StartedAsync(
            ProjectScene.Planning(code: "B", name: "Payroll migration"));

        await scene.Projects.SetAllocationAsync(
            first.Project.Id, ProjectScene.Booking(scene.AdaId, percent: 70m));
        await scene.Projects.SetAllocationAsync(
            second.Project.Id, ProjectScene.Booking(scene.AdaId, percent: 60m));

        ProjectSummaryDto summary = await scene.Projects.SummaryAsync();

        OverAllocation over = Assert.Single(summary.OverAllocations);

        Assert.Equal(scene.AdaId, over.EmployeeId);
        Assert.Equal("Ada Okafor", over.EmployeeName);
        Assert.Equal(130m, over.TotalPercent);
        Assert.Equal(2, over.Projects.Count);

        // And it shows on the project itself, so the team panel can say so.
        ProjectDto? detail = await scene.Projects.GetAsync(first.Project.Id);

        Assert.Equal(130m, detail!.Allocations[0].CommittedPercentEverywhere);
        Assert.Equal(HealthStatus.Red, detail.Health.Resources);
    }

    [SkippableFact]
    public async Task AFinishedProjectStopsClaimingAnybodysTime()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto running = await scene.StartedAsync(ProjectScene.Planning(code: "A"));
        ProjectDto finished = await scene.StartedAsync(
            ProjectScene.Planning(code: "B", name: "Wrapped up"));

        await scene.Projects.SetAllocationAsync(
            running.Project.Id, ProjectScene.Booking(scene.AdaId, percent: 70m));
        await scene.Projects.SetAllocationAsync(
            finished.Project.Id, ProjectScene.Booking(scene.AdaId, percent: 60m));

        await scene.Projects.UpdateAsync(
            finished.Project.Id,
            ProjectScene.Planning(
                code: "B", name: "Wrapped up", status: ProjectStatus.Completed));

        ProjectSummaryDto summary = await scene.Projects.SummaryAsync();

        Assert.Empty(summary.OverAllocations);
        Assert.Equal(1, summary.PeopleAssigned);
    }

    // ── the figures ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ReportsTheLandingPagesFigures()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto first = await scene.StartedAsync(
            ProjectScene.Planning(code: "A", budget: 100_000m));

        await scene.StartedAsync(
            ProjectScene.Planning(
                code: "B", name: "Closed one", status: ProjectStatus.Completed, budget: null));

        await scene.AddedAsync(
            first.Project.Id,
            ProjectScene.Work(status: ProjectTaskStatus.Completed, percentComplete: 100));

        await scene.Projects.SetAllocationAsync(
            first.Project.Id, ProjectScene.Booking(scene.AdaId, hourlyRate: 100m));

        TimeEntryResult logged = await scene.Time.LogAsync(
            first.Project.Id, ProjectScene.Hours(scene.AdaId, 10m));

        await scene.Time.SetStatusAsync(
            first.Project.Id, logged.Entry!.Id, TimeEntryStatus.Approved);

        ProjectSummaryDto summary = await scene.Projects.SummaryAsync();

        Assert.Equal(2, summary.TotalProjects);
        Assert.Equal(1, summary.OpenProjects);
        Assert.Equal(1, summary.StaffedProjects);
        Assert.Equal(1, summary.BudgetedProjects);
        Assert.Equal(1, summary.PeopleAssigned);
        Assert.Equal(100_000m, summary.TotalBudget);
        Assert.Equal(1_000m, summary.TotalSpend);

        // Averaged across the open projects, which is what the card says.
        Assert.Equal(100, summary.AverageProgressPercent);
    }

    [SkippableFact]
    public async Task TheDetailCarriesTheMoney()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync(
            ProjectScene.Planning(budget: 10_000m));

        await scene.Projects.SetAllocationAsync(
            project.Project.Id, ProjectScene.Booking(scene.AdaId, hourlyRate: 100m));

        // Half-finished work with ten hours estimated: five hours left at a
        // hundred an hour.
        await scene.AddedAsync(
            project.Project.Id,
            ProjectScene.Work(
                status: ProjectTaskStatus.InProgress,
                percentComplete: 50,
                assignee: scene.AdaId,
                estimatedHours: 10m));

        TimeEntryResult logged = await scene.Time.LogAsync(
            project.Project.Id, ProjectScene.Hours(scene.AdaId, 8m));

        await scene.Time.SetStatusAsync(
            project.Project.Id, logged.Entry!.Id, TimeEntryStatus.Approved);

        ProjectDto? after = await scene.Projects.GetAsync(project.Project.Id);

        Assert.Equal(800m, after!.Project.Spend);
        Assert.Equal(500m, after.CommittedSpend);
        Assert.Equal(1_300m, after.ForecastFinalCost);
        Assert.Equal(8_700m, after.RemainingBudget);
        Assert.Equal(8m, after.BudgetUtilisationPercent);
        Assert.Equal(HealthStatus.Green, after.Health.Budget);
    }

    [SkippableFact]
    public async Task AProjectWithNoBudgetIsNotAnUnhealthyProject()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync(ProjectScene.Planning(budget: null));

        ProjectDto? after = await scene.Projects.GetAsync(project.Project.Id);

        Assert.Equal(HealthStatus.Unknown, after!.Health.Budget);
        Assert.Equal(HealthStatus.Unknown, after.Health.Scope);
        Assert.Equal(HealthStatus.Green, after.Health.Overall);
    }

    [SkippableFact]
    public async Task TheAlertNamesWhatIsHoldingTheProjectUp()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        (ProjectDto project, Guid _, Guid buildId) = await ChainAsync(scene);

        await scene.Projects.AddMilestoneAsync(
            project.Project.Id,
            ProjectScene.Date(name: "Go live", targetIn: 120, taskIds: [buildId]));

        ProjectDto? after = await scene.Projects.GetAsync(project.Project.Id);

        Assert.NotNull(after!.Alert);
        Assert.Equal("Build", after.Alert.BlockerTaskName);
        Assert.Equal("Go live", after.Alert.ImpactMilestoneName);
        Assert.Equal(HealthStatus.Red, after.Health.CriticalPath);
    }

    // ── removing one ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task DeletingAProjectTakesEverythingOnItWithIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        await scene.Projects.SetAllocationAsync(
            project.Project.Id, ProjectScene.Booking(scene.AdaId));
        await scene.AddedAsync(project.Project.Id, ProjectScene.Work());
        await scene.Projects.AddMilestoneAsync(project.Project.Id, ProjectScene.Date());
        await scene.Risks.AddAsync(project.Project.Id, ProjectScene.Threat());
        await scene.Time.LogAsync(project.Project.Id, ProjectScene.Hours(scene.AdaId));
        await scene.GoLive.SetAsync(
            project.Project.Id, "people.training", new GoLiveTickRequest { IsDone = true });

        Assert.True((await scene.Projects.DeleteAsync(project.Project.Id)).Succeeded);

        await using MoteeDbContext read = scene.Read();

        Assert.Empty(await read.ProjectTasks.ToListAsync());
        Assert.Empty(await read.ProjectMilestones.ToListAsync());
        Assert.Empty(await read.ProjectAllocations.ToListAsync());
        Assert.Empty(await read.ProjectTimeEntries.ToListAsync());
        Assert.Empty(await read.ProjectRisks.ToListAsync());
        Assert.Empty(await read.GoLiveCompletions.ToListAsync());
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersProjects()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        ProjectScene scene = await ArrangeAsync();

        await scene.StartedAsync();

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

        scene.Provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId =
            other.TenantId;

        Assert.Empty((await scene.Projects.ListAsync(new ProjectQuery())).Items);
        Assert.Equal(0, (await scene.Projects.SummaryAsync()).TotalProjects);

        // And the code is free again, because it is only taken within a
        // company.
        Assert.True((await scene.Projects.CreateAsync(ProjectScene.Planning())).Succeeded);
    }

    private Task<ProjectScene> ArrangeAsync() => new ProjectScene(fixture).ArrangeAsync();

    // Discovery, then Build waiting on it.
    private static async Task<(ProjectDto Project, Guid DiscoveryId, Guid BuildId)> ChainAsync(
        ProjectScene scene)
    {
        ProjectDto project = await scene.StartedAsync();

        ProjectDto withFirst = await scene.AddedAsync(
            project.Project.Id, ProjectScene.Work(name: "Discovery", endsIn: 40));

        Guid discoveryId = ProjectScene.TaskNamed(withFirst, "Discovery").Id;

        ProjectDto both = await scene.AddedAsync(
            project.Project.Id,
            ProjectScene.Work(
                name: "Build", startsIn: 40, endsIn: 80, dependsOn: [discoveryId]));

        return (both, discoveryId, ProjectScene.TaskNamed(both, "Build").Id);
    }
}
