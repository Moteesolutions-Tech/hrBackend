using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Headcount;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Headcount;
using Motee.Domain.Organisation;
using Motee.Domain.Recruitment;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Headcount planning.
//
// The figures here are the ones an executive reads once a quarter and acts
// on, which makes the failure mode quiet: a wrong actual does not throw, it
// just reports a department as understaffed when it is not, and somebody
// approves a hire against it.
//
// The historical actuals are what these tests exist for. The frontend fakes
// past quarters by scaling today's roster down 2% a quarter; counted off
// start and leaving dates, a past period reports what was actually true, and
// that is worth proving against real rows.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class HeadcountServiceTests(PostgresFixture fixture)
{
    // Inside Q4 2026, so "this quarter" and "last quarter" both have known
    // answers and the year's four quarters are all in the past or present.
    private static readonly DateOnly Today = new(2026, 11, 2);

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

    private static IHeadcountService Headcount(ServiceProvider provider) =>
        Resolve<IHeadcountService>(provider);

    private static async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid departmentId,
        DateOnly? startDate = null,
        EmployeeStatus status = EmployeeStatus.Active,
        string? gender = null,
        DateOnly? dateOfBirth = null,
        string? grade = null,
        EmploymentType employmentType = EmploymentType.FullTime) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            EmploymentType = employmentType,
            Status = status,
            StartDate = startDate ?? new DateOnly(2024, 1, 10),
            DateOfBirth = dateOfBirth,
            Gender = gender,
            Grade = grade,
        })).Employee!.Id;

    private async Task LeftAsync(Guid employeeId, DateOnly on)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Employee employee = await seed.Employees.FirstAsync(row => row.Id == employeeId);

        employee.DateOfLeaving = on;
        employee.Status = EmployeeStatus.Inactive;

        await seed.SaveChangesAsync();
    }

    private static Task<HeadcountPlanResult> TargetAsync(
        ServiceProvider provider,
        Guid departmentId,
        string period,
        int target,
        decimal? budget = null) =>
        Headcount(provider).SetTargetAsync(new HeadcountPlanRequest
        {
            DepartmentId = departmentId,
            Period = period,
            Target = target,
            Budget = budget,
        });

    private static HeadcountPlanRowDto RowFor(
        HeadcountPlanBoardDto board,
        Guid departmentId) =>
        Assert.Single(board.Rows, row => row.DepartmentId == departmentId);

    // ── targets ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ATargetIsSetForADepartmentAndPeriod()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        HeadcountPlanResult result = await TargetAsync(
            provider, _engineeringId, "Q4 2026", 12, budget: 480_000m);

        Assert.True(result.Succeeded, result.Outcome.ToString());
        Assert.Equal(12, result.Plan!.Target);
        Assert.Equal(480_000m, result.Plan.Budget);
        Assert.Equal("Engineering", result.Plan.DepartmentName);
    }

    // Two targets for one quarter is two answers to one question, so setting
    // it again moves the existing row rather than adding a second.
    [SkippableFact]
    public async Task SettingATargetTwiceChangesItRatherThanDuplicatingIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid first = (await TargetAsync(provider, _engineeringId, "Q4 2026", 12)).Plan!.Id!.Value;
        HeadcountPlanResult again = await TargetAsync(provider, _engineeringId, "Q4 2026", 15);

        Assert.Equal(first, again.Plan!.Id);
        Assert.Equal(15, again.Plan.Target);

        HeadcountPlanBoardDto board = await Headcount(provider).BoardAsync("Q4 2026");

        Assert.Single(board.Rows, row => row.DepartmentId == _engineeringId);
    }

    [SkippableTheory]
    [InlineData("Q5 2026")]
    [InlineData("2026")]
    [InlineData("next quarter")]
    public async Task APeriodNobodyRecognisesIsRefused(string period)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            HeadcountPlanOutcome.PeriodNotRecognised,
            (await TargetAsync(provider, _engineeringId, period, 10)).Outcome);
    }

    [SkippableFact]
    public async Task ANegativeTargetIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            HeadcountPlanOutcome.TargetNegative,
            (await TargetAsync(provider, _engineeringId, "Q4 2026", -1)).Outcome);
    }

    [SkippableFact]
    public async Task ATargetForADepartmentThatDoesNotExistIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            HeadcountPlanOutcome.DepartmentNotFound,
            (await TargetAsync(provider, Guid.NewGuid(), "Q4 2026", 10)).Outcome);
    }

    [SkippableFact]
    public async Task ATargetIsRemoved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await TargetAsync(provider, _engineeringId, "Q4 2026", 12)).Plan!.Id!.Value;

        Assert.Equal(
            HeadcountPlanOutcome.Succeeded, await Headcount(provider).DeleteAsync(id));

        Assert.Null(RowFor(await Headcount(provider).BoardAsync("Q4 2026"), _engineeringId).Target);
    }

    // ── the board ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheBoardCountsActualAgainstTarget()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId);
        await HireAsync(provider, "Bola", _engineeringId);
        await TargetAsync(provider, _engineeringId, "Q4 2026", 4);

        HeadcountPlanRowDto row = RowFor(
            await Headcount(provider).BoardAsync("Q4 2026"), _engineeringId);

        Assert.Equal(4, row.Target);
        Assert.Equal(2, row.Actual);
        Assert.Equal(-2, row.Gap);
        Assert.Equal(GapStatus.Under, row.GapStatus);
    }

    // A department nobody has planned reads as unplanned, not as a target of
    // nothing. Defaulting it to today's headcount would hide the gap in the
    // plan itself.
    [SkippableFact]
    public async Task ADepartmentWithNoTargetIsReportedAsUnplanned()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId);

        HeadcountPlanBoardDto board = await Headcount(provider).BoardAsync("Q4 2026");
        HeadcountPlanRowDto row = RowFor(board, _engineeringId);

        Assert.Null(row.Target);
        Assert.Null(row.Gap);
        Assert.Null(row.GapStatus);
        Assert.Equal(1, row.Actual);
        Assert.Equal(2, board.DepartmentsUnplanned);
    }

    [SkippableFact]
    public async Task TheBoardTotalsEveryDepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId);
        await HireAsync(provider, "Bola", _engineeringId);
        await HireAsync(provider, "Dele", _salesId);

        await TargetAsync(provider, _engineeringId, "Q4 2026", 4, budget: 200_000m);
        await TargetAsync(provider, _salesId, "Q4 2026", 1, budget: 50_000m);

        HeadcountPlanBoardDto board = await Headcount(provider).BoardAsync("Q4 2026");

        Assert.Equal(5, board.TotalTarget);
        Assert.Equal(3, board.TotalActual);
        Assert.Equal(-2, board.TotalGap);
        Assert.Equal(1, board.DepartmentsUnder);
        Assert.Equal(0, board.DepartmentsOver);
        Assert.Equal(1, board.DepartmentsOnTarget);
        Assert.Equal(250_000m, board.TotalBudget);
    }

    // The headline improvement over the frontend, which has only today's
    // roster and scales it down by a fixed percentage per quarter. Counted
    // off the dates, Q1 reports who was actually there in Q1.
    [SkippableFact]
    public async Task APastPeriodCountsWhoWasActuallyThereThen()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        // There all year.
        await HireAsync(provider, "Cara", _engineeringId, new DateOnly(2024, 1, 10));

        // Joined in Q3, so absent from Q1 and Q2.
        await HireAsync(provider, "Bola", _engineeringId, new DateOnly(2026, 8, 1));

        // Left at the end of Q1, so present in Q1 and gone afterwards.
        Guid dele = await HireAsync(
            provider, "Dele", _engineeringId, new DateOnly(2024, 1, 10));

        await LeftAsync(dele, new DateOnly(2026, 3, 31));

        Assert.Equal(
            2, RowFor(await Headcount(provider).BoardAsync("Q1 2026"), _engineeringId).Actual);

        Assert.Equal(
            1, RowFor(await Headcount(provider).BoardAsync("Q2 2026"), _engineeringId).Actual);

        Assert.Equal(
            2, RowFor(await Headcount(provider).BoardAsync("Q4 2026"), _engineeringId).Actual);
    }

    // A target for next quarter against a headcount nobody has yet would read
    // as a total shortfall, so a future period counts as at today.
    [SkippableFact]
    public async Task AFuturePeriodCountsAsAtToday()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId);

        HeadcountPlanBoardDto board = await Headcount(provider).BoardAsync("Q2 2027");

        Assert.Equal(Today, board.CountedAsAt);
        Assert.Equal(1, RowFor(board, _engineeringId).Actual);
    }

    [SkippableFact]
    public async Task WithNoPeriodAskedForTheBoardUsesThisQuarter()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal("Q4 2026", (await Headcount(provider).BoardAsync()).Period);
    }

    [SkippableFact]
    public async Task SomebodyWhoHasNotStartedIsNotHeadcount()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(
            provider, "Emeka", _engineeringId,
            new DateOnly(2027, 1, 4), EmployeeStatus.Pending);

        Assert.Equal(
            0, RowFor(await Headcount(provider).BoardAsync("Q4 2026"), _engineeringId).Actual);
    }

    // ── why a gap is still open ─────────────────────────────────────────────

    private async Task<Guid> VacancyAsync(
        Guid departmentId,
        RequisitionStatus status,
        int openings = 1,
        DateOnly? targetStart = null)
    {
        Guid id = Guid.NewGuid();

        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.JobRequisitions.Add(new JobRequisition
        {
            Id = id,
            TenantId = _tenantId,
            PositionTitle = "Backend engineer",
            DepartmentId = departmentId,
            Status = status,
            Openings = openings,
            TargetStartDate = targetStart,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();

        return id;
    }

    [SkippableFact]
    public async Task AGapWithHiringUnderWaySaysSo()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId);
        await TargetAsync(provider, _engineeringId, "Q4 2026", 3);
        await VacancyAsync(_engineeringId, RequisitionStatus.Interviewing, openings: 2);

        HeadcountPlanRowDto row = RowFor(
            await Headcount(provider).BoardAsync("Q4 2026"), _engineeringId);

        Assert.Equal(1, row.ActiveRecruitments);
        Assert.Equal(2, row.OpenVacancies);
        Assert.Equal(RecruitmentPipelineStage.Interviewing, row.RecruitmentStage);
        Assert.Equal("Recruitment in Progress", row.RecommendedAction);
    }

    [SkippableFact]
    public async Task AGapWithNothingRaisedAgainstItNeedsARequest()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId);
        await TargetAsync(provider, _engineeringId, "Q4 2026", 3);

        HeadcountPlanRowDto row = RowFor(
            await Headcount(provider).BoardAsync("Q4 2026"), _engineeringId);

        Assert.Equal(RecruitmentPipelineStage.NotStarted, row.RecruitmentStage);
        Assert.Equal("Create Workforce Request", row.RecommendedAction);
    }

    [SkippableFact]
    public async Task BeingOverTargetAsksAboutTheStructure()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId);
        await HireAsync(provider, "Bola", _engineeringId);
        await TargetAsync(provider, _engineeringId, "Q4 2026", 1);

        Assert.Equal(
            "Review Organisation Structure",
            RowFor(await Headcount(provider).BoardAsync("Q4 2026"), _engineeringId)
                .RecommendedAction);
    }

    // The furthest-along vacancy decides the department's stage: one role at
    // offer and one not started is a department at offer stage.
    [SkippableFact]
    public async Task TheFurthestAlongVacancyDecidesTheStage()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await TargetAsync(provider, _engineeringId, "Q4 2026", 5);
        await VacancyAsync(_engineeringId, RequisitionStatus.Draft);
        await VacancyAsync(_engineeringId, RequisitionStatus.OfferStage);

        Assert.Equal(
            RecruitmentPipelineStage.OfferMade,
            RowFor(await Headcount(provider).BoardAsync("Q4 2026"), _engineeringId)
                .RecruitmentStage);
    }

    [SkippableFact]
    public async Task ADraftVacancyIsARequestRaisedRatherThanARecruitment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await TargetAsync(provider, _engineeringId, "Q4 2026", 5);
        await VacancyAsync(_engineeringId, RequisitionStatus.Draft);

        HeadcountPlanRowDto row = RowFor(
            await Headcount(provider).BoardAsync("Q4 2026"), _engineeringId);

        Assert.Equal(RecruitmentPipelineStage.RequestRaised, row.RecruitmentStage);
        Assert.Equal(0, row.ActiveRecruitments);
    }

    [SkippableFact]
    public async Task TheTargetStartDateOfTheSoonestVacancyIsReported()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await TargetAsync(provider, _engineeringId, "Q4 2026", 5);
        await VacancyAsync(
            _engineeringId, RequisitionStatus.Open, targetStart: new DateOnly(2027, 3, 1));
        await VacancyAsync(
            _engineeringId, RequisitionStatus.Open, targetStart: new DateOnly(2027, 1, 15));

        Assert.Equal(
            new DateOnly(2027, 1, 15),
            RowFor(await Headcount(provider).BoardAsync("Q4 2026"), _engineeringId)
                .ExpectedStartDate);
    }

    [SkippableFact]
    public async Task LastPeriodsGapIsCarriedForContext()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId, new DateOnly(2024, 1, 10));
        await TargetAsync(provider, _engineeringId, "Q3 2026", 4);
        await TargetAsync(provider, _engineeringId, "Q4 2026", 4);

        Assert.Equal(
            -3,
            RowFor(await Headcount(provider).BoardAsync("Q4 2026"), _engineeringId)
                .PreviousPeriodGap);
    }

    // ── trends ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task QuarterOnQuarterComparesAgainstTheQuarterBefore()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId, new DateOnly(2024, 1, 10));
        await HireAsync(provider, "Bola", _engineeringId, new DateOnly(2026, 10, 1));

        HeadcountTrendDto trend = await Headcount(provider).TrendAsync("Q4 2026");

        Assert.Equal("Q3 2026", trend.ComparisonPeriod);
        Assert.Equal(2, trend.TotalCurrent);
        Assert.Equal(1, trend.TotalPrevious);
        Assert.Equal(1, trend.TotalDelta);
        Assert.Equal(100m, trend.TotalPercentChange);
        Assert.Equal(1, trend.Grew);
    }

    [SkippableFact]
    public async Task YearOnYearComparesAgainstTheSameQuarterLastYear()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId, new DateOnly(2026, 2, 1));

        HeadcountTrendDto trend = await Headcount(provider).TrendAsync(
            "Q4 2026", TrendComparison.SameQuarterLastYear);

        Assert.Equal("Q4 2025", trend.ComparisonPeriod);
        Assert.Equal(1, trend.TotalCurrent);
        Assert.Equal(0, trend.TotalPrevious);

        // Growth from nobody has no percentage, so the screen shows the
        // absolute movement instead of a number that reads as a fault.
        Assert.Null(trend.TotalPercentChange);
    }

    [SkippableFact]
    public async Task ADepartmentThatShrankIsCountedAsSuch()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId, new DateOnly(2024, 1, 10));
        Guid bola = await HireAsync(
            provider, "Bola", _engineeringId, new DateOnly(2024, 1, 10));

        await LeftAsync(bola, new DateOnly(2026, 9, 30));

        HeadcountTrendDto trend = await Headcount(provider).TrendAsync("Q4 2026");

        Assert.Equal(1, trend.Shrank);
        Assert.Equal(-1, trend.TotalDelta);
        Assert.Equal(-50m, trend.TotalPercentChange);
    }

    [SkippableFact]
    public async Task AFullYearHasNoQuarterToCompareAgainst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Null((await Headcount(provider).TrendAsync("FY 2026")).ComparisonPeriod);
    }

    [SkippableFact]
    public async Task TheSeriesWalksTheYearsQuarters()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId, new DateOnly(2024, 1, 10));
        await HireAsync(provider, "Bola", _engineeringId, new DateOnly(2026, 8, 1));
        await TargetAsync(provider, _engineeringId, "Q4 2026", 5);

        IReadOnlyList<HeadcountSeriesPointDto> series =
            await Headcount(provider).SeriesAsync(2026);

        Assert.Equal(4, series.Count);
        Assert.Equal("Q1 2026", series[0].Period);
        Assert.Equal(1, series[0].Actual);
        Assert.Null(series[0].Target);
        Assert.Equal(2, series[3].Actual);
        Assert.Equal(5, series[3].Target);
    }

    // ── workforce composition ───────────────────────────────────────────────

    [SkippableFact]
    public async Task DemographicsBreakDownTheRosterWithoutNamingAnybody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(
            provider, "Cara", _engineeringId, new DateOnly(2020, 1, 10),
            gender: "Female", dateOfBirth: new DateOnly(1990, 6, 18), grade: "Senior");

        await HireAsync(
            provider, "Bola", _engineeringId, new DateOnly(2026, 8, 1),
            gender: "Male", dateOfBirth: new DateOnly(2002, 1, 5), grade: "Junior",
            employmentType: EmploymentType.Contract);

        await HireAsync(
            provider, "Dele", _salesId, new DateOnly(2024, 1, 10), gender: "Male");

        DemographicsDto demographics = await Headcount(provider).DemographicsAsync();

        Assert.Equal(3, demographics.Headcount);

        Assert.Equal(
            2, Assert.Single(demographics.Department, row => row.Label == "Engineering").Count);

        Assert.Equal(
            2, Assert.Single(demographics.Gender, row => row.Label == "Male").Count);

        Assert.Equal(
            67m, Assert.Single(demographics.Gender, row => row.Label == "Male").Percentage);

        // Bands, not ages: the report does not need a date of birth.
        Assert.Equal(
            1, Assert.Single(demographics.Age, row => row.Label == "35–44").Count);
        Assert.Equal(
            1, Assert.Single(demographics.Age, row => row.Label == "Under 25").Count);

        Assert.Equal(
            1, Assert.Single(demographics.Tenure, row => row.Label == "<1y").Count);
        Assert.Equal(
            1, Assert.Single(demographics.Tenure, row => row.Label == "5+y").Count);

        Assert.Equal(
            1, Assert.Single(demographics.EmploymentType, row => row.Label == "Contract").Count);

        // A segment carries the id behind it, so a bar can reach its people.
        Assert.Equal(
            _engineeringId,
            Assert.Single(demographics.Department, row => row.Label == "Engineering")
                .DepartmentId);
    }

    // A blank gender is not a gender. Counting it as one puts a category on
    // the chart that nobody chose.
    [SkippableFact]
    public async Task UnrecordedFieldsAreLeftOutOfTheirBreakdown()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId);

        DemographicsDto demographics = await Headcount(provider).DemographicsAsync();

        Assert.Empty(demographics.Gender);
        Assert.Empty(demographics.Age);
        Assert.Empty(demographics.Grade);
    }

    // ── attrition risk ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheRiskBoardScoresPeopleWithTheFactorsBehindIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId, new DateOnly(2018, 1, 10));
        await HireAsync(provider, "Bola", _engineeringId, new DateOnly(2026, 8, 1));

        IReadOnlyList<AttritionRiskDto> board =
            await Headcount(provider).AttritionAsync(new AttritionQuery());

        AttritionRiskDto cara = Assert.Single(board, row => row.EmployeeName == "Cara Okafor");

        Assert.Equal(45, cara.Score);
        Assert.Equal(AttritionRiskLevel.Medium, cara.Level);
        Assert.Equal(
            "Long tenure without role change", Assert.Single(cara.Factors).Label);
        Assert.Equal("Review progression opportunities", cara.RecommendedAction);
        Assert.Equal("Engineering", cara.DepartmentName);
    }

    [SkippableFact]
    public async Task TheRiskBoardRanksWorstFirstAndNarrowsToABand()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await HireAsync(provider, "Cara", _engineeringId, new DateOnly(2018, 1, 10));
        await HireAsync(provider, "Bola", _engineeringId, new DateOnly(2024, 1, 10));
        await HireAsync(provider, "Dele", _salesId, new DateOnly(2026, 8, 1));

        IReadOnlyList<AttritionRiskDto> all =
            await Headcount(provider).AttritionAsync(new AttritionQuery());

        Assert.Equal("Cara Okafor", all[0].EmployeeName);

        Assert.Equal(
            "Cara Okafor",
            Assert.Single(await Headcount(provider).AttritionAsync(
                new AttritionQuery { AtLeast = AttritionRiskLevel.Medium })).EmployeeName);

        Assert.Equal(
            "Dele Okafor",
            Assert.Single(await Headcount(provider).AttritionAsync(
                new AttritionQuery { DepartmentId = _salesId })).EmployeeName);
    }

    [SkippableFact]
    public async Task SomebodyWhoHasLeftIsNotARetentionRisk()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid cara = await HireAsync(
            provider, "Cara", _engineeringId, new DateOnly(2018, 1, 10));

        await LeftAsync(cara, new DateOnly(2026, 9, 30));

        Assert.Empty(await Headcount(provider).AttritionAsync(new AttritionQuery()));
    }
}
