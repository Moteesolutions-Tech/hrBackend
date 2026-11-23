using Motee.Application.Projects;
using Motee.Domain.Projects;

namespace Motee.Tests.Integration;

// The risk and issue register, and the go-live checklist.
//
// Written before the services.
[Collection(PostgresCollection.Name)]
public class ProjectRiskServiceTests(PostgresFixture fixture)
{
    // ── the register ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AddsARiskToTheRegister()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectRiskResult result = await scene.Risks.AddAsync(
            projectId, ProjectScene.Threat() with { OwnerEmployeeId = scene.AdaId });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        ProjectRiskDto risk = result.Risk!;

        Assert.Equal("Payroll parallel run may slip", risk.Title);
        Assert.Equal(RiskKind.Risk, risk.RiskKind);
        Assert.Equal(RiskImpact.High, risk.Impact);
        Assert.Equal("Ada Okafor", risk.OwnerName);
        Assert.False(risk.IsOverdue);
    }

    [SkippableFact]
    public async Task RefusesARiskWithNoTitle()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        Assert.Equal(
            ProjectRefusal.RiskTitleTooShort,
            (await scene.Risks.AddAsync(projectId, ProjectScene.Threat(title: " "))).Refusal);
    }

    [SkippableFact]
    public async Task RefusesAnOwnerWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        Assert.Equal(
            ProjectOutcome.EmployeeNotFound,
            (await scene.Risks.AddAsync(
                projectId,
                ProjectScene.Threat() with { OwnerEmployeeId = Guid.NewGuid() })).Outcome);
    }

    [SkippableFact]
    public async Task AnOpenRiskPastItsDateIsOverdue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectRiskResult open = await scene.Risks.AddAsync(
            projectId, ProjectScene.Threat(dueIn: -3));

        Assert.True(open.Risk!.IsOverdue);

        // A closed one is not: there is nothing left to act on.
        ProjectRiskResult closed = await scene.Risks.UpdateAsync(
            projectId,
            open.Risk.Id,
            ProjectScene.Threat(dueIn: -3, status: RiskStatus.Closed));

        Assert.False(closed.Risk!.IsOverdue);
    }

    // Open first, then worst impact: the order the register is worked through.
    [SkippableFact]
    public async Task ListsTheRegisterInTheOrderItIsWorkedThrough()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        await scene.Risks.AddAsync(
            projectId,
            ProjectScene.Threat(title: "Closed one", status: RiskStatus.Closed));

        await scene.Risks.AddAsync(
            projectId, ProjectScene.Threat(title: "Minor", impact: RiskImpact.Low));

        await scene.Risks.AddAsync(
            projectId, ProjectScene.Threat(title: "Serious", impact: RiskImpact.High));

        Assert.Equal(
            ["Serious", "Minor", "Closed one"],
            (await scene.Risks.ListAsync(projectId)).Select(risk => risk.Title));
    }

    [SkippableFact]
    public async Task TalliesTheRegister()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        await scene.Risks.AddAsync(
            projectId, ProjectScene.Threat(title: "Open and serious"));

        await scene.Risks.AddAsync(
            projectId,
            ProjectScene.Threat(title: "Already happened", kind: RiskKind.Issue));

        await scene.Risks.AddAsync(
            projectId, ProjectScene.Threat(title: "Dealt with", status: RiskStatus.Closed));

        RiskTally tally = await scene.Risks.TallyAsync(projectId);

        Assert.Equal(3, tally.Total);
        Assert.Equal(2, tally.Open);
        Assert.Equal(1, tally.Issues);
        Assert.Equal(2, tally.HighImpactOpen);

        // And the project detail carries the same count.
        ProjectDto? project = await scene.Projects.GetAsync(projectId);

        Assert.Equal(3, project!.RiskCount);
        Assert.Equal(2, project.OpenRiskCount);
    }

    [SkippableFact]
    public async Task DeletingARiskClearsItFromTheMilestoneThatNamedIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectRiskResult risk = await scene.Risks.AddAsync(
            projectId, ProjectScene.Threat());

        ProjectResult milestone = await scene.Projects.AddMilestoneAsync(
            projectId,
            ProjectScene.Date() with { RiskIds = [risk.Risk!.Id] });

        Assert.Single(milestone.Project!.Milestones[0].RiskIds);

        Assert.True((await scene.Risks.DeleteAsync(projectId, risk.Risk.Id)).Succeeded);

        ProjectDto? after = await scene.Projects.GetAsync(projectId);

        Assert.Empty(after!.Milestones[0].RiskIds);
    }

    [SkippableFact]
    public async Task ARiskFromAnotherProjectIsNotFound()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectDto other = await scene.StartedAsync(
            ProjectScene.Planning(code: "PRJ-002", name: "Payroll migration"));

        ProjectRiskResult risk = await scene.Risks.AddAsync(
            projectId, ProjectScene.Threat());

        Assert.Equal(
            ProjectOutcome.NotFound,
            (await scene.Risks.DeleteAsync(other.Project.Id, risk.Risk!.Id)).Outcome);
    }

    // ── go-live readiness ───────────────────────────────────────────────────

    // Nothing is seeded when a project is created, so this is the first read.
    [SkippableFact]
    public async Task AProjectNobodyHasOpenedStillHasTheWholeChecklist()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        GoLiveChecklistDto? checklist = await scene.GoLive.GetAsync(projectId);

        Assert.NotNull(checklist);
        Assert.Equal(GoLiveTemplate.All.Count, checklist.Items.Count);
        Assert.Equal(0, checklist.Readiness.OverallPercent);
        Assert.All(checklist.Items, item => Assert.False(item.IsDone));

        await using Motee.Infrastructure.Persistence.MoteeDbContext read = scene.Read();

        Assert.Empty(read.GoLiveCompletions);
    }

    [SkippableFact]
    public async Task TicksACheckAndCountsIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        GoLiveResult result = await scene.GoLive.SetAsync(
            projectId,
            "data.cutover-rehearsed",
            new GoLiveTickRequest { IsDone = true, Notes = "Rehearsed on the 12th." });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        GoLiveCheckDto check = result.Checklist!.Items
            .First(item => item.Key == "data.cutover-rehearsed");

        Assert.True(check.IsDone);
        Assert.Equal("Rehearsed on the 12th.", check.Notes);
        Assert.Equal(scene.ActorUserId, check.UpdatedByUserId);

        Assert.Equal(1, result.Checklist.Readiness.ByCategory[GoLiveCategory.Data].Done);
        Assert.Equal(0, result.Checklist.Readiness.ByCategory[GoLiveCategory.People].Done);
    }

    [SkippableFact]
    public async Task UntickingACheckTakesItBackOut()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        await scene.GoLive.SetAsync(
            projectId, "people.training", new GoLiveTickRequest { IsDone = true });

        GoLiveResult result = await scene.GoLive.SetAsync(
            projectId, "people.training", new GoLiveTickRequest { IsDone = false });

        Assert.Equal(0, result.Checklist!.Readiness.OverallPercent);

        // One row, moved, rather than a second tick for the same check.
        await using Motee.Infrastructure.Persistence.MoteeDbContext read = scene.Read();

        Assert.Single(read.GoLiveCompletions);
    }

    [SkippableFact]
    public async Task RefusesACheckThatIsNotOnTheList()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        Assert.Equal(
            ProjectRefusal.UnknownCheck,
            (await scene.GoLive.SetAsync(
                projectId, "people.invented", new GoLiveTickRequest { IsDone = true }))
                .Refusal);
    }

    [SkippableFact]
    public async Task TickingEverythingIsReady()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        foreach (GoLiveItem item in GoLiveTemplate.All)
        {
            await scene.GoLive.SetAsync(
                projectId, item.Key, new GoLiveTickRequest { IsDone = true });
        }

        GoLiveChecklistDto? checklist = await scene.GoLive.GetAsync(projectId);

        Assert.Equal(100, checklist!.Readiness.OverallPercent);
    }

    [SkippableFact]
    public async Task OneProjectsTicksAreNotAnothersReadiness()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        (ProjectScene scene, Guid projectId) = await ArrangeAsync();

        ProjectDto other = await scene.StartedAsync(
            ProjectScene.Planning(code: "PRJ-002", name: "Payroll migration"));

        await scene.GoLive.SetAsync(
            projectId, "people.training", new GoLiveTickRequest { IsDone = true });

        GoLiveChecklistDto? theirs = await scene.GoLive.GetAsync(other.Project.Id);

        Assert.Equal(0, theirs!.Readiness.OverallPercent);
    }

    private async Task<(ProjectScene Scene, Guid ProjectId)> ArrangeAsync()
    {
        ProjectScene scene = await new ProjectScene(fixture).ArrangeAsync();

        ProjectDto project = await scene.StartedAsync();

        return (scene, project.Project.Id);
    }
}
