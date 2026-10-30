using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Onboarding;
using Motee.Application.Organisation;
using Motee.Application.Recruitment;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Recruitment;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Hiring, end to end: a vacancy, the people who apply to it, and the gates
// between them.
//
// Written before the services. The clock is pinned because expiry, "days in
// stage" and the reference number are all date arithmetic.
[Collection(PostgresCollection.Name)]
public class RecruitmentServiceTests(PostgresFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    private readonly MovableClock _clock = new(
        new DateTimeOffset(Today.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _managerId;
    private Guid _recruiterId;

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

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _managerId = await HireAsync(provider, "Cara", "cara@acme.com");
        _recruiterId = await HireAsync(provider, "Bola", "bola@acme.com");

        return provider;
    }

    private async Task<Guid> HireAsync(ServiceProvider provider, string first, string email) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = email,
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
        })).Employee!.Id;

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private RequisitionRequest Vacancy(
        int openings = 1,
        DateOnly? expiryDate = null,
        IReadOnlyList<CandidateStage>? stages = null) => new()
        {
            PositionTitle = "Senior Engineer",
            DepartmentId = _departmentId,
            HiringManagerId = _managerId,
            RecruiterId = _recruiterId,
            EmploymentType = EmploymentType.FullTime,
            Location = "Lagos",
            Openings = openings,
            SalaryMin = 6_000_000m,
            SalaryMax = 9_000_000m,
            JobDescription = "Build and run the platform.",
            RequiredSkills = ["C#", "Postgres"],
            ExpiryDate = expiryDate,
            Stages = stages,
        };

    // A published vacancy, which is the starting point for everything about
    // candidates — an unpublished one takes no applications at all.
    private async Task<Guid> OpenAsync(
        ServiceProvider provider, RequisitionRequest? request = null)
    {
        IRequisitionService requisitions = Resolve<IRequisitionService>(provider);

        RequisitionResult created = await requisitions.CreateAsync(request ?? Vacancy());

        Assert.True(created.Succeeded, created.Outcome.ToString());

        RequisitionResult published = await requisitions.PublishAsync(created.Requisition!.Id);

        Assert.True(published.Succeeded, published.Outcome.ToString());

        return published.Requisition!.Id;
    }

    private static CandidateRequest Applicant(Guid requisitionId, string first = "Chidi") => new()
    {
        RequisitionId = requisitionId,
        FirstName = first,
        LastName = "Eze",
        Email = $"{first.ToLowerInvariant()}@example.com",
        Phone = "08098765432",
        Source = "linkedin",
        Skills = ["C#"],
    };

    private static async Task<Guid> ApplyAsync(
        ServiceProvider provider, Guid requisitionId, string first = "Chidi")
    {
        CandidateResult result = await Resolve<ICandidateService>(provider)
            .CreateAsync(Applicant(requisitionId, first));

        Assert.True(result.Succeeded, result.Outcome.ToString());

        return result.Candidate!.Id;
    }

    // ── vacancies ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ANewVacancyIsADraftWithNoReferenceYet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        RequisitionDto created = (await Resolve<IRequisitionService>(provider)
            .CreateAsync(Vacancy())).Requisition!;

        Assert.Equal(RequisitionStatus.Draft, created.Status);
        Assert.Null(created.Reference);
        Assert.False(created.IsAcceptingApplications);
    }

    // Issued on publication and never reissued, because it is quoted in emails
    // and to agencies.
    [SkippableFact]
    public async Task PublishingIssuesAReferenceOnceAndOnlyOnce()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IRequisitionService requisitions = Resolve<IRequisitionService>(provider);
        Guid id = await OpenAsync(provider);

        string reference = (await requisitions.GetAsync(id))!.Reference!;

        Assert.StartsWith("REQ-2026-", reference, StringComparison.Ordinal);

        await requisitions.HoldAsync(id);
        await requisitions.ResumeAsync(id);

        Assert.Equal(reference, (await requisitions.GetAsync(id))!.Reference);
    }

    [SkippableFact]
    public async Task APublishedVacancyTakesApplications()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        RequisitionDto open = (await Resolve<IRequisitionService>(provider)
            .GetAsync(await OpenAsync(provider)))!;

        Assert.Equal(RequisitionStatus.Open, open.Status);
        Assert.True(open.IsAcceptingApplications);
        Assert.NotNull(open.PublishedAt);
    }

    // Returned rather than enforced, so the screen shows the whole list at
    // once instead of one problem per attempt.
    [SkippableFact]
    public async Task TheWarningsSayWhatIsMissingBeforeItGoesLive()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IRequisitionService requisitions = Resolve<IRequisitionService>(provider);

        Guid id = (await requisitions.CreateAsync(Vacancy() with
        {
            HiringManagerId = null,
            JobDescription = null,
            SalaryMin = null,
            SalaryMax = null,
        })).Requisition!.Id;

        IReadOnlyList<PublishWarningDto> warnings = await requisitions.WarningsAsync(id);

        Assert.Contains(
            warnings, warning => warning.Field == "Hiring manager" && warning.IsBlocking);
        Assert.Contains(
            warnings, warning => warning.Field == "Job description" && warning.IsBlocking);

        // Advisory: it attracts the wrong applicants rather than wasting
        // everybody's time.
        Assert.Contains(warnings, warning => warning.Field == "Salary" && !warning.IsBlocking);
    }

    [SkippableFact]
    public async Task AVacancyNobodyOwnsCannotBePublished()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IRequisitionService requisitions = Resolve<IRequisitionService>(provider);

        Guid id = (await requisitions.CreateAsync(Vacancy() with { HiringManagerId = null }))
            .Requisition!.Id;

        Assert.Equal(
            RequisitionOutcome.HiringManagerRequired, (await requisitions.PublishAsync(id)).Outcome);
    }

    [SkippableFact]
    public async Task AVacancyWithNothingToReadCannotBePublished()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IRequisitionService requisitions = Resolve<IRequisitionService>(provider);

        Guid id = (await requisitions.CreateAsync(Vacancy() with { JobDescription = "  " }))
            .Requisition!.Id;

        Assert.Equal(
            RequisitionOutcome.JobDescriptionRequired,
            (await requisitions.PublishAsync(id)).Outcome);
    }

    [SkippableFact]
    public async Task AnInvertedSalaryRangeIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            RequisitionOutcome.SalaryRangeInverted,
            (await Resolve<IRequisitionService>(provider).CreateAsync(
                Vacancy() with { SalaryMin = 9_000_000m, SalaryMax = 6_000_000m })).Outcome);
    }

    // Derived from the date, so it is right with no job having run.
    [SkippableFact]
    public async Task AVacancyPastItsClosingDateStopsTakingApplications()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await OpenAsync(provider, Vacancy(expiryDate: Today.AddDays(1)));

        Assert.True((await Resolve<IRequisitionService>(provider).GetAsync(id))!
            .IsAcceptingApplications);

        _clock.Advance(TimeSpan.FromDays(2));

        RequisitionDto expired = (await Resolve<IRequisitionService>(provider).GetAsync(id))!;

        Assert.True(expired.HasExpired);
        Assert.False(expired.IsAcceptingApplications);

        Assert.Equal(
            CandidateOutcome.NotAcceptingApplications,
            (await Resolve<ICandidateService>(provider).CreateAsync(Applicant(id))).Outcome);
    }

    // Closing keeps the funnel. Deleting would take the applicants with it.
    [SkippableFact]
    public async Task AVacancyPeopleHaveAppliedToCannotBeDeleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await OpenAsync(provider);

        await ApplyAsync(provider, id);

        Assert.Equal(
            RequisitionOutcome.HasCandidates,
            await Resolve<IRequisitionService>(provider).DeleteAsync(id));
    }

    [SkippableFact]
    public async Task AVacancyOnHoldCannotBeHeldAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IRequisitionService requisitions = Resolve<IRequisitionService>(provider);
        Guid id = await OpenAsync(provider);

        Assert.True((await requisitions.HoldAsync(id)).Succeeded);
        Assert.Equal(
            RequisitionOutcome.NotInThatState, (await requisitions.HoldAsync(id)).Outcome);
    }

    // ── the derived badge ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheVacancyBadgeFollowsItsPipeline()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IRequisitionService requisitions = Resolve<IRequisitionService>(provider);
        ICandidateService candidates = Resolve<ICandidateService>(provider);

        Guid id = await OpenAsync(provider);
        Guid chidi = await ApplyAsync(provider, id);

        Assert.Equal(RequisitionStatus.Open, (await requisitions.GetAsync(id))!.Status);

        await candidates.MoveAsync(chidi, CandidateStage.Interview);

        Assert.Equal(RequisitionStatus.Interviewing, (await requisitions.GetAsync(id))!.Status);

        await ScoreAsync(provider, chidi);
        await candidates.MoveAsync(chidi, CandidateStage.Interviewed);
        await candidates.MoveAsync(chidi, CandidateStage.Offer);

        Assert.Equal(RequisitionStatus.OfferStage, (await requisitions.GetAsync(id))!.Status);

        await AcceptAsync(provider, chidi);
        await candidates.MoveAsync(chidi, CandidateStage.Hired);

        Assert.Equal(RequisitionStatus.Filled, (await requisitions.GetAsync(id))!.Status);
    }

    [SkippableFact]
    public async Task ThePipelineCountsAreCountedRatherThanStored()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await OpenAsync(provider, Vacancy(openings: 2));
        Guid chidi = await ApplyAsync(provider, id);

        await ApplyAsync(provider, id, "Dupe");
        await Resolve<ICandidateService>(provider).RejectAsync(chidi, "Not enough experience.");

        PipelineCountsDto pipeline =
            (await Resolve<IRequisitionService>(provider).GetAsync(id))!.Pipeline;

        Assert.Equal(2, pipeline.Openings);
        Assert.Equal(0, pipeline.Hired);
        Assert.Equal(2, pipeline.Remaining);
        Assert.Equal(1, pipeline.Active);
        Assert.Equal(1, pipeline.Rejected);
        Assert.Equal(2, pipeline.ByStage[CandidateStage.Applicants]);
    }

    // ── candidates ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnApplicantStartsInTheApplicantsStageOwnedByTheRecruiter()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await OpenAsync(provider);

        CandidateDto applicant = (await Resolve<ICandidateService>(provider)
            .CreateAsync(Applicant(id))).Candidate!;

        Assert.Equal(CandidateStage.Applicants, applicant.Stage);
        Assert.Equal(CandidateStatus.Active, applicant.Status);

        // Not nobody. An unowned applicant is one nobody is answerable for.
        Assert.Equal(_recruiterId, applicant.OwnerEmployeeId);
        Assert.Equal("Bola Okafor", applicant.OwnerName);
    }

    // Their first application is the one carrying the history.
    [SkippableFact]
    public async Task TheSamePersonCannotApplyTwiceToOneVacancy()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await OpenAsync(provider);

        await ApplyAsync(provider, id);

        Assert.Equal(
            CandidateOutcome.AlreadyApplied,
            (await Resolve<ICandidateService>(provider).CreateAsync(Applicant(id))).Outcome);
    }

    [SkippableFact]
    public async Task TheSamePersonMayApplyToADifferentVacancy()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid first = await OpenAsync(provider);
        Guid second = await OpenAsync(
            provider, Vacancy() with { PositionTitle = "Staff Engineer" });

        await ApplyAsync(provider, first);

        Assert.True((await Resolve<ICandidateService>(provider)
            .CreateAsync(Applicant(second))).Succeeded);
    }

    [SkippableFact]
    public async Task AnUnpublishedVacancyTakesNoApplications()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = (await Resolve<IRequisitionService>(provider).CreateAsync(Vacancy()))
            .Requisition!.Id;

        Assert.Equal(
            CandidateOutcome.NotAcceptingApplications,
            (await Resolve<ICandidateService>(provider).CreateAsync(Applicant(id))).Outcome);
    }

    // The refusal reaches the board as a reason rather than as a snap-back.
    [SkippableFact]
    public async Task TheBoardCanAskWhetherAMoveWouldBeAllowed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);
        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        Assert.Equal(
            MoveRefusal.None, await candidates.CanMoveAsync(chidi, CandidateStage.Interview));
        Assert.Equal(
            MoveRefusal.SkipsAStage,
            await candidates.CanMoveAsync(chidi, CandidateStage.Interviewed));
    }

    [SkippableFact]
    public async Task ARefusedMoveSaysWhichGateStoppedIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);
        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        await candidates.MoveAsync(chidi, CandidateStage.Interview);

        CandidateResult refused = await candidates.MoveAsync(chidi, CandidateStage.Interviewed);

        Assert.Equal(CandidateOutcome.MoveRefused, refused.Outcome);
        Assert.Equal(MoveRefusal.NotScored, refused.Refusal);
    }

    // The number that says somebody has gone quiet.
    [SkippableFact]
    public async Task HowLongSomebodyHasSatWhereTheyAreIsReported()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);
        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        _clock.Advance(TimeSpan.FromDays(9));

        Assert.Equal(9, (await candidates.GetAsync(chidi))!.DaysInStage);

        await candidates.MoveAsync(chidi, CandidateStage.Interview);

        Assert.Equal(0, (await candidates.GetAsync(chidi))!.DaysInStage);
    }

    // Rejection keeps the stage, so "rejected at offer" and "rejected on sight"
    // stay different facts and the funnel survives.
    [SkippableFact]
    public async Task RejectionKeepsTheStageTheyReached()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);
        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        await candidates.MoveAsync(chidi, CandidateStage.Interview);

        CandidateDto rejected =
            (await candidates.RejectAsync(chidi, "Withdrew.")).Candidate!;

        Assert.Equal(CandidateStatus.Rejected, rejected.Status);
        Assert.Equal(CandidateStage.Interview, rejected.Stage);
        Assert.Equal("Withdrew.", rejected.RejectionReason);
        Assert.NotNull(rejected.RejectedAt);
    }

    [SkippableFact]
    public async Task ARejectionNeedsAReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        Assert.Equal(
            CandidateOutcome.ReasonRequired,
            (await Resolve<ICandidateService>(provider).RejectAsync(chidi, "   ")).Outcome);
    }

    [SkippableFact]
    public async Task RestoringPutsThemBackWhereTheyWere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);
        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        await candidates.MoveAsync(chidi, CandidateStage.Interview);
        await candidates.RejectAsync(chidi, "Changed our minds.");

        CandidateDto restored = (await candidates.RestoreAsync(chidi)).Candidate!;

        Assert.Equal(CandidateStatus.Active, restored.Status);
        Assert.Equal(CandidateStage.Interview, restored.Stage);
        Assert.Null(restored.RejectionReason);
    }

    // ── evidence ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AScoreIsTheAverageOfItsLines()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);
        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        CandidateDto scored = (await candidates.ScoreAsync(chidi, new ScorecardRequest
        {
            Criteria =
            [
                new ScorecardCriterion { Label = "Technical", Score = 4 },
                new ScorecardCriterion { Label = "Communication", Score = 3 },
            ],
            Recommendation = ScorecardRecommendation.Yes,
        })).Candidate!;

        Assert.Equal(3.5m, Assert.Single(scored.Scorecards).Overall);
        Assert.Equal(3.5m, scored.Score);
    }

    [SkippableFact]
    public async Task AScorecardWithNoLinesIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        Assert.Equal(
            CandidateOutcome.CriteriaRequired,
            (await Resolve<ICandidateService>(provider)
                .ScoreAsync(chidi, new ScorecardRequest { Criteria = [] })).Outcome);
    }

    [SkippableFact]
    public async Task AScoreOutsideOneToFiveIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        Assert.Equal(
            CandidateOutcome.ScoreOutOfRange,
            (await Resolve<ICandidateService>(provider).ScoreAsync(chidi, new ScorecardRequest
            {
                Criteria = [new ScorecardCriterion { Label = "Technical", Score = 9 }],
            })).Outcome);
    }

    // Withdraw the first or record its answer. Two live offers is two different
    // numbers the candidate could be holding.
    [SkippableFact]
    public async Task ASecondOfferWhileOneIsUnansweredIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);
        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        await candidates.SendOfferAsync(chidi, new OfferRequest { Salary = 7_000_000m });

        Assert.Equal(
            CandidateOutcome.OfferOutstanding,
            (await candidates.SendOfferAsync(chidi, new OfferRequest { Salary = 8_000_000m }))
                .Outcome);
    }

    [SkippableFact]
    public async Task AnAnsweredOfferCannotBeAnsweredAgain()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);
        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        Guid offerId = (await candidates.SendOfferAsync(
            chidi, new OfferRequest { Salary = 7_000_000m })).Candidate!.Offers[0].Id;

        await candidates.RespondToOfferAsync(
            chidi, offerId, new OfferResponseRequest { Status = OfferStatus.Accepted });

        Assert.Equal(
            CandidateOutcome.OfferAlreadyAnswered,
            (await candidates.RespondToOfferAsync(
                chidi, offerId, new OfferResponseRequest { Status = OfferStatus.Rejected }))
                .Outcome);
    }

    // Sent is where an offer starts, not something a candidate can reply.
    [SkippableFact]
    public async Task SentIsNotAnAnswer()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);
        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        Guid offerId = (await candidates.SendOfferAsync(
            chidi, new OfferRequest { Salary = 7_000_000m })).Candidate!.Offers[0].Id;

        Assert.Equal(
            CandidateOutcome.NotAnAnswer,
            (await candidates.RespondToOfferAsync(
                chidi, offerId, new OfferResponseRequest { Status = OfferStatus.Sent })).Outcome);
    }

    // A role funded for one seat cannot take two.
    [SkippableFact]
    public async Task TheSecondHireAgainstOneOpeningIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);
        Guid id = await OpenAsync(provider);

        Guid chidi = await ApplyAsync(provider, id);
        Guid dupe = await ApplyAsync(provider, id, "Dupe");

        await HireThemAsync(provider, chidi);
        await HireThemAsync(provider, dupe, expectHired: false);

        CandidateResult refused = await candidates.MoveAsync(dupe, CandidateStage.Hired);

        Assert.Equal(MoveRefusal.NoOpeningsLeft, refused.Refusal);
    }

    // ── the handoff to onboarding ───────────────────────────────────────────
    //
    // The link the chain was missing. Until this existed, a hire was a card in
    // a column and somebody retyped their details into the employee form.

    [SkippableFact]
    public async Task AHireBecomesAnEmployeeWithAnOnboardingRecord()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await OpenAsync(provider);
        Guid chidi = await ApplyAsync(provider, id);

        await HireThemAsync(provider, chidi);

        CandidateDto handed = (await Resolve<ICandidateService>(provider)
            .SendToOnboardingAsync(chidi)).Candidate!;

        Assert.NotNull(handed.CreatedEmployeeId);

        EmployeeDto employee = (await Resolve<IEmployeeService>(provider)
            .GetAsync(handed.CreatedEmployeeId!.Value))!;

        Assert.Equal("Chidi", employee.FirstName);
        Assert.Equal("chidi@example.com", employee.Email);
        Assert.Equal("Senior Engineer", employee.JobTitle);
        Assert.Equal(_departmentId, employee.DepartmentId);

        // Pending, not active. They have not started, and counting them as
        // headcount the day the offer lands inflates every figure on the
        // dashboard.
        Assert.Equal(EmployeeStatus.Pending, employee.Status);

        // Ensured when the employee is created, so the record is already there
        // rather than this having to make a second one. The other two records
        // belong to the colleagues the fixture hires.
        Assert.Contains(
            (await Resolve<IOnboardingService>(provider).ListAsync(new OnboardingQuery())).Items,
            onboarding => onboarding.EmployeeId == employee.Id);
    }

    // The stage table and the drawer both offer this. Doing it twice would
    // duplicate a real person.
    [SkippableFact]
    public async Task HandingTheSameHireOverTwiceIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);

        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        await HireThemAsync(provider, chidi);
        await candidates.SendToOnboardingAsync(chidi);

        Assert.Equal(
            CandidateOutcome.AlreadyOnboarding,
            (await candidates.SendToOnboardingAsync(chidi)).Outcome);
    }

    [SkippableFact]
    public async Task SomebodyNotYetHiredCannotBeHandedOver()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);
        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        await candidates.MoveAsync(chidi, CandidateStage.Interview);

        Assert.Equal(
            CandidateOutcome.NotHired, (await candidates.SendToOnboardingAsync(chidi)).Outcome);
    }

    // The accepted offer's date is what the candidate agreed to; the vacancy's
    // target date is only ever what HR hoped for.
    [SkippableFact]
    public async Task TheStartDateComesFromTheOfferTheyAccepted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);

        Guid id = await OpenAsync(
            provider, Vacancy() with { TargetStartDate = new DateOnly(2026, 8, 1) });

        Guid chidi = await ApplyAsync(provider, id);

        await candidates.MoveAsync(chidi, CandidateStage.Interview);
        await ScoreAsync(provider, chidi);
        await candidates.MoveAsync(chidi, CandidateStage.Interviewed);
        await candidates.MoveAsync(chidi, CandidateStage.Offer);

        CandidateResult sent = await candidates.SendOfferAsync(chidi, new OfferRequest
        {
            Salary = 7_000_000m,
            StartDate = new DateOnly(2026, 9, 14),
        });

        await candidates.RespondToOfferAsync(
            chidi,
            sent.Candidate!.Offers[^1].Id,
            new OfferResponseRequest { Status = OfferStatus.Accepted });

        await candidates.MoveAsync(chidi, CandidateStage.Hired);

        Guid employeeId =
            (await candidates.SendToOnboardingAsync(chidi)).Candidate!.CreatedEmployeeId!.Value;

        Assert.Equal(
            new DateOnly(2026, 9, 14),
            (await Resolve<IEmployeeService>(provider).GetAsync(employeeId))!.StartDate);
    }

    // The fix is on the vacancy, not the candidate, so it gets its own answer.
    [SkippableFact]
    public async Task AVacancyWithNoDepartmentCannotHandSomebodyOver()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        ICandidateService candidates = Resolve<ICandidateService>(provider);

        Guid id = await OpenAsync(provider, Vacancy() with { DepartmentId = null });
        Guid chidi = await ApplyAsync(provider, id);

        await HireThemAsync(provider, chidi);

        Assert.Equal(
            CandidateOutcome.DepartmentRequired,
            (await candidates.SendToOnboardingAsync(chidi)).Outcome);
    }

    // Somebody already on the payroll under that address. Reported rather than
    // swallowed, because the two records would be the same person.
    [SkippableFact]
    public async Task AHireWhoAlreadyWorksHereIsReported()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await OpenAsync(provider);

        CandidateResult applied = await Resolve<ICandidateService>(provider)
            .CreateAsync(Applicant(id) with { Email = "cara@acme.com" });

        await HireThemAsync(provider, applied.Candidate!.Id);

        // Resolved fresh, as each HTTP request would be. Holding one instance
        // across the hire would read the candidate out of that scope's change
        // tracker and still see them as an applicant.
        Assert.Equal(
            CandidateOutcome.CouldNotCreateEmployee,
            (await Resolve<ICandidateService>(provider)
                .SendToOnboardingAsync(applied.Candidate!.Id)).Outcome);
    }

    // ── interviews ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnInterviewFallsBackToTheVacancysStandingPanel()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid id = await OpenAsync(
            provider, Vacancy() with { InterviewPanel = [_managerId, _recruiterId] });

        Guid chidi = await ApplyAsync(provider, id);

        InterviewDto scheduled = (await Resolve<IInterviewService>(provider)
            .ScheduleAsync(new InterviewRequest
            {
                CandidateId = chidi,
                Round = "First round",
                ScheduledAt = new DateTimeOffset(
                    Today.AddDays(2).ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(1)),
            })).Interview!;

        Assert.Equal(2, scheduled.Panel.Count);
        Assert.Contains("Cara Okafor", scheduled.PanelNames);
        Assert.Equal(InterviewStatus.Scheduled, scheduled.Status);
    }

    // Reported, not refused: the diary is the user's to resolve.
    [SkippableFact]
    public async Task ADoubleBookedInterviewerIsReportedRatherThanRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IInterviewService interviews = Resolve<IInterviewService>(provider);

        Guid id = await OpenAsync(provider);
        Guid chidi = await ApplyAsync(provider, id);
        Guid dupe = await ApplyAsync(provider, id, "Dupe");

        DateTimeOffset slot = new(
            Today.AddDays(2).ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(1));

        await interviews.ScheduleAsync(new InterviewRequest
        {
            CandidateId = chidi,
            Round = "First round",
            ScheduledAt = slot,
            Panel = [_managerId],
        });

        InterviewResult clashing = await interviews.ScheduleAsync(new InterviewRequest
        {
            CandidateId = dupe,
            Round = "First round",

            // Half an hour in, so it overlaps the hour already booked.
            ScheduledAt = slot.AddMinutes(30),
            Panel = [_managerId],
        });

        Assert.True(clashing.Succeeded);
        Assert.Contains("Cara Okafor", clashing.Interview!.Clashes);
    }

    [SkippableFact]
    public async Task SchedulingForSomebodyOutOfTheRunningIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        await Resolve<ICandidateService>(provider).RejectAsync(chidi, "Not for us.");

        Assert.Equal(
            InterviewOutcome.CandidateRejected,
            (await Resolve<IInterviewService>(provider).ScheduleAsync(new InterviewRequest
            {
                CandidateId = chidi,
                Round = "First round",
                ScheduledAt = DateTimeOffset.UtcNow.AddDays(2),
            })).Outcome);
    }

    [SkippableFact]
    public async Task ACancelledInterviewCannotBeCompleted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IInterviewService interviews = Resolve<IInterviewService>(provider);
        Guid chidi = await ApplyAsync(provider, await OpenAsync(provider));

        Guid interviewId = (await interviews.ScheduleAsync(new InterviewRequest
        {
            CandidateId = chidi,
            Round = "First round",
            ScheduledAt = DateTimeOffset.UtcNow.AddDays(2),
        })).Interview!.Id;

        Assert.True((await interviews.CancelAsync(interviewId)).Succeeded);
        Assert.Equal(
            InterviewOutcome.NotScheduled, (await interviews.CompleteAsync(interviewId)).Outcome);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static Task<CandidateResult> ScoreAsync(ServiceProvider provider, Guid candidateId) =>
        Resolve<ICandidateService>(provider).ScoreAsync(candidateId, new ScorecardRequest
        {
            Criteria = [new ScorecardCriterion { Label = "Technical", Score = 4 }],
            Recommendation = ScorecardRecommendation.Yes,
        });

    private static async Task AcceptAsync(ServiceProvider provider, Guid candidateId)
    {
        ICandidateService candidates = Resolve<ICandidateService>(provider);

        CandidateResult sent = await candidates.SendOfferAsync(
            candidateId, new OfferRequest { Salary = 7_000_000m });

        Assert.True(sent.Succeeded, sent.Outcome.ToString());

        CandidateResult answered = await candidates.RespondToOfferAsync(
            candidateId,
            sent.Candidate!.Offers[^1].Id,
            new OfferResponseRequest { Status = OfferStatus.Accepted });

        Assert.True(answered.Succeeded, answered.Outcome.ToString());
    }

    // Walks somebody the whole way, which is the only way to reach Hired.
    private static async Task HireThemAsync(
        ServiceProvider provider, Guid candidateId, bool expectHired = true)
    {
        ICandidateService candidates = Resolve<ICandidateService>(provider);

        await candidates.MoveAsync(candidateId, CandidateStage.Interview);
        await ScoreAsync(provider, candidateId);
        await candidates.MoveAsync(candidateId, CandidateStage.Interviewed);
        await candidates.MoveAsync(candidateId, CandidateStage.Offer);
        await AcceptAsync(provider, candidateId);

        if (!expectHired)
        {
            return;
        }

        CandidateResult hired = await candidates.MoveAsync(candidateId, CandidateStage.Hired);

        Assert.True(hired.Succeeded, hired.Refusal.ToString());
    }
}
