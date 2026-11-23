using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Surveys;
using Motee.Domain.Diversity;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Surveys;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Surveys, out of the database.
//
// The tests that matter most are the two halves of anonymity: a respondent's
// name never comes back, and below the reporting threshold the aggregates do
// not either — a mean over three replies from a team of four identifies people
// as surely as a name. After that it is the engagement score, which must come
// from what people said rather than from how many of them replied.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class SurveyServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(new DateOnly(2026, 6, 15).ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _engineeringId;
    private Guid _salesId;
    private Guid _hrId;

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

        IDepartmentService departments = Resolve<IDepartmentService>(provider);

        _engineeringId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _salesId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        _hrId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "People", Code = "PPL" }))
            .Department!.Id;

        _hrLeadId = await HireAsync(provider, "Ada", _hrId);
        await AccountAsync(_hrLeadId, "ada");
        await ActAsync(provider, _hrLeadId);

        return provider;
    }

    private Guid _hrLeadId;

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static ISurveyService Surveys(ServiceProvider provider) =>
        Resolve<ISurveyService>(provider);

    private async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid departmentId,
        Guid? managerId = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            ManagerId = managerId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    private async Task AccountAsync(Guid employeeId, string handle)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = char.ToUpperInvariant(handle[0]) + handle[1..],
            LastName = "Okafor",
            Email = $"{handle}-login@acme.com",
            NormalizedEmail = $"{handle.ToUpperInvariant()}-LOGIN@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    private async Task ActAsync(ServiceProvider provider, Guid employeeId)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid userId = await seed.Users
            .IgnoreQueryFilters()
            .Where(user => user.EmployeeId == employeeId && user.TenantId == _tenantId)
            .Select(user => user.Id)
            .FirstAsync();

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();
    }

    // A team big enough to clear the reporting threshold.
    private async Task<List<Guid>> TeamAsync(
        ServiceProvider provider,
        Guid departmentId,
        int howMany,
        string prefix)
    {
        List<Guid> ids = [];

        for (int number = 0; number < howMany; number++)
        {
            Guid id = await HireAsync(provider, $"{prefix}{number}", departmentId);

            await AccountAsync(id, $"{prefix}{number}".ToLowerInvariant());

            ids.Add(id);
        }

        return ids;
    }

    private static SurveyRequest Asking(
        string title = "Spring engagement survey",
        SurveyType type = SurveyType.Engagement,
        SurveyAudience audience = SurveyAudience.AllStaff,
        IReadOnlyList<Guid>? departments = null,
        bool anonymous = false,
        SurveyStatus status = SurveyStatus.Draft,
        IReadOnlyList<SurveyQuestionRequest>? questions = null) => new()
        {
            Title = title,
            Description = "Ten minutes on how the last quarter went.",
            SurveyType = type,
            Audience = audience,
            TargetDepartmentIds = departments,
            IsAnonymous = anonymous,
            Status = status,
            Questions = questions ??
            [
                new SurveyQuestionRequest
                {
                    Text = "How supported do you feel by your manager?",
                    QuestionType = QuestionType.Rating,
                    Required = true,
                    ScaleMin = 1,
                    ScaleMax = 5,
                },
            ],
        };

    private static async Task<SurveyDto> BuiltAsync(
        ServiceProvider provider,
        SurveyRequest request)
    {
        SurveyResult result = await Surveys(provider).CreateAsync(request);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Survey!;
    }

    private static async Task<SurveyDto> OpenedAsync(
        ServiceProvider provider,
        SurveyRequest request)
    {
        SurveyDto survey = await BuiltAsync(provider, request);

        SurveyResult opened = await Surveys(provider)
            .SetStatusAsync(survey.Id, SurveyStatus.Active);

        Assert.True(opened.Succeeded, $"{opened.Outcome} / {opened.Refusal}");

        return opened.Survey!;
    }

    private static async Task AnswerAsync(
        ServiceProvider provider,
        SurveyDto survey,
        string value)
    {
        SurveyResult result = await Surveys(provider).RespondAsync(
            survey.Id,
            new SurveySubmissionRequest
            {
                Answers =
                [
                    new SubmittedAnswerRequest
                    {
                        QuestionId = survey.Questions[0].Id,
                        Values = [value],
                    },
                ],
            });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
    }

    // ── building one ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ANewSurveyIsADraftWithNoTargetCapturedYet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto survey = await BuiltAsync(provider, Asking());

        Assert.Equal(SurveyStatus.Draft, survey.Status);
        Assert.Null(survey.TotalTargeted);
        Assert.Null(survey.ResponseRate);
        Assert.Single(survey.Questions);
        Assert.Equal("Ada Okafor", survey.CreatedByName);
    }

    [SkippableFact]
    public async Task RefusesASurveyWithNoQuestions()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            SurveyRefusal.NoQuestions,
            (await Surveys(provider).CreateAsync(Asking(questions: []))).Refusal);
    }

    [SkippableFact]
    public async Task RefusesADepartmentSurveyWithNoDepartments()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            SurveyRefusal.NoTargetDepartments,
            (await Surveys(provider).CreateAsync(
                Asking(audience: SurveyAudience.Department, departments: []))).Refusal);
    }

    [SkippableFact]
    public async Task RefusesARatingQuestionWithNoScale()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyResult result = await Surveys(provider).CreateAsync(Asking(questions:
        [
            new SurveyQuestionRequest
            {
                Text = "How happy are you?",
                QuestionType = QuestionType.Rating,
                Required = true,
            },
        ]));

        Assert.Equal(SurveyRefusal.ScaleBackwards, result.Refusal);
    }

    // ── opening it ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OpeningTheSurveyCapturesHowManyWereAsked()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await TeamAsync(provider, _engineeringId, 3, "eng");

        SurveyDto survey = await OpenedAsync(provider, Asking());

        // Three engineers plus the HR lead who built it.
        Assert.Equal(4, survey.TotalTargeted);
    }

    // A closed survey's response rate must not drift as the company grows.
    [SkippableFact]
    public async Task TheCapturedTargetDoesNotMoveWhenThePayrollGrows()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto survey = await OpenedAsync(provider, Asking());

        Assert.Equal(1, survey.TotalTargeted);

        await TeamAsync(provider, _engineeringId, 4, "eng");

        SurveyResult closed = await Surveys(provider)
            .SetStatusAsync(survey.Id, SurveyStatus.Closed);

        Assert.Equal(1, closed.Survey!.TotalTargeted);
    }

    [SkippableFact]
    public async Task ADepartmentSurveyOnlyCountsThatDepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await TeamAsync(provider, _engineeringId, 3, "eng");
        await TeamAsync(provider, _salesId, 2, "sal");

        SurveyDto survey = await OpenedAsync(provider, Asking(
            audience: SurveyAudience.Department, departments: [_engineeringId]));

        Assert.Equal(3, survey.TotalTargeted);
    }

    // ── editing ─────────────────────────────────────────────────────────────

    // Editing a question under a reply would leave an answer pointing at a
    // question whose wording has changed since it was given.
    [SkippableFact]
    public async Task ASurveyPeopleHaveAnsweredCannotBeEdited()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto survey = await OpenedAsync(provider, Asking());

        await AnswerAsync(provider, survey, "4");

        Assert.Equal(
            SurveyOutcome.HasResponses,
            (await Surveys(provider).UpdateAsync(survey.Id, Asking(title: "Edited"))).Outcome);
    }

    [SkippableFact]
    public async Task ADraftIsEditedFreely()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto survey = await BuiltAsync(provider, Asking());

        SurveyResult updated = await Surveys(provider).UpdateAsync(
            survey.Id, Asking(title: "Autumn engagement survey"));

        Assert.True(updated.Succeeded, $"{updated.Outcome} / {updated.Refusal}");
        Assert.Equal("Autumn engagement survey", updated.Survey!.Title);
    }

    // ── answering ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnEmployeeSeesWhatIsOpenToThemAndNotWhatTheyHaveAnswered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        List<Guid> engineers = await TeamAsync(provider, _engineeringId, 1, "eng");

        SurveyDto survey = await OpenedAsync(provider, Asking());

        await ActAsync(provider, engineers[0]);

        Assert.Equal([survey.Id], (await Surveys(provider).MineAsync()).Select(row => row.Id));

        await AnswerAsync(provider, survey, "4");

        Assert.Empty(await Surveys(provider).MineAsync());
    }

    [SkippableFact]
    public async Task ADepartmentSurveyDoesNotReachAnotherDepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        List<Guid> sales = await TeamAsync(provider, _salesId, 1, "sal");

        SurveyDto survey = await OpenedAsync(provider, Asking(
            audience: SurveyAudience.Department, departments: [_engineeringId]));

        await ActAsync(provider, sales[0]);

        Assert.Empty(await Surveys(provider).MineAsync());

        Assert.Equal(
            SurveyRefusal.NotInAudience,
            (await Surveys(provider).RespondAsync(
                survey.Id,
                new SurveySubmissionRequest { Answers = [] })).Refusal);
    }

    // Managers means anybody with somebody reporting to them, which is a fact
    // about the org chart rather than a flag on the person.
    [SkippableFact]
    public async Task AManagersSurveyReachesOnlyPeopleWithReports()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid managerId = await HireAsync(provider, "Bola", _engineeringId);
        await AccountAsync(managerId, "bola");

        Guid reportId = await HireAsync(provider, "Ngozi", _engineeringId, managerId: managerId);
        await AccountAsync(reportId, "ngozi");

        SurveyDto survey = await OpenedAsync(provider, Asking(audience: SurveyAudience.Managers));

        await ActAsync(provider, managerId);
        Assert.Equal([survey.Id], (await Surveys(provider).MineAsync()).Select(row => row.Id));

        await ActAsync(provider, reportId);
        Assert.Empty(await Surveys(provider).MineAsync());
    }

    [SkippableFact]
    public async Task ADraftCannotBeAnswered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto survey = await BuiltAsync(provider, Asking());

        Assert.Equal(
            SurveyRefusal.NotOpen,
            (await Surveys(provider).RespondAsync(
                survey.Id,
                new SurveySubmissionRequest { Answers = [] })).Refusal);
    }

    [SkippableFact]
    public async Task NobodyAnswersTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto survey = await OpenedAsync(provider, Asking());

        await AnswerAsync(provider, survey, "4");

        SurveyResult again = await Surveys(provider).RespondAsync(
            survey.Id,
            new SurveySubmissionRequest
            {
                Answers =
                [
                    new SubmittedAnswerRequest
                    {
                        QuestionId = survey.Questions[0].Id,
                        Values = ["5"],
                    },
                ],
            });

        Assert.Equal(SurveyRefusal.AlreadyAnswered, again.Refusal);
    }

    // A required question missing from the payload is the commonest way a
    // half-finished form gets submitted.
    [SkippableFact]
    public async Task RefusesASubmissionThatSkipsARequiredQuestion()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto survey = await OpenedAsync(provider, Asking());

        Assert.Equal(
            SurveyRefusal.AnswerRequired,
            (await Surveys(provider).RespondAsync(
                survey.Id,
                new SurveySubmissionRequest { Answers = [] })).Refusal);
    }

    [SkippableFact]
    public async Task RefusesARatingOutsideTheScaleAsked()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto survey = await OpenedAsync(provider, Asking());

        SurveyResult result = await Surveys(provider).RespondAsync(
            survey.Id,
            new SurveySubmissionRequest
            {
                Answers =
                [
                    new SubmittedAnswerRequest
                    {
                        QuestionId = survey.Questions[0].Id,
                        Values = ["9"],
                    },
                ],
            });

        Assert.Equal(SurveyRefusal.AnswerOutOfRange, result.Refusal);
    }

    // ── reading your own answers back ───────────────────────────────────────

    [SkippableFact]
    public async Task ASignedSurveyGivesTheRespondentTheirAnswersBack()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto survey = await OpenedAsync(provider, Asking(anonymous: false));

        await AnswerAsync(provider, survey, "4");

        MySurveyResponseDto mine = (await Surveys(provider).MyResponseAsync(survey.Id))!;

        Assert.True(mine.HaveIAnswered);
        Assert.Equal(["4"], Assert.Single(mine.Answers!).Values);
    }

    // Reading an anonymous answer back is a route to its author, so it is not
    // offered — only the fact that they answered.
    [SkippableFact]
    public async Task AnAnonymousSurveyGivesBackOnlyThatYouAnswered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto survey = await OpenedAsync(provider, Asking(anonymous: true));

        await AnswerAsync(provider, survey, "4");

        MySurveyResponseDto mine = (await Surveys(provider).MyResponseAsync(survey.Id))!;

        Assert.True(mine.HaveIAnswered);
        Assert.Null(mine.Answers);
    }

    // ── results, and what is withheld ───────────────────────────────────────

    // Withholding the name is not enough: a mean over three replies from a
    // team of four identifies people.
    [SkippableFact]
    public async Task AnAnonymousSurveyWithhoneTooFewRepliesPublishesNothing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        List<Guid> team = await TeamAsync(
            provider, _engineeringId, Suppression.Threshold - 1, "eng");

        SurveyDto survey = await OpenedAsync(provider, Asking(anonymous: true));

        foreach (Guid id in team)
        {
            await ActAsync(provider, id);
            await AnswerAsync(provider, survey, "4");
        }

        SurveyResultsDto results = (await Surveys(provider).ResultsAsync(survey.Id))!;

        Assert.False(results.IsReportable);
        Assert.NotNull(results.Withheld);
        Assert.Empty(results.Questions);
        Assert.Null(results.EngagementScore);

        // The count and the rate are still reported — knowing how many
        // answered identifies nobody.
        Assert.Equal(Suppression.Threshold - 1, results.Responses);
    }

    [SkippableFact]
    public async Task AnAnonymousSurveyPublishesOnceEnoughHaveAnswered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        List<Guid> team = await TeamAsync(provider, _engineeringId, Suppression.Threshold, "eng");

        SurveyDto survey = await OpenedAsync(provider, Asking(anonymous: true));

        foreach (Guid id in team)
        {
            await ActAsync(provider, id);
            await AnswerAsync(provider, survey, "4");
        }

        SurveyResultsDto results = (await Surveys(provider).ResultsAsync(survey.Id))!;

        Assert.True(results.IsReportable);
        Assert.Null(results.Withheld);
        Assert.Single(results.Questions);
    }

    // A signed survey has nothing to protect — the respondent put their name
    // to it on purpose.
    [SkippableFact]
    public async Task ASignedSurveyPublishesHoweverFewAnswered()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto survey = await OpenedAsync(provider, Asking(anonymous: false));

        await AnswerAsync(provider, survey, "4");

        SurveyResultsDto results = (await Surveys(provider).ResultsAsync(survey.Id))!;

        Assert.True(results.IsReportable);
        Assert.Equal(1, results.Responses);
    }

    // The headline fix. The client falls back to the response rate for any
    // survey without an NPS question, so a survey everybody answered
    // "terrible" scores a hundred because everybody answered.
    [SkippableFact]
    public async Task TheEngagementScoreComesFromTheAnswersNotTheTurnout()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        List<Guid> team = await TeamAsync(provider, _engineeringId, Suppression.Threshold, "eng");

        SurveyDto survey = await OpenedAsync(provider, Asking(anonymous: true));

        // Everybody who was asked replied, and every one of them said the
        // worst thing on the scale.
        foreach (Guid id in team)
        {
            await ActAsync(provider, id);
            await AnswerAsync(provider, survey, "1");
        }

        SurveyResultsDto results = (await Surveys(provider).ResultsAsync(survey.Id))!;

        Assert.Equal(0m, results.EngagementScore);

        // A near-total turnout, scoring nothing.
        Assert.True(results.ResponseRate > 80m, $"rate was {results.ResponseRate}");
    }

    [SkippableFact]
    public async Task AggregatesARatingQuestion()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        List<Guid> team = await TeamAsync(provider, _engineeringId, 3, "eng");

        SurveyDto survey = await OpenedAsync(provider, Asking(anonymous: false));

        string[] answers = ["5", "4", "3"];

        for (int index = 0; index < team.Count; index++)
        {
            await ActAsync(provider, team[index]);
            await AnswerAsync(provider, survey, answers[index]);
        }

        QuestionResultDto question =
            Assert.Single((await Surveys(provider).ResultsAsync(survey.Id))!.Questions);

        Assert.Equal(3, question.Answers);
        Assert.Equal(4m, question.Average);
        Assert.Equal(3, question.Distribution.Count);
    }

    [SkippableFact]
    public async Task ReportsEnpsFromAnNpsQuestion()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        List<Guid> team = await TeamAsync(provider, _engineeringId, 4, "eng");

        SurveyDto survey = await OpenedAsync(provider, Asking(
            type: SurveyType.Enps,
            anonymous: false,
            questions:
            [
                new SurveyQuestionRequest
                {
                    Text = "How likely are you to recommend us as a place to work?",
                    QuestionType = QuestionType.Nps,
                    Required = true,
                },
            ]));

        string[] scores = ["10", "9", "7", "3"];

        for (int index = 0; index < team.Count; index++)
        {
            await ActAsync(provider, team[index]);
            await AnswerAsync(provider, survey, scores[index]);
        }

        EnpsTally enps = (await Surveys(provider).ResultsAsync(survey.Id))!.Enps!;

        Assert.Equal(2, enps.Promoters);
        Assert.Equal(1, enps.Passives);
        Assert.Equal(1, enps.Detractors);

        // Fifty per cent promoters less twenty-five per cent detractors.
        Assert.Equal(25, enps.Score);
    }

    // Zero is a real eNPS — as many promoters as detractors — so a survey
    // with no NPS question reports none rather than nought.
    [SkippableFact]
    public async Task ASurveyWithNoNpsQuestionHasNoEnps()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto survey = await OpenedAsync(provider, Asking(anonymous: false));

        await AnswerAsync(provider, survey, "4");

        Assert.Null((await Surveys(provider).ResultsAsync(survey.Id))!.Enps);
    }

    // A department below the threshold is counted as suppressed rather than
    // listed, so the screen can say some groups were too small.
    [SkippableFact]
    public async Task ADepartmentTooSmallToReportIsSuppressedNotListed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        List<Guid> engineers = await TeamAsync(
            provider, _engineeringId, Suppression.Threshold, "eng");

        List<Guid> sales = await TeamAsync(provider, _salesId, 2, "sal");

        SurveyDto survey = await OpenedAsync(provider, Asking(anonymous: true));

        foreach (Guid id in engineers.Concat(sales))
        {
            await ActAsync(provider, id);
            await AnswerAsync(provider, survey, "4");
        }

        SurveyResultsDto results = (await Surveys(provider).ResultsAsync(survey.Id))!;

        Assert.Equal(["Engineering"], results.ByDepartment.Select(row => row.DepartmentName));
        Assert.Equal(1, results.SuppressedDepartments);
    }

    // ── the trend ───────────────────────────────────────────────────────────

    // The client ships twelve months of hardcoded figures. A tenant with no
    // surveys has no trend, which is the truthful answer.
    [SkippableFact]
    public async Task ATenantWithNoAnswersHasNoTrend()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Empty(await Surveys(provider).EngagementTrendAsync());
    }

    [SkippableFact]
    public async Task TheTrendIsBuiltFromRealAnswers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        List<Guid> team = await TeamAsync(provider, _engineeringId, Suppression.Threshold, "eng");

        SurveyDto survey = await OpenedAsync(provider, Asking(anonymous: false));

        foreach (Guid id in team)
        {
            await ActAsync(provider, id);
            await AnswerAsync(provider, survey, "5");
        }

        IReadOnlyList<EngagementPointDto> trend = await Surveys(provider).EngagementTrendAsync();

        EngagementPointDto point = Assert.Single(trend);

        Assert.Equal("2026-06", point.Month);
        Assert.Equal(100m, point.CompanyWide);
        Assert.Equal(["Engineering"], point.ByDepartment.Select(row => row.DepartmentName));
    }

    // ── the board ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ReportsTheBoardsFigures()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto active = await OpenedAsync(provider, Asking(anonymous: false));
        await BuiltAsync(provider, Asking(title: "A draft survey"));

        await AnswerAsync(provider, active, "4");

        SurveyBoardDto board = await Surveys(provider).SummaryAsync();

        Assert.Equal(2, board.Total);
        Assert.Equal(1, board.Active);
        Assert.Equal(1, board.Draft);
        Assert.Equal(1, board.TotalResponses);
        Assert.Equal(75m, board.EngagementScore);
    }

    // ── deleting ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task DeletingASurveyTakesItsQuestionsAndAnswersWithIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SurveyDto survey = await OpenedAsync(provider, Asking(anonymous: false));

        await AnswerAsync(provider, survey, "4");

        Assert.True((await Surveys(provider).DeleteAsync(survey.Id)).Succeeded);

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        Assert.Empty(await read.SurveyQuestions.ToListAsync());
        Assert.Empty(await read.SurveyResponses.ToListAsync());
        Assert.Empty(await read.SurveyAnswers.ToListAsync());
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersSurveys()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await BuiltAsync(provider, Asking());

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

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = other.TenantId;

        Assert.Empty((await Surveys(provider).ListAsync(new SurveyQuery())).Items);
        Assert.Equal(0, (await Surveys(provider).SummaryAsync()).Total);
    }
}
