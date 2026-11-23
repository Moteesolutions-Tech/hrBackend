using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Projects;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Projects;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// A company with a department and three people, ready for a project.
//
// Shared by the four project test classes rather than copied into each: they
// need the same world, and the only thing that differs between them is what
// they then do to it. Today is pinned, because overdue, due today and due this
// week are all comparisons against a date.
internal sealed class ProjectScene(PostgresFixture fixture)
{
    public static readonly DateOnly Today = new(2026, 6, 15);

    public MovableClock Clock { get; } = new(
        new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));

    public Guid TenantId { get; private set; }

    public Guid DepartmentId { get; private set; }

    public Guid AdaId { get; private set; }

    public Guid BolaId { get; private set; }

    public Guid ChidiId { get; private set; }

    // Who is doing the work. Signed in during the arrange because production
    // always has a user behind [Authorize], and the rows that record who
    // created a project or signed off a milestone point at a real account.
    public Guid ActorUserId { get; private set; }

    public ServiceProvider Provider { get; private set; } = null!;

    public async Task<ProjectScene> ArrangeAsync()
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

        TenantId = registration.TenantId;

        Provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(Clock));

        Provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = TenantId;

        DepartmentId = (await Resolve<IDepartmentService>()
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        AdaId = await HireAsync("Ada");
        BolaId = await HireAsync("Bola");
        ChidiId = await HireAsync("Chidi");

        ActorUserId = await SignInAsync(AdaId, "ada");

        return this;
    }

    public T Resolve<T>() where T : notnull =>
        Provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    public IProjectService Projects => Resolve<IProjectService>();

    public IProjectTimeService Time => Resolve<IProjectTimeService>();

    public IProjectRiskService Risks => Resolve<IProjectRiskService>();

    public IProjectGoLiveService GoLive => Resolve<IProjectGoLiveService>();

    public IProjectDocumentService Documents => Resolve<IProjectDocumentService>();

    public MoteeDbContext Read() => fixture.CreateContext(TenantId);

    public void ActAs(Guid userId) =>
        Provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

    // The signed-in user, so a sign-off or an approval has somebody's name on
    // it.
    public async Task<Guid> SignInAsync(Guid employeeId, string handle)
    {
        await using MoteeDbContext seed = Read();

        Guid userId = Guid.NewGuid();

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = userId,
            TenantId = TenantId,
            EmployeeId = employeeId,
            FirstName = char.ToUpperInvariant(handle[0]) + handle[1..],
            LastName = "Okafor",
            Email = $"{handle}-login@acme.com",
            NormalizedEmail = $"{handle.ToUpperInvariant()}-LOGIN@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        ActAs(userId);

        return userId;
    }

    private async Task<Guid> HireAsync(string first) =>
        (await Resolve<IEmployeeService>().CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = DepartmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    // ── the things under test ───────────────────────────────────────────────

    public static ProjectRequest Planning(
        string code = "PRJ-001",
        string name = "HRIS Implementation",
        ProjectStatus status = ProjectStatus.Active,
        decimal? budget = 100_000m,
        Guid? owner = null,
        int startsIn = 0,
        int endsIn = 180) => new()
        {
            Code = code,
            Name = name,
            Status = status,
            Priority = ProjectPriority.High,
            Client = "Internal — People team",
            OwnerEmployeeId = owner,
            StartOn = Today.AddDays(startsIn),
            EndOn = Today.AddDays(endsIn),
            Budget = budget,
            CostCentreCode = "hr001",
        };

    public static ProjectTaskRequest Work(
        string name = "Data migration mapping",
        ProjectTaskStatus status = ProjectTaskStatus.NotStarted,
        int percentComplete = 0,
        int startsIn = 0,
        int endsIn = 10,
        Guid? assignee = null,
        decimal? estimatedHours = null,
        IReadOnlyList<Guid>? dependsOn = null) => new()
        {
            Name = name,
            Status = status,
            PercentComplete = percentComplete,
            StartOn = Today.AddDays(startsIn),
            EndOn = Today.AddDays(endsIn),
            AssigneeEmployeeId = assignee,
            EstimatedHours = estimatedHours,
            DependsOn = dependsOn,
            Phase = "Discovery",
        };

    public static MilestoneRequest Date(
        string name = "UAT sign-off",
        MilestoneStatus status = MilestoneStatus.NotStarted,
        int targetIn = 60,
        Guid? responsible = null,
        IReadOnlyList<Guid>? taskIds = null) => new()
        {
            Name = name,
            Status = status,
            TargetOn = Today.AddDays(targetIn),
            ResponsibleEmployeeId = responsible,
            TaskIds = taskIds,
        };

    public static AllocationRequest Booking(
        Guid employeeId,
        decimal percent = 50m,
        decimal? hourlyRate = null,
        string? role = null) => new()
        {
            EmployeeId = employeeId,
            ProjectRole = role,
            AllocationPercent = percent,
            HourlyRate = hourlyRate,
        };

    public static TimeEntryRequest Hours(
        Guid employeeId,
        decimal hours = 7.5m,
        Guid? taskId = null,
        int workedDaysAgo = 0,
        TimeEntryStatus? status = null) => new()
        {
            EmployeeId = employeeId,
            TaskId = taskId,
            WorkedOn = Today.AddDays(-workedDaysAgo),
            Hours = hours,
            Status = status,
        };

    public static RiskRequest Threat(
        string title = "Payroll parallel run may slip",
        RiskKind kind = RiskKind.Risk,
        RiskImpact impact = RiskImpact.High,
        RiskStatus status = RiskStatus.Open,
        int? dueIn = null,
        IReadOnlyList<Guid>? taskIds = null,
        IReadOnlyList<Guid>? milestoneIds = null) => new()
        {
            RiskKind = kind,
            Title = title,
            Description = "The December run needs two cycles of reconciliation.",
            Impact = impact,
            Status = status,
            DueOn = dueIn is int days ? Today.AddDays(days) : null,
            RelatedTaskIds = taskIds,
            RelatedMilestoneIds = milestoneIds,
        };

    public async Task<ProjectDto> StartedAsync(ProjectRequest? request = null)
    {
        ProjectResult result = await Projects.CreateAsync(request ?? Planning());

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Project!;
    }

    public async Task<ProjectDto> AddedAsync(Guid projectId, ProjectTaskRequest request)
    {
        ProjectResult result = await Projects.AddTaskAsync(projectId, request);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Project!;
    }

    public static ProjectTaskDto TaskNamed(ProjectDto project, string name) =>
        project.Tasks.First(task => task.Name == name);

    public static ProjectMilestoneDto MilestoneNamed(ProjectDto project, string name) =>
        project.Milestones.First(milestone => milestone.Name == name);
}
