using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Dashboard;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Offboarding;
using Motee.Domain.Onboarding;
using Motee.Domain.Organisation;
using Motee.Domain.Recruitment;
using Motee.Domain.Workforce;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The lifecycle overview: how many people sit at each stage from workforce
// planning through to offboarding.
//
// The screen is an eight-stage model and every stage card was reading a Redux
// slice, which means the page only ever showed what the browser happened to
// have loaded. Two of the eight had no data source at all — the frontend fixed
// pre-employment and left develop-perform-retain blank, so the widest stage in
// the model, everybody actually employed, showed nothing.
//
// Counts only. The screen shows a number and its own wording per card, so a
// label from here would be a second copy of their copy — and the names behind
// the counts span candidates, joiners and leavers across five modules, which is
// a lot of personal data to ship for a card that displays an integer.
[Collection(PostgresCollection.Name)]
public class LifecycleDashboardTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private readonly MovableClock _clock = new(
        new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;

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

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IDashboardService Dashboard(ServiceProvider provider) =>
        Resolve<IDashboardService>(provider);

    private static async Task<int> CountAsync(ServiceProvider provider, string key)
    {
        LifecycleDashboardDto board = await Dashboard(provider).LifecycleAsync();

        return board.Stages.FirstOrDefault(stage => stage.Key == key)?.Count ?? -1;
    }

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        EmployeeStatus status = EmployeeStatus.Active) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Adeyemi",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = status,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    // ── every stage is reported ─────────────────────────────────────────────

    [SkippableFact]
    public async Task EveryStageOfTheModelIsReportedEvenAtZero()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        LifecycleDashboardDto board = await Dashboard(provider).LifecycleAsync();

        // The screen draws eight cards whether or not anything is at that stage.
        // Omitting the empty ones would make a quiet month look like a broken
        // page, and the client would have to know the model to fill the gaps.
        Assert.Equal(
            [
                "workforce-planning", "requisition", "attract", "select",
                "pre-employment", "onboard", "develop-perform-retain", "offboard",
            ],
            board.Stages.Select(stage => stage.Key).ToArray());

        Assert.All(board.Stages, stage => Assert.True(stage.Count >= 0));
    }

    [SkippableFact]
    public async Task TheDayEverythingWasCountedAgainstIsOnTheAnswer()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        LifecycleDashboardDto board = await Dashboard(provider).LifecycleAsync();

        Assert.Equal(new DateOnly(2026, 9, 22), board.AsOf);
    }

    // ── the stage the frontend left blank ───────────────────────────────────

    // Develop, Perform & Retain is everybody in post, and its card rendered
    // empty because no slice answered it. It is the widest stage in the model.
    [SkippableFact]
    public async Task DevelopPerformRetainCountsEverybodyInPost()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada");
        await HireAsync(provider, "Ben", EmployeeStatus.Probation);
        await HireAsync(provider, "Cara", EmployeeStatus.OnLeave);

        // Not in post: has not started, and has left.
        await HireAsync(provider, "Dele", EmployeeStatus.Pending);
        await HireAsync(provider, "Emeka", EmployeeStatus.Inactive);

        Assert.Equal(3, await CountAsync(provider, "develop-perform-retain"));
    }

    // Somebody serving notice is still employed — on payroll, accruing leave,
    // holding a laptop. They belong in post, which is what every other module
    // already says via EmployeeLifecycle.InPost.
    [SkippableFact]
    public async Task SomebodyServingNoticeIsStillInPost()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada");
        await HireAsync(provider, "Ben", EmployeeStatus.Offboarding);

        Assert.Equal(2, await CountAsync(provider, "develop-perform-retain"));
    }

    // ── planning and requisitions ───────────────────────────────────────────

    [SkippableFact]
    public async Task WorkforcePlanningCountsRequestsNotYetConverted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid requisitionId = await RequisitionIdAsync();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.WorkforceRequests.AddRange(
                Request(),
                Request(),

                // Converted is the end of this stage: the request became a
                // requisition and is counted there instead. Counting it twice
                // would make the funnel wider than the work going through it.
                Request(convertedTo: requisitionId),
                Request(cancelled: true));

            await seed.SaveChangesAsync();
        }

        Assert.Equal(2, await CountAsync(provider, "workforce-planning"));
    }

    [SkippableFact]
    public async Task AttractCountsRequisitionsActuallyRecruiting()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.JobRequisitions.AddRange(
                Requisition(RequisitionStatus.Open),
                Requisition(RequisitionStatus.Interviewing),
                Requisition(RequisitionStatus.Filled),
                Requisition(RequisitionStatus.Cancelled));

            await seed.SaveChangesAsync();
        }

        // Through the domain's own IsRecruiting rather than a list repeated
        // here, or the dashboard and the recruitment board would disagree about
        // what "open" means the first time one of them changed.
        Assert.Equal(2, await CountAsync(provider, "attract"));
    }

    // ── selection and pre-employment ────────────────────────────────────────

    [SkippableFact]
    public async Task SelectCountsCandidatesStillInPlay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid requisitionId = await RequisitionIdAsync();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Candidates.AddRange(
                Candidate(requisitionId, CandidateStage.Applicants),
                Candidate(requisitionId, CandidateStage.Interview),
                Candidate(requisitionId, CandidateStage.Offer),
                Candidate(requisitionId, CandidateStage.Applicants, CandidateStatus.Rejected));

            await seed.SaveChangesAsync();
        }

        Assert.Equal(3, await CountAsync(provider, "select"));
    }

    // Between "we want them" and "they have started": a hired candidate who is
    // not yet an employee, plus a joiner whose onboarding has not begun.
    [SkippableFact]
    public async Task PreEmploymentCountsHiredCandidatesAndJoinersNotYetStarted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid requisitionId = await RequisitionIdAsync();
        Guid joinerId = await HireAsync(provider, "Ada", EmployeeStatus.Pending);

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            // Hired, and nobody has turned them into an employee yet.
            seed.Candidates.Add(Candidate(requisitionId, CandidateStage.Hired));

            // Hired and already an employee: they have moved on to onboarding,
            // and counting them here as well would double the stage.
            Candidate converted = Candidate(requisitionId, CandidateStage.Hired);
            converted.CreatedEmployeeId = joinerId;
            seed.Candidates.Add(converted);

            await seed.SaveChangesAsync();
        }

        // The joiner's own pre-boarding record is the second: hiring anybody
        // creates one, so there is nothing to insert here.
        Assert.Equal(2, await CountAsync(provider, "pre-employment"));
    }

    // Every new hire gets a pre-boarding record whichever route they came in
    // by, which includes the staff a new tenant imports on day one. Counting
    // the stage alone would tell a company that migrated 200 people it has 200
    // in pre-employment.
    [SkippableFact]
    public async Task StaffAlreadyWorkingAreNotCountedAsPreEmployment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Ada");
        await HireAsync(provider, "Ben", EmployeeStatus.Probation);
        await HireAsync(provider, "Cara", EmployeeStatus.OnLeave);

        // All three are at pre-boarding as far as the onboarding table is
        // concerned, and none of them is pre-employment.
        Assert.Equal(0, await CountAsync(provider, "pre-employment"));

        await HireAsync(provider, "Dele", EmployeeStatus.Pending);

        Assert.Equal(1, await CountAsync(provider, "pre-employment"));
    }

    [SkippableFact]
    public async Task OnboardCountsJoinersUnderwayButNotFinished()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada");
        Guid ben = await HireAsync(provider, "Ben");
        Guid cara = await HireAsync(provider, "Cara");

        // Moved rather than inserted: hiring already created each record, one
        // per person and enforced by the database.
        await MoveAsync(ada, OnboardingStage.PreBoarding);
        await MoveAsync(ben, OnboardingStage.FirstWeek);
        await MoveAsync(cara, OnboardingStage.Completed);

        // Pre-boarding has not started yet and belongs to the stage before;
        // completed is no longer onboarding.
        Assert.Equal(1, await CountAsync(provider, "onboard"));
    }

    // ── offboarding ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OffboardCountsLeaversStillInProgress()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid ada = await HireAsync(provider, "Ada", EmployeeStatus.Offboarding);
        Guid ben = await HireAsync(provider, "Ben", EmployeeStatus.Offboarding);
        Guid cara = await HireAsync(provider, "Cara", EmployeeStatus.Inactive);

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.OffboardingRecords.Add(Offboarding(ada, OffboardingStatus.InProgress));
            seed.OffboardingRecords.Add(Offboarding(ben, OffboardingStatus.Pending));
            seed.OffboardingRecords.Add(Offboarding(cara, OffboardingStatus.Completed));

            await seed.SaveChangesAsync();
        }

        // Through OffboardingLifecycle.IsOpen, which the offboarding board
        // already uses, rather than a second list of what counts as open.
        Assert.Equal(2, await CountAsync(provider, "offboard"));
    }

    // ── seeding helpers ─────────────────────────────────────────────────────

    // A workforce request has no status column: WorkforceRequests.Derive reads
    // it off the approval chain, the conversion and the cancellation. So these
    // say what the rule actually looks at rather than naming a status, which is
    // also the only way to seed a state the rule agrees exists.
    private WorkforceRequest Request(
        Guid? convertedTo = null,
        bool cancelled = false) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        Position = "Second site engineer",
        Justification = "Second site opens in January.",
        DepartmentId = _departmentId,

        // Becoming a requisition is the whole definition of Converted — there
        // is no flag, and the foreign key is the record of it.
        RequisitionId = convertedTo,
        CancelledAt = cancelled ? _clock.GetUtcNow() : null,
        CreatedAt = _clock.GetUtcNow(),
        UpdatedAt = _clock.GetUtcNow(),
    };

    private JobRequisition Requisition(RequisitionStatus status) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        Reference = $"REQ-{Guid.NewGuid():N}"[..10],
        PositionTitle = "Engineer",
        DepartmentId = _departmentId,
        Status = status,
        Openings = 1,
        CreatedAt = _clock.GetUtcNow(),
        UpdatedAt = _clock.GetUtcNow(),
    };

    private async Task<Guid> RequisitionIdAsync()
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        JobRequisition requisition = Requisition(RequisitionStatus.Open);

        seed.JobRequisitions.Add(requisition);

        await seed.SaveChangesAsync();

        return requisition.Id;
    }

    private Candidate Candidate(
        Guid requisitionId,
        CandidateStage stage,
        CandidateStatus status = CandidateStatus.Active) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        RequisitionId = requisitionId,
        FirstName = "Cand",
        LastName = $"{Guid.NewGuid():N}"[..6],
        Email = $"cand-{Guid.NewGuid():N}@example.com",
        Stage = stage,
        Status = status,
        AppliedAt = _clock.GetUtcNow(),
        UpdatedAt = _clock.GetUtcNow(),
    };

    // Hiring already created the record — one per person, enforced by the
    // database — so this moves the existing one rather than adding another.
    private async Task MoveAsync(Guid employeeId, OnboardingStage stage)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        OnboardingRecord record = await seed.OnboardingRecords
            .FirstAsync(row => row.EmployeeId == employeeId);

        record.Stage = stage;
        record.UpdatedAt = _clock.GetUtcNow();

        await seed.SaveChangesAsync();
    }

    private OffboardingRecord Offboarding(Guid employeeId, OffboardingStatus status) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        EmployeeId = employeeId,
        Status = status,
        ExitReason = ExitReason.Resignation,
        LastWorkingDate = Today.AddDays(14),
        InitiatedAt = _clock.GetUtcNow(),
        UpdatedAt = _clock.GetUtcNow(),
    };
}
