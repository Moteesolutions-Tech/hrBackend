using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Learning;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Learning;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Learning and development, out of the database.
//
// The quiz is what these are really about. The client sends each question's
// correct answer to the learner's browser and marks the attempt there, so a
// mandatory compliance certificate can be earned by reading the network
// response. Marking happens here, the learner's route has nowhere to put an
// answer, and the attempt limit is enforced rather than hidden behind a button.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class LearningServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly Today = new(2026, 6, 15);

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _adaId;
    private Guid _bolaId;
    private Guid _adaUserId;

    // ── the catalogue ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CreatesACourse()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        CourseResult result = await Learning(provider).CreateAsync(Writing());

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        CourseRowDto row = result.Course!.Course;

        Assert.Equal("Data protection", row.Title);
        Assert.Equal(0, row.EnrolledCount);
        Assert.Equal(0, row.CompletedCount);
        Assert.False(row.HasQuiz);
    }

    [SkippableFact]
    public async Task RefusesACourseThatLastsNoTime()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            LearningRefusal.DurationNotPositive,
            (await Learning(provider).CreateAsync(Writing() with { DurationHours = 0m }))
                .Refusal);
    }

    [SkippableFact]
    public async Task RefusesACourseThatEndsBeforeItStarts()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            LearningRefusal.EndsBeforeStart,
            (await Learning(provider).CreateAsync(
                Writing() with
                {
                    StartsOn = Today,
                    EndsOn = Today.AddDays(-1),
                })).Refusal);
    }

    // Counted from the enrolments, not kept on the course: a stored count
    // drifts the first time somebody is dropped.
    [SkippableFact]
    public async Task TheCountsFollowTheEnrolments()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid courseId = await PublishedAsync(provider);

        await Learning(provider).EnrollAsync(
            courseId, new EnrollRequest { EmployeeIds = [_adaId, _bolaId] });

        CourseDto? after = await Learning(provider).GetAsync(courseId);

        Assert.Equal(2, after!.Course.EnrolledCount);
        Assert.Equal(0, after.Course.CompletedCount);
        Assert.Equal(0, after.Course.CompletionPercent);
    }

    // ── enrolling ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task EnrolsSeveralPeopleAtOnce()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid courseId = await PublishedAsync(provider);

        EnrollResult result = await Learning(provider).EnrollAsync(
            courseId,
            new EnrollRequest { EmployeeIds = [_adaId, _bolaId], DueOn = Today.AddDays(30) });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
        Assert.Equal(2, result.Enrolled.Count);
        Assert.Empty(result.Skipped);
        Assert.All(result.Enrolled, row => Assert.Equal(Today.AddDays(30), row.DueOn));
    }

    // Somebody already on the course is skipped rather than failing the whole
    // batch — which is what an assign dialog over a filtered list needs.
    [SkippableFact]
    public async Task SomebodyAlreadyOnTheCourseIsSkippedNotRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid courseId = await PublishedAsync(provider);

        await Learning(provider).EnrollAsync(
            courseId, new EnrollRequest { EmployeeIds = [_adaId] });

        EnrollResult again = await Learning(provider).EnrollAsync(
            courseId, new EnrollRequest { EmployeeIds = [_adaId, _bolaId] });

        Assert.True(again.Succeeded, $"{again.Outcome} / {again.Refusal}");
        Assert.Equal([_bolaId], again.Enrolled.Select(row => row.EmployeeId));
        Assert.Equal([_adaId], again.Skipped);
    }

    [SkippableFact]
    public async Task ADraftCourseTakesNobody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        CourseResult draft = await Learning(provider).CreateAsync(
            Writing() with { Status = CourseStatus.Draft });

        Assert.Equal(
            LearningRefusal.CourseNotOpen,
            (await Learning(provider).EnrollAsync(
                draft.Course!.Course.Id,
                new EnrollRequest { EmployeeIds = [_adaId] })).Refusal);
    }

    // A course with two places left does not take three.
    [SkippableFact]
    public async Task ACourseWithNoPlacesLeftTakesNobodyElse()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid courseId = await PublishedAsync(provider, capacity: 1);

        Assert.True(
            (await Learning(provider).EnrollAsync(
                courseId, new EnrollRequest { EmployeeIds = [_adaId] })).Succeeded);

        Assert.Equal(
            LearningRefusal.CourseFull,
            (await Learning(provider).EnrollAsync(
                courseId, new EnrollRequest { EmployeeIds = [_bolaId] })).Refusal);

        Assert.Equal(0, (await Learning(provider).GetAsync(courseId))!.Course.PlacesLeft);
    }

    [SkippableFact]
    public async Task RefusesToEnrolSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid courseId = await PublishedAsync(provider);

        Assert.Equal(
            LearningOutcome.EmployeeNotFound,
            (await Learning(provider).EnrollAsync(
                courseId, new EnrollRequest { EmployeeIds = [Guid.NewGuid()] })).Outcome);
    }

    // ── progress ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task WatchingToTheEndCompletesTheCourse()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid _, Guid enrollmentId) = await EnrolledAsync(provider);

        EnrollmentResult half = await Learning(provider).SetProgressAsync(
            enrollmentId, new ProgressRequest { Progress = 50, ResumeSeconds = 900 });

        Assert.Equal(EnrollmentStatus.InProgress, half.Enrollment!.Status);
        Assert.Equal(900, half.Enrollment.ResumeSeconds);
        Assert.Null(half.Enrollment.CompletedOn);

        EnrollmentResult done = await Learning(provider).SetProgressAsync(
            enrollmentId, new ProgressRequest { Progress = 100 });

        Assert.Equal(EnrollmentStatus.Completed, done.Enrollment!.Status);
        Assert.Equal(Today, done.Enrollment.CompletedOn);
    }

    [SkippableFact]
    public async Task RefusesProgressOutsideAHundredPerCent()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid _, Guid enrollmentId) = await EnrolledAsync(provider);

        Assert.Equal(
            LearningRefusal.ProgressOutOfRange,
            (await Learning(provider).SetProgressAsync(
                enrollmentId, new ProgressRequest { Progress = 140 })).Refusal);
    }

    // ── the quiz ────────────────────────────────────────────────────────────

    // The one thing that must never leave the server. The client puts it in the
    // learner's payload and marks the attempt in the browser against it.
    [SkippableFact]
    public async Task TheLearnerIsNeverGivenTheAnswers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid courseId, Guid enrollmentId) = await WithQuizAsync(provider);

        // The author's view carries them, behind the module grant.
        IReadOnlyList<QuizQuestionDto> authored = await Learning(provider).QuizAsync(courseId);

        Assert.Equal(2, authored.Count);
        Assert.Equal(0, authored[0].CorrectIndex);

        // The learner's does not, because the shape has nowhere to put one.
        AskedQuizDto? asked = await Learning(provider).AskedAsync(enrollmentId);

        Assert.NotNull(asked);
        Assert.Equal(2, asked.Questions.Count);

        Assert.DoesNotContain(
            typeof(AskedQuestion).GetProperties(),
            property => property.Name.Contains("Correct", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task MarksAnAttemptAgainstTheStoredAnswers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid courseId, Guid enrollmentId) = await WithQuizAsync(provider);

        IReadOnlyList<QuizQuestionDto> questions =
            await Learning(provider).QuizAsync(courseId);

        AttemptResult result = await Learning(provider).AttemptAsync(
            enrollmentId,
            new AttemptRequest
            {
                Answers = new Dictionary<Guid, int>
                {
                    [questions[0].Id] = questions[0].CorrectIndex,
                    [questions[1].Id] = questions[1].CorrectIndex,
                },
            });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
        Assert.Equal(100m, result.Scored!.Score);
        Assert.True(result.Scored.Passed);

        // Passing completes the course.
        Assert.Equal(EnrollmentStatus.Completed, result.Enrollment!.Status);
        Assert.Equal(100, result.Enrollment.Progress);
    }

    [SkippableFact]
    public async Task AFailedAttemptIsRecordedAndDoesNotComplete()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid courseId, Guid enrollmentId) = await WithQuizAsync(provider);

        IReadOnlyList<QuizQuestionDto> questions =
            await Learning(provider).QuizAsync(courseId);

        AttemptResult result = await Learning(provider).AttemptAsync(
            enrollmentId,
            new AttemptRequest
            {
                Answers = new Dictionary<Guid, int>
                {
                    [questions[0].Id] = Wrong(questions[0]),
                    [questions[1].Id] = Wrong(questions[1]),
                },
            });

        Assert.Equal(0m, result.Scored!.Score);
        Assert.False(result.Scored.Passed);
        Assert.NotEqual(EnrollmentStatus.Completed, result.Enrollment!.Status);
        Assert.Single(result.Enrollment.Attempts);
        Assert.Null(result.Certificate);
    }

    // The limit is enforced here, not by hiding the retake button.
    [SkippableFact]
    public async Task TheAttemptLimitIsEnforced()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid courseId, Guid enrollmentId) = await WithQuizAsync(provider, maxAttempts: 2);

        IReadOnlyList<QuizQuestionDto> questions =
            await Learning(provider).QuizAsync(courseId);

        Dictionary<Guid, int> wrong = new()
        {
            [questions[0].Id] = Wrong(questions[0]),
            [questions[1].Id] = Wrong(questions[1]),
        };

        AttemptResult first = await Learning(provider).AttemptAsync(
            enrollmentId, new AttemptRequest { Answers = wrong });

        Assert.Equal(1, first.AttemptsLeft);

        AttemptResult second = await Learning(provider).AttemptAsync(
            enrollmentId, new AttemptRequest { Answers = wrong });

        Assert.Equal(0, second.AttemptsLeft);

        // Out of goes without passing, so there is nothing more to wait for.
        Assert.Equal(EnrollmentStatus.Failed, second.Enrollment!.Status);

        AttemptResult third = await Learning(provider).AttemptAsync(
            enrollmentId, new AttemptRequest { Answers = wrong });

        Assert.Equal(LearningRefusal.NoAttemptsLeft, third.Refusal);
    }

    [SkippableFact]
    public async Task SomebodyElsesEnrolmentIsNotTheirsToSit()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid _, Guid enrollmentId) = await WithQuizAsync(provider);

        ActAs(provider, await UserForAsync(_bolaId));

        Assert.Null(await Learning(provider).AskedAsync(enrollmentId));

        Assert.Equal(
            LearningOutcome.NotYours,
            (await Learning(provider).AttemptAsync(
                enrollmentId,
                new AttemptRequest { Answers = new Dictionary<Guid, int>() })).Outcome);
    }

    [SkippableFact]
    public async Task ACourseWithNoQuizHasNothingToSit()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid _, Guid enrollmentId) = await EnrolledAsync(provider);

        Assert.Null(await Learning(provider).AskedAsync(enrollmentId));

        Assert.Equal(
            LearningRefusal.NoQuiz,
            (await Learning(provider).AttemptAsync(
                enrollmentId,
                new AttemptRequest { Answers = new Dictionary<Guid, int>() })).Refusal);
    }

    [SkippableFact]
    public async Task RefusesAQuizWhoseAnswerIsNotOneOfItsOptions()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid courseId = await PublishedAsync(provider);

        Assert.Equal(
            LearningRefusal.AnswerNotAnOption,
            (await Learning(provider).SetQuizAsync(
                courseId,
                new QuizRequest
                {
                    PassingScore = 50,
                    Questions =
                    [
                        new QuizQuestionRequest
                        {
                            Prompt = "Who do you tell?",
                            Options = ["The DPO", "Nobody"],
                            CorrectIndex = 7,
                        },
                    ],
                })).Refusal);
    }

    [SkippableFact]
    public async Task SavingAQuizTwiceReplacesItRatherThanAddingToIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid courseId, Guid _) = await WithQuizAsync(provider);

        await Learning(provider).SetQuizAsync(
            courseId,
            new QuizRequest
            {
                PassingScore = 60,
                Questions =
                [
                    new QuizQuestionRequest
                    {
                        Prompt = "One question only now.",
                        Options = ["Yes", "No"],
                        CorrectIndex = 0,
                    },
                ],
            });

        Assert.Single(await Learning(provider).QuizAsync(courseId));
        Assert.Equal(60, (await Learning(provider).GetAsync(courseId))!.Course.PassingScore);
    }

    // ── the certificate that follows ────────────────────────────────────────

    // Issued on completion, with an expiry worked out from the course's own
    // validity rather than typed.
    [SkippableFact]
    public async Task PassingIssuesACertificateThatExpiresWithTheCourse()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid courseId, Guid enrollmentId) = await WithQuizAsync(provider, validityMonths: 12);

        IReadOnlyList<QuizQuestionDto> questions =
            await Learning(provider).QuizAsync(courseId);

        AttemptResult result = await Learning(provider).AttemptAsync(
            enrollmentId,
            new AttemptRequest
            {
                Answers = new Dictionary<Guid, int>
                {
                    [questions[0].Id] = questions[0].CorrectIndex,
                    [questions[1].Id] = questions[1].CorrectIndex,
                },
            });

        CertificationDto? certificate = result.Certificate;

        Assert.NotNull(certificate);
        Assert.Equal("Data protection", certificate.Title);
        Assert.Equal(CertificationKind.Course, certificate.CertificationKind);
        Assert.Equal(Today, certificate.IssuedOn);
        Assert.Equal(Today.AddMonths(12), certificate.ExpiresOn);
        Assert.Equal(CertStatus.Active, certificate.Status);

        // The register says the field follows the course, so the screen can
        // withhold it rather than offering one that is overwritten.
        Assert.True(certificate.ExpiryIsDerived);
    }

    [SkippableFact]
    public async Task CompletingTwiceDoesNotIssueTwoCertificates()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid _, Guid enrollmentId) = await EnrolledAsync(provider);

        await Learning(provider).SetProgressAsync(
            enrollmentId, new ProgressRequest { Progress = 100 });

        await Learning(provider).SetProgressAsync(
            enrollmentId, new ProgressRequest { Progress = 60 });

        await Learning(provider).SetProgressAsync(
            enrollmentId, new ProgressRequest { Progress = 100 });

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        Assert.Single(await read.Certifications.ToListAsync());
    }

    [SkippableFact]
    public async Task ACourseWithNoValidityIssuesACertificateThatNeverExpires()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid _, Guid enrollmentId) = await EnrolledAsync(provider);

        await Learning(provider).SetProgressAsync(
            enrollmentId, new ProgressRequest { Progress = 100 });

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        Certification certificate = await read.Certifications.SingleAsync();

        Assert.Null(certificate.ExpiresOn);
    }

    // ── the learner's own view ──────────────────────────────────────────────

    [SkippableFact]
    public async Task ALearnerSeesWhatTheyAreOnAndWhatElseTheyCouldTake()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid onIt = await PublishedAsync(provider);

        await Learning(provider).CreateAsync(
            Writing(title: "Fire safety") with { Status = CourseStatus.Active });

        // A draft is not in anybody's library.
        await Learning(provider).CreateAsync(
            Writing(title: "Half written") with { Status = CourseStatus.Draft });

        await Learning(provider).EnrollAsync(
            onIt, new EnrollRequest { EmployeeIds = [_adaId] });

        MyLearningDto mine = await Learning(provider).MineAsync();

        Assert.Equal(_adaId, mine.EmployeeId);
        Assert.Equal(["Data protection"], mine.Enrolled.Select(row => row.CourseTitle));
        Assert.Equal(["Fire safety"], mine.Library.Select(row => row.Title));
    }

    [SkippableFact]
    public async Task ALearnerCountsTheirOwnHoursOnlyOnceFinished()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        (Guid _, Guid enrollmentId) = await EnrolledAsync(provider);

        Assert.Equal(0m, (await Learning(provider).MineAsync()).HoursCompleted);

        await Learning(provider).SetProgressAsync(
            enrollmentId, new ProgressRequest { Progress = 100 });

        MyLearningDto mine = await Learning(provider).MineAsync();

        Assert.Equal(2m, mine.HoursCompleted);
        Assert.Equal(1, mine.Completed);
    }

    [SkippableFact]
    public async Task AnAccountWithNoEmployeeRecordHasNoLearning()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PublishedAsync(provider);

        ActAs(provider, Guid.NewGuid());

        MyLearningDto mine = await Learning(provider).MineAsync();

        Assert.Null(mine.EmployeeId);
        Assert.Empty(mine.Enrolled);
        Assert.Empty(mine.Library);
    }

    // ── compulsory training ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task ReportsWhereTheCompanyIsOnCompulsoryTraining()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid compulsory = await PublishedAsync(provider, mandatory: true);

        // Optional, so it is not part of the requirement.
        await Learning(provider).CreateAsync(
            Writing(title: "Advanced Excel") with { Status = CourseStatus.Active });

        EnrollResult enrolled = await Learning(provider).EnrollAsync(
            compulsory, new EnrollRequest { EmployeeIds = [_adaId] });

        await Learning(provider).SetProgressAsync(
            enrolled.Enrolled[0].Id, new ProgressRequest { Progress = 100 });

        MandatoryTally tally = await Learning(provider).ComplianceAsync();

        // Two people, one compulsory course.
        Assert.Equal(2, tally.Required);
        Assert.Equal(1, tally.Completed);
        Assert.Equal(50, tally.CompletionPercent);

        // Bola was never enrolled, which is already overdue rather than merely
        // missing.
        Overdue overdue = Assert.Single(tally.Overdue);

        Assert.Equal(_bolaId, overdue.EmployeeId);
        Assert.Null(overdue.DueOn);
    }

    [SkippableFact]
    public async Task WithNothingCompulsoryEverybodyIsCompliant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PublishedAsync(provider);

        MandatoryTally tally = await Learning(provider).ComplianceAsync();

        Assert.Equal(0, tally.Required);
        Assert.Equal(100, tally.CompletionPercent);
    }

    // ── the figures ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ReportsTheFiguresTheScreenLeadsWith()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Guid courseId = await PublishedAsync(provider, mandatory: true);

        EnrollResult enrolled = await Learning(provider).EnrollAsync(
            courseId,
            new EnrollRequest { EmployeeIds = [_adaId, _bolaId], DueOn = Today.AddDays(-1) });

        await Learning(provider).SetProgressAsync(
            enrolled.Enrolled[0].Id, new ProgressRequest { Progress = 100 });

        LearningSummaryDto summary = await Learning(provider).SummaryAsync();

        Assert.Equal(1, summary.Courses);
        Assert.Equal(1, summary.MandatoryCourses);
        Assert.Equal(2, summary.Enrollments);
        Assert.Equal(1, summary.Completed);
        Assert.Equal(50, summary.CompletionPercent);

        // The one still outstanding, past its date.
        Assert.Equal(1, summary.Overdue);

        // Hours that were actually delivered: a course nobody finished
        // delivered nothing.
        Assert.Equal(2m, summary.HoursDelivered);
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersCourses()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PublishedAsync(provider);

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

        Assert.Empty((await Learning(provider).ListAsync(new CourseQuery())).Items);
        Assert.Equal(0, (await Learning(provider).SummaryAsync()).Courses);
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

        _adaId = await HireAsync(provider, "Ada");
        _bolaId = await HireAsync(provider, "Bola");

        _adaUserId = await AccountAsync(_adaId, "ada");
        await AccountAsync(_bolaId, "bola");

        ActAs(provider, _adaUserId);

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static ILearningService Learning(ServiceProvider provider) =>
        Resolve<ILearningService>(provider);

    private static void ActAs(ServiceProvider provider, Guid userId) =>
        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

    private static int Wrong(QuizQuestionDto question) =>
        question.CorrectIndex == 0 ? 1 : 0;

    private static CourseRequest Writing(string title = "Data protection") => new()
    {
        Title = title,
        Description = "What to do when personal data goes somewhere it should not.",
        Category = CourseCategory.Compliance,
        Status = CourseStatus.Active,
        DeliveryMode = DeliveryMode.Online,
        Instructor = "Ada Okafor",
        Provider = "ICO",
        DurationHours = 2m,
        Tags = ["gdpr", "compliance"],
    };

    private static async Task<Guid> PublishedAsync(
        ServiceProvider provider,
        bool mandatory = false,
        int? capacity = null,
        int? validityMonths = null)
    {
        CourseResult result = await Learning(provider).CreateAsync(
            Writing() with
            {
                IsMandatory = mandatory,
                Capacity = capacity,
                ValidityMonths = validityMonths,
            });

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Course!.Course.Id;
    }

    private async Task<(Guid CourseId, Guid EnrollmentId)> EnrolledAsync(
        ServiceProvider provider)
    {
        Guid courseId = await PublishedAsync(provider);

        EnrollResult enrolled = await Learning(provider).EnrollAsync(
            courseId, new EnrollRequest { EmployeeIds = [_adaId] });

        Assert.True(enrolled.Succeeded, $"{enrolled.Outcome} / {enrolled.Refusal}");

        return (courseId, enrolled.Enrolled[0].Id);
    }

    private async Task<(Guid CourseId, Guid EnrollmentId)> WithQuizAsync(
        ServiceProvider provider,
        int? maxAttempts = null,
        int? validityMonths = null)
    {
        Guid courseId = await PublishedAsync(provider, validityMonths: validityMonths);

        CourseResult quiz = await Learning(provider).SetQuizAsync(
            courseId,
            new QuizRequest
            {
                PassingScore = 80,
                MaxAttempts = maxAttempts,
                Questions =
                [
                    new QuizQuestionRequest
                    {
                        Prompt = "Who do you report a breach to?",
                        Options = ["The DPO", "Nobody", "The client"],
                        CorrectIndex = 0,
                        Points = 1,
                    },
                    new QuizQuestionRequest
                    {
                        QuestionKind = QuestionKind.TrueFalse,
                        Prompt = "A breach must be reported within 72 hours.",
                        Options = ["True", "False"],
                        CorrectIndex = 0,
                        Points = 1,
                    },
                ],
            });

        Assert.True(quiz.Succeeded, $"{quiz.Outcome} / {quiz.Refusal}");

        EnrollResult enrolled = await Learning(provider).EnrollAsync(
            courseId, new EnrollRequest { EmployeeIds = [_adaId] });

        return (courseId, enrolled.Enrolled[0].Id);
    }

    private async Task<Guid> HireAsync(ServiceProvider provider, string first) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    private async Task<Guid> AccountAsync(Guid employeeId, string handle)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid userId = Guid.NewGuid();

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = userId,
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

        return userId;
    }

    private async Task<Guid> UserForAsync(Guid employeeId)
    {
        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        return await read.Users
            .IgnoreQueryFilters()
            .Where(user => user.EmployeeId == employeeId && user.TenantId == _tenantId)
            .Select(user => user.Id)
            .FirstAsync();
    }
}
