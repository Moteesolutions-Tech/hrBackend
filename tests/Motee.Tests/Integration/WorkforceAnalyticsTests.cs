using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Workforce;
using Motee.Domain.Employees;
using Motee.Domain.Offboarding;
using Motee.Domain.Organisation;
using Motee.Domain.Recruitment;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Turnover and hiring, out of the database.
//
// These replace three hardcoded arrays on the workforce planning screen, so
// the thing worth proving is that the numbers track the records: a
// resignation logged against a department moves that department's rate, and
// an offer accepted moves its time-to-fill.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class WorkforceAnalyticsTests(PostgresFixture fixture)
{
    // Inside Q4 2026, so three quarters are behind and one is running.
    private static readonly DateOnly Today = new(2026, 11, 3);

    private readonly MovableClock _clock = new(
        new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _engineeringId;
    private Guid _salesId;

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

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IWorkforceAnalyticsService Workforce(ServiceProvider provider) =>
        Resolve<IWorkforceAnalyticsService>(provider);

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid? departmentId = null,
        DateOnly? startDate = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId ?? _engineeringId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = startDate ?? new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    // What offboarding leaves behind. Written directly because the exit chain
    // has its own tests and what turnover needs is a leaver with a reason.
    private async Task LeftAsync(Guid employeeId, DateOnly on, ExitReason? reason)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Employee employee = await seed.Employees.FirstAsync(row => row.Id == employeeId);

        employee.DateOfLeaving = on;
        employee.Status = EmployeeStatus.Inactive;

        if (reason is ExitReason recorded)
        {
            seed.OffboardingRecords.Add(new OffboardingRecord
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                EmployeeId = employeeId,
                Status = OffboardingStatus.Completed,
                ExitReason = recorded,
                LastWorkingDate = on,
                InitiatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await seed.SaveChangesAsync();
    }

    private static TurnoverRowDto CompanyRow(
        IReadOnlyList<TurnoverRowDto> rows,
        string period) =>
        Assert.Single(rows, row => row.Period == period && row.DepartmentId is null);

    private static TurnoverRowDto DepartmentRow(
        IReadOnlyList<TurnoverRowDto> rows,
        string period,
        Guid departmentId) =>
        Assert.Single(rows, row => row.Period == period && row.DepartmentId == departmentId);

    // ── overview ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheOverviewCountsHeadcountAndAverageTenure()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", startDate: new DateOnly(2022, 11, 3));
        await HireAsync(provider, "Bola", startDate: new DateOnly(2024, 11, 2));

        WorkforceOverviewDto overview = await Workforce(provider).OverviewAsync();

        Assert.Equal(Today, overview.AsOf);
        Assert.Equal(2, overview.Headcount);

        // Four years and two years, so three.
        Assert.Equal(3m, overview.AverageTenureYears);
    }

    [SkippableFact]
    public async Task TheOverviewReportsTheRollingYearsTurnover()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara");
        await HireAsync(provider, "Bola");
        Guid dele = await HireAsync(provider, "Dele");

        await LeftAsync(dele, new DateOnly(2026, 6, 30), ExitReason.Resignation);

        WorkforceOverviewDto overview = await Workforce(provider).OverviewAsync();

        Assert.Equal(1, overview.LeaversLastTwelveMonths);

        // Opening three, closing two, so an average of three against one
        // leaver — not one over two, which would overstate it.
        Assert.Equal(33.3m, overview.TurnoverRatePercent);
    }

    [SkippableFact]
    public async Task AnExitOlderThanAYearIsOutOfTheRollingFigure()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara");
        Guid dele = await HireAsync(provider, "Dele", startDate: new DateOnly(2023, 1, 10));

        await LeftAsync(dele, new DateOnly(2025, 6, 30), ExitReason.Resignation);

        Assert.Equal(0, (await Workforce(provider).OverviewAsync()).LeaversLastTwelveMonths);
    }

    [SkippableFact]
    public async Task TheOverviewCountsOpeningsNotVacancies()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await VacancyAsync(RequisitionStatus.Open, openings: 3);
        await VacancyAsync(RequisitionStatus.Interviewing, openings: 2);

        // One requisition for three engineers is three openings, and a draft
        // is not recruiting at all.
        await VacancyAsync(RequisitionStatus.Draft, openings: 9);

        Assert.Equal(5, (await Workforce(provider).OverviewAsync()).OpenVacancies);
    }

    // ── turnover ────────────────────────────────────────────────────────────

    // The split that makes the measure worth having: the same two leavers
    // mean opposite things depending on who decided.
    [SkippableFact]
    public async Task LeaversAreSplitByWhetherTheyChoseToGo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara");
        await HireAsync(provider, "Bola");
        Guid dele = await HireAsync(provider, "Dele");
        Guid emeka = await HireAsync(provider, "Emeka");
        Guid ngozi = await HireAsync(provider, "Ngozi");

        await LeftAsync(dele, new DateOnly(2026, 8, 14), ExitReason.Resignation);
        await LeftAsync(emeka, new DateOnly(2026, 8, 20), ExitReason.Redundancy);

        // No offboarding record, so no reason to classify by.
        await LeftAsync(ngozi, new DateOnly(2026, 8, 25), null);

        TurnoverRowDto row = CompanyRow(
            await Workforce(provider).TurnoverAsync(new TurnoverQuery()), "Q3 2026");

        Assert.Equal(3, row.Leavers);
        Assert.Equal(1, row.Voluntary);
        Assert.Equal(1, row.Involuntary);
        Assert.Equal(1, row.Unclassified);
    }

    [SkippableFact]
    public async Task TurnoverIsReportedPerDepartmentAndForTheCompany()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara");
        Guid bola = await HireAsync(provider, "Bola");
        await HireAsync(provider, "Dele", _salesId);

        await LeftAsync(bola, new DateOnly(2026, 8, 14), ExitReason.Resignation);

        IReadOnlyList<TurnoverRowDto> rows =
            await Workforce(provider).TurnoverAsync(new TurnoverQuery());

        Assert.Equal(1, CompanyRow(rows, "Q3 2026").Leavers);
        Assert.Equal(1, DepartmentRow(rows, "Q3 2026", _engineeringId).Leavers);
        Assert.Equal(0, DepartmentRow(rows, "Q3 2026", _salesId).Leavers);
    }

    [SkippableFact]
    public async Task TurnoverNarrowsToOneDepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Dele", _salesId);

        IReadOnlyList<TurnoverRowDto> rows = await Workforce(provider).TurnoverAsync(
            new TurnoverQuery { DepartmentId = _salesId });

        Assert.All(rows, row => Assert.Equal(_salesId, row.DepartmentId));
        Assert.DoesNotContain(rows, row => row.DepartmentId is null);
    }

    // A quarter still running is measured to today, not to its own end — or
    // its rate reads as a fraction of a quarter nobody has lived through.
    [SkippableFact]
    public async Task AQuarterStillRunningIsMeasuredToToday()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara");
        Guid bola = await HireAsync(provider, "Bola");

        await LeftAsync(bola, new DateOnly(2026, 10, 20), ExitReason.Resignation);

        TurnoverRowDto row = CompanyRow(
            await Workforce(provider).TurnoverAsync(new TurnoverQuery()), "Q4 2026");

        Assert.Equal(1, row.Leavers);

        // Opening two, closing one — an average of two rather than the one
        // left standing today.
        Assert.Equal(2, row.AverageHeadcount);
        Assert.Equal(50m, row.RatePercent);
    }

    [SkippableFact]
    public async Task ADepartmentNobodyWasInHasNoRate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Null(
            DepartmentRow(
                await Workforce(provider).TurnoverAsync(new TurnoverQuery()),
                "Q1 2026",
                _salesId)
            .RatePercent);
    }

    [SkippableFact]
    public async Task TurnoverCoversTheWholeYearAskedFor()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IReadOnlyList<TurnoverRowDto> rows = await Workforce(provider).TurnoverAsync(
            new TurnoverQuery { Year = 2025 });

        Assert.Equal(
            ["Q1 2025", "Q2 2025", "Q3 2025", "Q4 2025"],
            [.. rows.Select(row => row.Period).Distinct()]);
    }

    // ── hiring ──────────────────────────────────────────────────────────────

    private async Task<Guid> VacancyAsync(
        RequisitionStatus status,
        int openings = 1,
        Guid? departmentId = null,
        DateOnly? publishedOn = null)
    {
        Guid id = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.JobRequisitions.Add(new JobRequisition
        {
            Id = id,
            TenantId = _tenantId,
            PositionTitle = "Backend engineer",
            DepartmentId = departmentId ?? _engineeringId,
            Status = status,
            Openings = openings,
            PublishedAt = publishedOn is DateOnly day
                ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        return id;
    }

    private async Task OfferedAsync(
        Guid requisitionId,
        OfferStatus status,
        DateOnly sentOn,
        DateOnly? respondedOn = null)
    {
        Guid candidateId = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Candidates.Add(new Candidate
        {
            Id = candidateId,
            TenantId = _tenantId,
            RequisitionId = requisitionId,
            FirstName = "Ngozi",
            LastName = $"Eze{Guid.NewGuid():N}"[..12],
            Email = $"{Guid.NewGuid():N}@example.com",
            Stage = CandidateStage.Offer,
            Status = CandidateStatus.Active,
            AppliedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        seed.CandidateOffers.Add(new CandidateOffer
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            CandidateId = candidateId,
            Status = status,
            SentAt = new DateTimeOffset(sentOn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            RespondedAt = respondedOn is DateOnly day
                ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : null,
        });

        await seed.SaveChangesAsync();
    }

    private static HiringRowDto HiringFor(
        IReadOnlyList<HiringRowDto> rows,
        Guid departmentId) =>
        Assert.Single(rows, row => row.DepartmentId == departmentId);

    [SkippableFact]
    public async Task TimeToFillIsMeasuredFromPublicationToAcceptance()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid vacancy = await VacancyAsync(
            RequisitionStatus.Filled, publishedOn: new DateOnly(2026, 9, 1));

        await OfferedAsync(
            vacancy,
            OfferStatus.Accepted,
            new DateOnly(2026, 9, 20),
            new DateOnly(2026, 10, 16));

        HiringRowDto row = HiringFor(
            await Workforce(provider).HiringAsync(new HiringQuery()), _engineeringId);

        Assert.Equal(45m, row.AverageDaysToFill);
        Assert.Equal(1, row.OffersExtended);
        Assert.Equal(1, row.OffersAccepted);
        Assert.Equal(100m, row.AcceptanceRatePercent);
        Assert.Equal(1, row.FilledThisQuarter);
    }

    // A department still searching has no time-to-fill. Counting days-so-far
    // would let the slowest searches improve the figure.
    [SkippableFact]
    public async Task ADepartmentThatHasFilledNothingHasNoTimeToFill()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await VacancyAsync(RequisitionStatus.Open, publishedOn: new DateOnly(2026, 9, 1));

        HiringRowDto row = HiringFor(
            await Workforce(provider).HiringAsync(new HiringQuery()), _engineeringId);

        Assert.Null(row.AverageDaysToFill);
        Assert.Null(row.AcceptanceRatePercent);
        Assert.Equal(1, row.OpenRequisitions);
    }

    [SkippableFact]
    public async Task OffersTurnedDownPullTheAcceptanceRateDown()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid vacancy = await VacancyAsync(
            RequisitionStatus.OfferStage, publishedOn: new DateOnly(2026, 9, 1));

        await OfferedAsync(
            vacancy, OfferStatus.Accepted, new DateOnly(2026, 9, 20), new DateOnly(2026, 10, 1));
        await OfferedAsync(
            vacancy, OfferStatus.Rejected, new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 2));
        await OfferedAsync(
            vacancy, OfferStatus.Rejected, new DateOnly(2026, 9, 22), new DateOnly(2026, 10, 3));

        HiringRowDto row = HiringFor(
            await Workforce(provider).HiringAsync(new HiringQuery()), _engineeringId);

        Assert.Equal(3, row.OffersExtended);
        Assert.Equal(1, row.OffersAccepted);
        Assert.Equal(33.3m, row.AcceptanceRatePercent);
    }

    // The number that says a role has stopped being a search.
    [SkippableFact]
    public async Task AVacancyOpenTooLongIsReportedAsStale()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await VacancyAsync(RequisitionStatus.Open, publishedOn: new DateOnly(2026, 5, 1));
        await VacancyAsync(RequisitionStatus.Open, publishedOn: new DateOnly(2026, 10, 25));

        HiringRowDto row = HiringFor(
            await Workforce(provider).HiringAsync(new HiringQuery()), _engineeringId);

        Assert.Equal(1, row.StaleVacancies);
        Assert.Equal(186, row.LongestOpenDays);
    }

    [SkippableFact]
    public async Task HiringIsReportedPerDepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await VacancyAsync(RequisitionStatus.Open, publishedOn: new DateOnly(2026, 10, 1));
        await VacancyAsync(
            RequisitionStatus.Open, departmentId: _salesId, publishedOn: new DateOnly(2026, 10, 1));

        IReadOnlyList<HiringRowDto> rows =
            await Workforce(provider).HiringAsync(new HiringQuery());

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, HiringFor(rows, _engineeringId).OpenRequisitions);
        Assert.Equal(1, HiringFor(rows, _salesId).OpenRequisitions);
    }

    [SkippableFact]
    public async Task HiringNarrowsToOneDepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await VacancyAsync(RequisitionStatus.Open, publishedOn: new DateOnly(2026, 10, 1));
        await VacancyAsync(
            RequisitionStatus.Open, departmentId: _salesId, publishedOn: new DateOnly(2026, 10, 1));

        Assert.Equal(
            _salesId,
            Assert.Single(await Workforce(provider).HiringAsync(
                new HiringQuery { DepartmentId = _salesId })).DepartmentId);
    }

    [SkippableFact]
    public async Task AnOfferOlderThanTheWindowIsOutOfTheFigures()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid vacancy = await VacancyAsync(
            RequisitionStatus.Filled, publishedOn: new DateOnly(2024, 1, 1));

        await OfferedAsync(
            vacancy, OfferStatus.Accepted, new DateOnly(2024, 2, 1), new DateOnly(2024, 3, 1));

        HiringRowDto row = HiringFor(
            await Workforce(provider).HiringAsync(new HiringQuery { Months = 12 }),
            _engineeringId);

        Assert.Equal(0, row.OffersExtended);
        Assert.Null(row.AverageDaysToFill);
    }
}
