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

// Workflow templates, out of the database.
//
// Three things worth proving beyond the plumbing. Registering a company seeds
// the four flows the product fires itself, and re-seeding does not write a
// second copy. A seeded flow can be switched off but not rewritten. And the
// builder is told whether work would actually land on anybody, which is the
// question that decides whether a workflow does anything at all.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class WorkflowServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly Today = new(2026, 6, 15);

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _adaId;
    private Guid _hrAdminLevelId;
    private Guid _actorUserId;

    // ── the seeded flows ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RegisteringACompanySeedsTheFlowsTheProductFires()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PagedResult<WorkflowRowDto> listed = await Workflows(provider)
            .ListAsync(new WorkflowQuery { PageSize = 50 });

        Assert.Equal(
            [
                "Standard Offboarding",
                "Standard Onboarding",
                "Standard Preboarding",
                "Standard Recruitment",
            ],
            listed.Items.Select(row => row.Title).Order(StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task TheSeededFlowsAreLiveAndCarryTheirWork()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PagedResult<WorkflowRowDto> listed = await Workflows(provider)
            .ListAsync(new WorkflowQuery { PageSize = 50 });

        Assert.All(listed.Items, row =>
        {
            Assert.Equal(WorkflowKind.System, row.WorkflowKind);

            // Live from day one, unlike one somebody writes: the events that
            // trigger them are already firing.
            Assert.Equal(WorkflowStatus.Active, row.Status);
            Assert.True(row.IsRunnable);
            Assert.Equal(WorkflowTriggerMode.Automatic, row.TriggerMode);
            Assert.NotNull(row.TriggerEvent);
            Assert.True(row.TaskCount > 0);
        });

        // Sixteen for recruitment, fourteen for preboarding, eleven for
        // onboarding and twelve for offboarding.
        Assert.Equal(53, listed.Items.Sum(row => row.TaskCount));
    }

    [SkippableFact]
    public async Task SeedingACompanyThatAlreadyHasThemChangesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        IReadOnlyList<AccessLevel> levels = await read.AccessLevels.ToListAsync();

        await provider.CreateScope().ServiceProvider
            .GetRequiredService<Motee.Infrastructure.Workflows.WorkflowSeeder>()
            .SeedAsync(_tenantId, levels);

        Assert.Equal(4, (await Workflows(provider).SummaryAsync()).Total);
    }

    [SkippableFact]
    public async Task TheSeededWorkNamesAFunctionRatherThanAPerson()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        WorkflowDto onboarding = await SeededAsync(provider, "Standard Onboarding");

        Assert.All(onboarding.Tasks, task =>
        {
            // A named individual on a template is a workflow that breaks when
            // they leave.
            Assert.Equal(AssigneeKind.AccessLevel, task.AssigneeKind);
            Assert.NotNull(task.AssigneeAccessLevelId);
            Assert.NotNull(task.AssigneeAccessLevelName);
        });
    }

    [SkippableFact]
    public async Task ASeededFlowCanBeSwitchedOffButNotRewritten()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        WorkflowDto seeded = await SeededAsync(provider, "Standard Offboarding");

        // A company that does not offboard through the product should be able
        // to say so.
        WorkflowResult archived = await Workflows(provider)
            .SetStatusAsync(seeded.Workflow.Id, WorkflowStatus.Archived);

        Assert.True(archived.Succeeded, $"{archived.Outcome} / {archived.Refusal}");
        Assert.False(archived.Workflow!.Workflow.IsRunnable);

        // Rewriting it is a different matter: the product's own screens fire it.
        Assert.Equal(
            WorkflowOutcome.Seeded,
            (await Workflows(provider).UpdateAsync(seeded.Workflow.Id, Drafting())).Outcome);

        Assert.Equal(
            WorkflowOutcome.Seeded,
            (await Workflows(provider).DeleteAsync(seeded.Workflow.Id)).Outcome);
    }

    // ── writing one ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ANewWorkflowStartsAsADraft()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        WorkflowResult result = await Workflows(provider).CreateAsync(Drafting());

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        WorkflowRowDto row = result.Workflow!.Workflow;

        // Nothing goes live the moment it is saved.
        Assert.Equal(WorkflowStatus.Draft, row.Status);
        Assert.False(row.IsRunnable);
        Assert.Equal(1, row.Version);
        Assert.Equal(WorkflowKind.Custom, row.WorkflowKind);
    }

    [SkippableFact]
    public async Task RefusesAWorkflowWithNoWork()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            WorkflowRefusal.NoTasks,
            (await Workflows(provider).CreateAsync(Drafting() with { Tasks = [] })).Refusal);
    }

    [SkippableFact]
    public async Task RefusesADepartmentScopeWithNoDepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            WorkflowRefusal.DepartmentRequired,
            (await Workflows(provider).CreateAsync(
                Drafting() with { ScopeKind = WorkflowScopeKind.Department })).Refusal);
    }

    [SkippableFact]
    public async Task RefusesAnAutomaticWorkflowWithNoSchedule()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            WorkflowRefusal.ScheduleRequired,
            (await Workflows(provider).CreateAsync(
                Drafting() with { TriggerMode = WorkflowTriggerMode.Automatic })).Refusal);
    }

    [SkippableFact]
    public async Task RefusesWorkAssignedToAnAccessLevelThatDoesNotExist()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            WorkflowOutcome.AccessLevelNotFound,
            (await Workflows(provider).CreateAsync(
                Drafting(accessLevelId: Guid.NewGuid()))).Outcome);
    }

    [SkippableFact]
    public async Task RefusesWorkAssignedToSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        WorkflowRequest request = Drafting() with
        {
            Tasks =
            [
                new WorkflowTaskRequest
                {
                    Title = "Collect the signed contract",
                    AssigneeKind = AssigneeKind.Employee,
                    AssigneeEmployeeId = Guid.NewGuid(),
                },
            ],
        };

        Assert.Equal(
            WorkflowOutcome.EmployeeNotFound,
            (await Workflows(provider).CreateAsync(request)).Outcome);
    }

    [SkippableFact]
    public async Task RefusesWorkThatWaitsOnSomethingLater()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid level = await LevelAsync(Role.HrAdmin);

        WorkflowRequest request = Drafting() with
        {
            Tasks =
            [
                Step("First", level, dependsOn: [1]),
                Step("Second", level),
            ],
        };

        Assert.Equal(
            WorkflowRefusal.DependencyComesLater,
            (await Workflows(provider).CreateAsync(request)).Refusal);
    }

    // Dependencies come in as positions, because the ids are minted on save.
    [SkippableFact]
    public async Task ResolvesDependenciesFromTheirPositionsAndReportsThemBack()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid level = await LevelAsync(Role.HrAdmin);

        WorkflowResult result = await Workflows(provider).CreateAsync(
            Drafting() with
            {
                Tasks =
                [
                    Step("Sign the contract", level),
                    Step("Activate the account", level, dependsOn: [0]),
                ],
            });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        WorkflowTaskDto second = result.Workflow!.Tasks[1];

        Assert.Equal([result.Workflow.Tasks[0].Id], second.DependsOn);
        Assert.Equal([0], second.DependsOnPositions);
    }

    // The question a builder cannot answer on its own, and the one that decides
    // whether a workflow does anything.
    [SkippableFact]
    public async Task SaysWhetherWorkWouldLandOnAnybody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid hrAdmin = await LevelAsync(Role.HrAdmin);

        // A level that exists but that nobody is behind — the case that makes a
        // workflow look fine and do nothing.
        Guid recruiter = await LevelAsync(Role.Recruiter);

        await HoldsAsync(hrAdmin);

        WorkflowResult result = await Workflows(provider).CreateAsync(
            Drafting() with
            {
                Tasks =
                [
                    Step("Somebody holds this", hrAdmin),
                    Step("Nobody holds this", recruiter),
                ],
            });

        Assert.Equal(["Ada Okafor"], result.Workflow!.Tasks[0].WouldLandOn);
        Assert.Empty(result.Workflow.Tasks[1].WouldLandOn);

        Assert.True((await Workflows(provider).SummaryAsync()).TasksWithNoOwner > 0);
    }

    [SkippableFact]
    public async Task ADepartmentScopedWorkflowDoesNotReachOutsideIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid hrAdmin = await LevelAsync(Role.HrAdmin);

        await HoldsAsync(hrAdmin);

        Guid otherDepartment = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Finance", Code = "FIN" }))
            .Department!.Id;

        WorkflowResult result = await Workflows(provider).CreateAsync(
            Drafting(accessLevelId: hrAdmin) with
            {
                ScopeKind = WorkflowScopeKind.Department,
                ScopeDepartmentId = otherDepartment,
            });

        // Ada is in Engineering, so a workflow scoped to Finance does not reach
        // her — which is the point of scoping it.
        Assert.Empty(result.Workflow!.Tasks[0].WouldLandOn);
    }

    // ── editing one ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RetitlingAWorkflowDoesNotMakeANewVersion()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        WorkflowDto created = (await Workflows(provider).CreateAsync(Drafting())).Workflow!;

        WorkflowResult renamed = await Workflows(provider).UpdateAsync(
            created.Workflow.Id,
            Drafting() with { Title = "Contractor onboarding, revised" });

        // The version tracks what the workflow does, so a live run is not
        // invalidated by a typo fix.
        Assert.Equal(1, renamed.Workflow!.Workflow.Version);
    }

    [SkippableFact]
    public async Task ChangingTheWorkMakesANewVersion()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid level = await LevelAsync(Role.HrAdmin);

        WorkflowDto created = (await Workflows(provider).CreateAsync(Drafting())).Workflow!;

        WorkflowResult changed = await Workflows(provider).UpdateAsync(
            created.Workflow.Id,
            Drafting() with
            {
                Tasks = [Step("Collect the signed contract", level), Step("And file it", level)],
            });

        Assert.Equal(2, changed.Workflow!.Workflow.Version);
        Assert.Equal(2, changed.Workflow.Tasks.Count);
    }

    [SkippableFact]
    public async Task MovingToManualClearsTheScheduleRatherThanLeavingItToConfuse()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        WorkflowDto created = (await Workflows(provider).CreateAsync(
            Drafting() with
            {
                TriggerMode = WorkflowTriggerMode.Automatic,
                ScheduleKind = ScheduleKind.Relative,
                TriggerEvent = WorkflowTriggerEvent.OnboardingInitiated,
                OffsetValue = 2,
                OffsetUnit = ScheduleOffsetUnit.Days,
            })).Workflow!;

        Assert.Equal(
            WorkflowTriggerEvent.OnboardingInitiated, created.Workflow.TriggerEvent);

        WorkflowResult manual = await Workflows(provider).UpdateAsync(
            created.Workflow.Id,
            Drafting() with { TriggerMode = WorkflowTriggerMode.Manual });

        Assert.Null(manual.Workflow!.Workflow.TriggerEvent);
        Assert.Null(manual.Workflow.Workflow.ScheduleKind);
        Assert.Null(manual.Workflow.Workflow.OffsetValue);
    }

    [SkippableFact]
    public async Task DeletesAWorkflowSomebodyWrote()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        WorkflowDto created = (await Workflows(provider).CreateAsync(Drafting())).Workflow!;

        Assert.True((await Workflows(provider).DeleteAsync(created.Workflow.Id)).Succeeded);

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        Assert.Empty(await read.WorkflowTasks
            .Where(task => task.WorkflowId == created.Workflow.Id)
            .ToListAsync());
    }

    // ── what the list says ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task ListsOnlyWhatWouldActuallyFire()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        // A draft, and one dated for next month.
        await Workflows(provider).CreateAsync(Drafting(title: "Still being written"));

        WorkflowDto later = (await Workflows(provider).CreateAsync(
            Drafting(title: "Starts next month") with
            {
                Status = WorkflowStatus.Active,
                EffectiveFrom = Today.AddMonths(1),
            })).Workflow!;

        Assert.False(later.Workflow.IsRunnable);

        PagedResult<WorkflowRowDto> runnable = await Workflows(provider)
            .ListAsync(new WorkflowQuery { RunnableOnly = true, PageSize = 50 });

        // The four seeded ones, and neither of the two just written.
        Assert.Equal(4, runnable.TotalItems);

        WorkflowSummaryDto summary = await Workflows(provider).SummaryAsync();

        Assert.Equal(6, summary.Total);
        Assert.Equal(1, summary.Draft);
        Assert.Equal(1, summary.NotYetEffective);
    }

    [SkippableFact]
    public async Task DoesNotTreatASearchTermAsAWildcard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Workflows(provider).CreateAsync(Drafting(title: "100% remote onboarding"));

        Assert.Equal(
            1,
            (await Workflows(provider).ListAsync(new WorkflowQuery { Search = "100%" }))
                .TotalItems);
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersWorkflows()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await Workflows(provider).CreateAsync(Drafting(title: "Ours alone"));

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

        PagedResult<WorkflowRowDto> theirs = await Workflows(provider)
            .ListAsync(new WorkflowQuery { PageSize = 50 });

        // Their own four seeded flows, and nothing of ours.
        Assert.Equal(4, theirs.TotalItems);
        Assert.DoesNotContain("Ours alone", theirs.Items.Select(row => row.Title));
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

        _adaId = (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

        _hrAdminLevelId = await LevelAsync(Role.HrAdmin);

        // Signed in, because production always has a user behind [Authorize]
        // and the row that records who last changed a workflow points at a
        // real account.
        _actorUserId = await SignInAsync(provider);

        return provider;
    }

    private async Task<Guid> SignInAsync(ServiceProvider provider)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid userId = Guid.NewGuid();

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = userId,
            TenantId = _tenantId,
            EmployeeId = _adaId,
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada-login@acme.com",
            NormalizedEmail = "ADA-LOGIN@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

        return userId;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IWorkflowService Workflows(ServiceProvider provider) =>
        Resolve<IWorkflowService>(provider);

    private static async Task<WorkflowDto> SeededAsync(
        ServiceProvider provider,
        string title)
    {
        PagedResult<WorkflowRowDto> listed = await Workflows(provider)
            .ListAsync(new WorkflowQuery { PageSize = 50 });

        Guid id = listed.Items.First(row => row.Title == title).Id;

        return (await Workflows(provider).GetAsync(id))!;
    }

    private async Task<Guid> LevelAsync(Role role)
    {
        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        return await read.AccessLevels
            .Where(level => level.TemplateSlug == Roles.ToSlug(role))
            .Select(level => level.Id)
            .FirstAsync();
    }

    // Puts the acting user behind an access level, so work assigned to it lands
    // on their employee record.
    private async Task HoldsAsync(Guid accessLevelId)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.UserAccessLevels.Add(new UserAccessLevel
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            UserId = _actorUserId,
            AccessLevelId = accessLevelId,
            AssignedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    private WorkflowRequest Drafting(
        string title = "Contractor onboarding",
        Guid? accessLevelId = null) => new()
        {
            Title = title,
            Description = "A shorter flow for people who are not on the payroll.",
            TriggerMode = WorkflowTriggerMode.Manual,
            ScopeKind = WorkflowScopeKind.All,
            Tasks = [Step("Collect the signed contract", accessLevelId ?? _hrAdminLevelId)],
        };

    private static WorkflowTaskRequest Step(
        string title,
        Guid accessLevelId,
        IReadOnlyList<int>? dependsOn = null) => new()
        {
            Title = title,
            AssigneeKind = AssigneeKind.AccessLevel,
            AssigneeAccessLevelId = accessLevelId,
            Priority = WorkflowTaskPriority.Normal,
            DependsOnPositions = dependsOn,
        };
}
