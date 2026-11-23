using Motee.Application.Common;
using Motee.Application.Projects;
using Motee.Domain.Projects;

namespace Motee.Tests.Integration;

// Hours logged against a project.
//
// The two rules the client cannot enforce: time can only be logged by somebody
// actually on the project, and a person's day cannot add up to more than a day.
// It guards one entry at a time, so twenty hours on each of three projects
// passes it.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class ProjectTimeServiceTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task LogsTimeAgainstAProject()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        TimeEntryResult result = await scene.Time.LogAsync(
            projectId, ProjectScene.Hours(scene.AdaId, 7.5m));

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        TimeEntryDto entry = result.Entry!;

        Assert.Equal(7.5m, entry.Hours);
        Assert.Equal("Ada Okafor", entry.EmployeeName);
        Assert.Equal("HRIS Implementation", entry.ProjectName);

        // Straight to submitted: the screen's button is "log time", not "save
        // a draft".
        Assert.Equal(TimeEntryStatus.Submitted, entry.Status);
        Assert.Null(entry.TaskId);
    }

    [SkippableFact]
    public async Task OnlySomebodyOnTheProjectCanLogTimeToIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        Assert.Equal(
            ProjectRefusal.NotAllocated,
            (await scene.Time.LogAsync(projectId, ProjectScene.Hours(scene.ChidiId)))
                .Refusal);
    }

    [SkippableFact]
    public async Task RefusesMoreHoursThanADayHas()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        Assert.Equal(
            ProjectRefusal.HoursBeyondADay,
            (await scene.Time.LogAsync(projectId, ProjectScene.Hours(scene.AdaId, 25m)))
                .Refusal);
    }

    [SkippableFact]
    public async Task RefusesTimeThatIsNotTime()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        Assert.Equal(
            ProjectRefusal.HoursNotPositive,
            (await scene.Time.LogAsync(projectId, ProjectScene.Hours(scene.AdaId, 0m)))
                .Refusal);
    }

    // The day is the limit, not the entry.
    [SkippableFact]
    public async Task RefusesADayThatAddsUpToMoreThanADayAcrossProjects()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectDto second = await scene.StartedAsync(
            ProjectScene.Planning(code: "PRJ-002", name: "Payroll migration"));

        await scene.Projects.SetAllocationAsync(
            second.Project.Id, ProjectScene.Booking(scene.AdaId));

        Assert.True(
            (await scene.Time.LogAsync(projectId, ProjectScene.Hours(scene.AdaId, 20m)))
                .Succeeded);

        Assert.Equal(
            ProjectRefusal.DayBeyondADay,
            (await scene.Time.LogAsync(
                second.Project.Id, ProjectScene.Hours(scene.AdaId, 8m))).Refusal);
    }

    [SkippableFact]
    public async Task TimeSentBackDoesNotCountTowardsTheDay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        TimeEntryResult first = await scene.Time.LogAsync(
            projectId, ProjectScene.Hours(scene.AdaId, 20m));

        await scene.Time.SetStatusAsync(
            projectId, first.Entry!.Id, TimeEntryStatus.Rejected);

        // Rejected hours were not worked, so they do not hold up a correction.
        Assert.True(
            (await scene.Time.LogAsync(projectId, ProjectScene.Hours(scene.AdaId, 8m)))
                .Succeeded);
    }

    [SkippableFact]
    public async Task RefusesATaskFromAnotherProject()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectDto other = await scene.StartedAsync(
            ProjectScene.Planning(code: "PRJ-002", name: "Payroll migration"));

        ProjectDto withTask = await scene.AddedAsync(
            other.Project.Id, ProjectScene.Work(name: "Elsewhere"));

        Assert.Equal(
            ProjectRefusal.UnknownTask,
            (await scene.Time.LogAsync(
                projectId,
                ProjectScene.Hours(scene.AdaId, taskId: withTask.Tasks[0].Id))).Refusal);
    }

    [SkippableFact]
    public async Task ApprovingTimeRecordsWhoDidItAndWhen()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        TimeEntryResult logged = await scene.Time.LogAsync(
            projectId, ProjectScene.Hours(scene.AdaId, 8m));

        TimeEntryResult approved = await scene.Time.SetStatusAsync(
            projectId, logged.Entry!.Id, TimeEntryStatus.Approved);

        Assert.Equal(TimeEntryStatus.Approved, approved.Entry!.Status);
        Assert.Equal(scene.ActorUserId, approved.Entry.ResolvedByUserId);
        Assert.NotNull(approved.Entry.ResolvedAt);
    }

    [SkippableFact]
    public async Task SendingTimeBackClearsTheApproval()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        TimeEntryResult logged = await scene.Time.LogAsync(
            projectId, ProjectScene.Hours(scene.AdaId, 8m));

        await scene.Time.SetStatusAsync(projectId, logged.Entry!.Id, TimeEntryStatus.Approved);

        TimeEntryResult back = await scene.Time.SetStatusAsync(
            projectId, logged.Entry.Id, TimeEntryStatus.Submitted);

        // Back with the submitter, so there is nobody who settled it.
        Assert.Null(back.Entry!.ResolvedAt);
        Assert.Null(back.Entry.ResolvedByUserId);
    }

    // Only approved time is a cost.
    [SkippableFact]
    public async Task UnapprovedTimeIsNotYetSpend()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        TimeEntryResult logged = await scene.Time.LogAsync(
            projectId, ProjectScene.Hours(scene.AdaId, 8m));

        ProjectDto? before = await scene.Projects.GetAsync(projectId);

        Assert.Equal(0m, before!.Project.Spend);
        Assert.Equal(0m, before.ApprovedHours);

        await scene.Time.SetStatusAsync(projectId, logged.Entry!.Id, TimeEntryStatus.Approved);

        ProjectDto? after = await scene.Projects.GetAsync(projectId);

        Assert.Equal(800m, after!.Project.Spend);
        Assert.Equal(8m, after.ApprovedHours);
    }

    [SkippableFact]
    public async Task TalliesWhatIsApprovedAndWhatIsWaiting()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        TimeEntryResult first = await scene.Time.LogAsync(
            projectId, ProjectScene.Hours(scene.AdaId, 8m, workedDaysAgo: 2));

        await scene.Time.LogAsync(
            projectId, ProjectScene.Hours(scene.AdaId, 5m, workedDaysAgo: 1));

        await scene.Time.SetStatusAsync(projectId, first.Entry!.Id, TimeEntryStatus.Approved);

        TimeTally tally = await scene.Time.TallyAsync(projectId);

        Assert.Equal(8m, tally.ApprovedHours);
        Assert.Equal(5m, tally.AwaitingApprovalHours);
        Assert.Equal(13m, tally.TotalHours);
        Assert.Equal(2, tally.Entries);
    }

    [SkippableFact]
    public async Task ListsTheNewestFirstAndFiltersByPerson()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        await scene.Projects.SetAllocationAsync(
            projectId, ProjectScene.Booking(scene.BolaId));

        await scene.Time.LogAsync(
            projectId, ProjectScene.Hours(scene.AdaId, 8m, workedDaysAgo: 3));
        await scene.Time.LogAsync(
            projectId, ProjectScene.Hours(scene.BolaId, 4m, workedDaysAgo: 1));

        PagedResult<TimeEntryDto> all = await scene.Time.ListAsync(
            new TimeEntryQuery { ProjectId = projectId });

        Assert.Equal(["Bola Okafor", "Ada Okafor"], all.Items.Select(row => row.EmployeeName));

        PagedResult<TimeEntryDto> mine = await scene.Time.ListAsync(
            new TimeEntryQuery { ProjectId = projectId, EmployeeId = scene.AdaId });

        Assert.Equal(8m, Assert.Single(mine.Items).Hours);
    }

    [SkippableFact]
    public async Task DeletesAnEntry()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        TimeEntryResult logged = await scene.Time.LogAsync(
            projectId, ProjectScene.Hours(scene.AdaId, 8m));

        Assert.True((await scene.Time.DeleteAsync(projectId, logged.Entry!.Id)).Succeeded);
        Assert.Equal(0, (await scene.Time.TallyAsync(projectId)).Entries);
    }

    [SkippableFact]
    public async Task AnEntryFromAnotherProjectIsNotFound()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectDto other = await scene.StartedAsync(
            ProjectScene.Planning(code: "PRJ-002", name: "Payroll migration"));

        TimeEntryResult logged = await scene.Time.LogAsync(
            projectId, ProjectScene.Hours(scene.AdaId, 8m));

        Assert.Equal(
            ProjectOutcome.NotFound,
            (await scene.Time.DeleteAsync(other.Project.Id, logged.Entry!.Id)).Outcome);
    }

    // A project with somebody on it at a hundred an hour.
    private async Task<(ProjectScene Scene, Guid ProjectId)> ArrangeAsync()
    {
        ProjectScene scene = await new ProjectScene(fixture).ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        await scene.Projects.SetAllocationAsync(
            project.Project.Id, ProjectScene.Booking(scene.AdaId, hourlyRate: 100m));

        return (scene, project.Project.Id);
    }
}
