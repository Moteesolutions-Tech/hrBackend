using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Workforce;
using Motee.Domain.Employees;
using Motee.Domain.Onboarding;
using Motee.Domain.Organisation;
using Motee.Domain.Recruitment;
using Motee.Domain.Workforce;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The hire tracker: five records joined into one line each.
//
// The join is where this can go wrong, and quietly. A vacancy whose candidate
// has an accepted offer reads as "attracting" if the candidates are not
// reached, and a role nobody has touched since August reads as fresh if the
// movement date is taken from the wrong record. Both produce a plausible
// screen that sends somebody to the wrong problem.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class HireTrackerTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 11, 3);

    private readonly MovableClock _clock = new(
        new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _engineeringId;
    private Guid _salesId;
    private Guid _caraId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        IDepartmentService departments = Resolve<IDepartmentService>(provider);

        _engineeringId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _salesId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        _caraId = (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = "Cara",
            LastName = "Okafor",
            Email = "cara@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineering lead",
            DepartmentId = _engineeringId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static Task<IReadOnlyList<HireTrackerRowDto>> TrackAsync(
        ServiceProvider provider,
        HireTrackerQuery? query = null) =>
        Resolve<IHireTrackerService>(provider).ListAsync(query ?? new HireTrackerQuery());

    private async Task<Guid> VacancyAsync(
        RequisitionStatus status,
        Guid? departmentId = null,
        bool published = true,
        DateOnly? movedOn = null,
        int openings = 1)
    {
        Guid id = Guid.NewGuid();
        DateTimeOffset moved = Instant(movedOn ?? Today);

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.JobRequisitions.Add(new JobRequisition
        {
            Id = id,
            TenantId = _tenantId,
            PositionTitle = "Backend engineer",
            DepartmentId = departmentId ?? _engineeringId,
            HiringManagerId = _caraId,
            Status = status,
            Openings = openings,
            PublishedAt = published ? moved : null,
            CreatedAt = moved,
            UpdatedAt = moved,
        });

        await seed.SaveChangesAsync();

        return id;
    }

    private async Task<Guid> CandidateAsync(
        Guid requisitionId,
        CandidateStage stage,
        CandidateStatus status = CandidateStatus.Active,
        Guid? createdEmployeeId = null,
        DateOnly? movedOn = null,
        string first = "Ngozi")
    {
        Guid id = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Candidates.Add(new Candidate
        {
            Id = id,
            TenantId = _tenantId,
            RequisitionId = requisitionId,
            FirstName = first,
            LastName = "Eze",
            Email = $"{Guid.NewGuid():N}@example.com",
            Stage = stage,
            Status = status,
            CreatedEmployeeId = createdEmployeeId,
            AppliedAt = Instant(movedOn ?? Today),
            UpdatedAt = Instant(movedOn ?? Today),
        });

        await seed.SaveChangesAsync();

        return id;
    }

    private async Task<Guid> JoinerAsync(
        ServiceProvider provider,
        OnboardingStage stage,
        string first = "Ngozi")
    {
        Guid employeeId = (await Resolve<IEmployeeService>(provider).CreateAsync(
            new EmployeeRequest
            {
                FirstName = first,
                LastName = "Eze",
                Email = $"{first.ToLowerInvariant()}-{Guid.NewGuid():N}"[..20] + "@acme.com",
                Phone = "08012345678",
                JobTitle = "Backend engineer",
                DepartmentId = _engineeringId,
                EmploymentType = EmploymentType.FullTime,
                Status = EmployeeStatus.Pending,
                StartDate = Today.AddDays(14),
            })).Employee!.Id;

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        OnboardingRecord? existing = seed.OnboardingRecords
            .FirstOrDefault(record => record.EmployeeId == employeeId);

        if (existing is null)
        {
            seed.OnboardingRecords.Add(new OnboardingRecord
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                EmployeeId = employeeId,
                Stage = stage,
                CreatedAt = Instant(Today),
                UpdatedAt = Instant(Today),
            });
        }
        else
        {
            existing.Stage = stage;
            existing.UpdatedAt = Instant(Today);
        }

        await seed.SaveChangesAsync();

        return employeeId;
    }

    private static DateTimeOffset Instant(DateOnly day) =>
        new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    // ── one row per effort, at the right stage ──────────────────────────────

    [SkippableFact]
    public async Task AnAdvertisedVacancyWithNobodyOnItIsAttracting()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await VacancyAsync(RequisitionStatus.Open);

        HireTrackerRowDto row = Assert.Single(await TrackAsync(provider));

        Assert.Equal(HiringStage.Attract, row.Stage);
        Assert.Equal(3, row.Step);
        Assert.Equal("Advertised — no applicants yet", row.Detail);
        Assert.Equal("Backend engineer", row.Title);
        Assert.Equal("Engineering", row.DepartmentName);
        Assert.Equal("Cara Okafor", row.HiringManagerName);
    }

    [SkippableFact]
    public async Task AnUnpublishedVacancyIsStillAtRequisition()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await VacancyAsync(RequisitionStatus.Approved, published: false);

        HireTrackerRowDto row = Assert.Single(await TrackAsync(provider));

        Assert.Equal(HiringStage.Requisition, row.Stage);
        Assert.Equal("Approved — not advertised yet", row.Detail);
    }

    [SkippableFact]
    public async Task CandidatesInInterviewsPutTheRoleInSelection()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid vacancy = await VacancyAsync(RequisitionStatus.Interviewing);
        await CandidateAsync(vacancy, CandidateStage.Interview);
        await CandidateAsync(vacancy, CandidateStage.Interviewed, first: "Tunde");

        HireTrackerRowDto row = Assert.Single(await TrackAsync(provider));

        Assert.Equal(HiringStage.Select, row.Stage);
        Assert.Equal("2 candidates in selection", row.Detail);
        Assert.Equal(2, row.InSelection);
    }

    // The precedence case. Two people still interviewing and one with an
    // offer out is a role that is nearly filled, not one in selection.
    [SkippableFact]
    public async Task TheFurthestAlongCandidateDecidesTheStage()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid vacancy = await VacancyAsync(RequisitionStatus.OfferStage);
        await CandidateAsync(vacancy, CandidateStage.Applicants, first: "Ada");
        await CandidateAsync(vacancy, CandidateStage.Interview, first: "Tunde");
        await CandidateAsync(vacancy, CandidateStage.Offer, first: "Ngozi");

        HireTrackerRowDto row = Assert.Single(await TrackAsync(provider));

        Assert.Equal(HiringStage.PreEmployment, row.Stage);
        Assert.Equal("Offer out — awaiting acceptance", row.Detail);
        Assert.Equal("Ngozi Eze", row.Person);
    }

    // The gap the onboarding handoff exists to close, surfaced by name: a
    // person who said yes and has heard nothing.
    [SkippableFact]
    public async Task AHireNobodyHasInvitedToOnboardingIsCalledOut()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid vacancy = await VacancyAsync(RequisitionStatus.Filled);
        await CandidateAsync(vacancy, CandidateStage.Hired);

        HireTrackerRowDto row = Assert.Single(await TrackAsync(provider));

        Assert.Equal(HiringStage.PreEmployment, row.Stage);
        Assert.Equal("Hired — onboarding invite not sent", row.Detail);
    }

    [SkippableFact]
    public async Task AJoinerNotYetStartedIsAtPreEmployment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid vacancy = await VacancyAsync(RequisitionStatus.Filled);
        Guid employeeId = await JoinerAsync(provider, OnboardingStage.PreBoarding);
        await CandidateAsync(
            vacancy, CandidateStage.Hired, createdEmployeeId: employeeId);

        HireTrackerRowDto row = Assert.Single(await TrackAsync(provider));

        Assert.Equal(HiringStage.PreEmployment, row.Stage);
        Assert.Equal("Checks in progress before they start", row.Detail);
        Assert.Equal("Ngozi Eze", row.Person);
    }

    [SkippableFact]
    public async Task AJoinerWhoHasStartedIsOnboarding()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid vacancy = await VacancyAsync(RequisitionStatus.Filled);
        Guid employeeId = await JoinerAsync(provider, OnboardingStage.FirstWeek);
        await CandidateAsync(
            vacancy, CandidateStage.Hired, createdEmployeeId: employeeId);

        HireTrackerRowDto row = Assert.Single(await TrackAsync(provider));

        Assert.Equal(HiringStage.Onboard, row.Stage);
        Assert.Equal(6, row.Step);
    }

    [SkippableFact]
    public async Task ARejectedCandidateDoesNotDecideTheStage()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid vacancy = await VacancyAsync(RequisitionStatus.Open);
        await CandidateAsync(vacancy, CandidateStage.Offer, CandidateStatus.Rejected);

        Assert.Equal(HiringStage.Attract, Assert.Single(await TrackAsync(provider)).Stage);
    }

    // ── requests with no vacancy ────────────────────────────────────────────

    // Still a hiring effort, and the one most likely to be forgotten: nothing
    // downstream knows it exists.
    [SkippableFact]
    public async Task ARequestWithNoVacancyYetIsTrackedAtPlanning()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await Resolve<IWorkforceRequestService>(provider).CreateAsync(
            new WorkforceRequestRequest
            {
                DepartmentId = _salesId,
                Position = "Account executive",
                Justification = "Pipeline has doubled.",
                CostCentreCode = "SAL-01",
                NumberOfHires = 2,
            });

        HireTrackerRowDto row = Assert.Single(await TrackAsync(provider));

        Assert.Equal(HiringStage.WorkforcePlanning, row.Stage);
        Assert.Equal(1, row.Step);
        Assert.Equal("Account executive", row.Title);
        Assert.Equal("Sales", row.DepartmentName);
        Assert.Equal(2, row.Openings);
        Assert.NotNull(row.WorkforceRequestId);
        Assert.Null(row.RequisitionId);
    }

    // Once it becomes a vacancy the vacancy is the row. Counting both would
    // show one hiring effort twice, at two different stages.
    [SkippableFact]
    public async Task ARequestThatBecameAVacancyIsNotCountedTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid vacancy = await VacancyAsync(RequisitionStatus.Open);

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.WorkforceRequests.Add(new WorkforceRequest
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                Position = "Backend engineer",
                Justification = "Growth.",
                DepartmentId = _engineeringId,
                CostCentreCode = "ENG-01",
                RequisitionId = vacancy,
                CreatedAt = Instant(Today),
                UpdatedAt = Instant(Today),
            });

            await seed.SaveChangesAsync();
        }

        HireTrackerRowDto row = Assert.Single(await TrackAsync(provider));

        Assert.Equal(vacancy, row.RequisitionId);
    }

    // ── stalling ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnEffortNobodyHasTouchedIsFlaggedStalled()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await VacancyAsync(RequisitionStatus.Open, movedOn: Today.AddDays(-40));

        HireTrackerRowDto row = Assert.Single(await TrackAsync(provider));

        Assert.Equal(40, row.DaysSinceMoved);
        Assert.True(row.Stalled);
    }

    // One candidate progressing means the vacancy is moving, even if the
    // requisition row itself has not been touched in months.
    [SkippableFact]
    public async Task MovementOnAnyCandidateCountsAsMovement()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid vacancy = await VacancyAsync(
            RequisitionStatus.Interviewing, movedOn: Today.AddDays(-40));

        await CandidateAsync(vacancy, CandidateStage.Interview, movedOn: Today.AddDays(-2));

        HireTrackerRowDto row = Assert.Single(await TrackAsync(provider));

        Assert.Equal(2, row.DaysSinceMoved);
        Assert.False(row.Stalled);
    }

    [SkippableFact]
    public async Task StalledEffortsComeFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await VacancyAsync(RequisitionStatus.Open, movedOn: Today);
        await VacancyAsync(
            RequisitionStatus.Open, departmentId: _salesId, movedOn: Today.AddDays(-30));

        IReadOnlyList<HireTrackerRowDto> rows = await TrackAsync(provider);

        Assert.True(rows[0].Stalled);
        Assert.Equal(_salesId, rows[0].DepartmentId);
    }

    // ── filters ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheTrackerNarrowsToAStage()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid interviewing = await VacancyAsync(RequisitionStatus.Interviewing);
        await CandidateAsync(interviewing, CandidateStage.Interview);
        await VacancyAsync(RequisitionStatus.Open, departmentId: _salesId);

        Assert.Equal(
            HiringStage.Select,
            Assert.Single(await TrackAsync(
                provider, new HireTrackerQuery { Stage = HiringStage.Select })).Stage);
    }

    [SkippableFact]
    public async Task TheTrackerNarrowsToStalledOnly()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await VacancyAsync(RequisitionStatus.Open, movedOn: Today);
        await VacancyAsync(
            RequisitionStatus.Open, departmentId: _salesId, movedOn: Today.AddDays(-30));

        Assert.Equal(
            _salesId,
            Assert.Single(await TrackAsync(
                provider, new HireTrackerQuery { Stalled = true })).DepartmentId);
    }

    [SkippableFact]
    public async Task TheTrackerSearchesByRoleAndByPerson()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid vacancy = await VacancyAsync(RequisitionStatus.OfferStage);
        await CandidateAsync(vacancy, CandidateStage.Offer, first: "Ngozi");

        Assert.Single(await TrackAsync(provider, new HireTrackerQuery { Search = "backend" }));
        Assert.Single(await TrackAsync(provider, new HireTrackerQuery { Search = "ngozi" }));
        Assert.Empty(await TrackAsync(provider, new HireTrackerQuery { Search = "plumber" }));
    }

    [SkippableFact]
    public async Task TheTrackerNarrowsToADepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await VacancyAsync(RequisitionStatus.Open);
        await VacancyAsync(RequisitionStatus.Open, departmentId: _salesId);

        Assert.Equal(
            _salesId,
            Assert.Single(await TrackAsync(
                provider, new HireTrackerQuery { DepartmentId = _salesId })).DepartmentId);
    }

    [SkippableFact]
    public async Task ACancelledVacancyIsNotTracked()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await VacancyAsync(RequisitionStatus.Cancelled);

        Assert.Empty(await TrackAsync(provider));
    }
}
