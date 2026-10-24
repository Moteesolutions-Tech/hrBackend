using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Performance;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Performance;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

// A review, written by two people.
//
// The halves are the thing to protect. An employee must not be able to write into
// somebody else's review, a manager must not overwrite the self-assessment, and
// calibration must not erase what the manager actually said — that last one is the
// evidence anybody arguing about a rating would need.
[Collection(PostgresCollection.Name)]
public class PerformanceReviewTests(PostgresFixture fixture)
{
    private Guid _tenantId;
    private Guid _employeeId;
    private Guid _managerId;

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

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        Guid departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _employeeId = await HireAsync(provider, departmentId, "Bola", "bola@acme.com");
        _managerId = await HireAsync(provider, departmentId, "Cara", "cara@acme.com");

        Guid userId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext(_tenantId))
        {
            seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
            {
                Id = userId,
                TenantId = _tenantId,
                EmployeeId = _employeeId,
                FirstName = "Bola",
                LastName = "Okafor",
                Email = "bola-login@acme.com",
                NormalizedEmail = "BOLA-LOGIN@ACME.COM",
                EmailConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();

        return provider;
    }

    private static async Task<Guid> HireAsync(
        ServiceProvider provider,
        Guid departmentId,
        string first,
        string email) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = email,
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
        })).Employee!.Id;

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private async Task<Guid> OpenAsync(ServiceProvider provider, Guid? forEmployee = null)
    {
        ReviewResult opened = await Resolve<IPerformanceReviewService>(provider)
            .OpenAsync(new OpenReviewRequest
            {
                EmployeeId = forEmployee ?? _employeeId,
                ReviewerId = _managerId,
                ReviewType = ReviewType.Annual,
                Period = "H1 2026",
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
            });

        Assert.True(opened.Succeeded, opened.Outcome.ToString());

        return opened.Review!.Id;
    }

    private static SelfAssessmentRequest Assessment(int? rating = 4) => new()
    {
        Achievements = "Shipped the migration.",
        Challenges = "Scope moved twice.",
        DevelopmentAreas = "Delegation.",
        ManagerFeedback = "More context on priorities, please.",
        SelfRating = rating,
    };

    [SkippableFact]
    public async Task OpeningAReviewStartsItUnbegun()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        PerformanceReviewDto review = (await Resolve<IPerformanceReviewService>(provider)
            .GetAsync(await OpenAsync(provider)))!;

        Assert.Equal(ReviewStatus.NotStarted, review.Status);
        Assert.Equal("Cara Okafor", review.ReviewerName);
        Assert.Null(review.Rating);
        Assert.False(review.IsOverdue);
    }

    // A draft leaves it waiting on them, which is what the unset submission date
    // means — the manager has nothing to read yet.
    [SkippableFact]
    public async Task SavingADraftDoesNotHandItIn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceReviewService reviews = Resolve<IPerformanceReviewService>(provider);
        Guid id = await OpenAsync(provider);

        PerformanceReviewDto saved =
            (await reviews.SaveSelfAssessmentAsync(id, Assessment())).Review!;

        Assert.Null(saved.SelfSubmittedAt);
        Assert.Equal(ReviewStatus.InProgress, saved.Status);
        Assert.Equal("Shipped the migration.", saved.SelfAssessment!.Achievements);
    }

    [SkippableFact]
    public async Task HandingInStampsTheDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceReviewService reviews = Resolve<IPerformanceReviewService>(provider);
        Guid id = await OpenAsync(provider);

        PerformanceReviewDto submitted =
            (await reviews.SubmitSelfAssessmentAsync(id, Assessment())).Review!;

        Assert.NotNull(submitted.SelfSubmittedAt);
        Assert.Equal(4, submitted.SelfRating);
    }

    // A blank assessment handed in is worse than one still open: the manager has
    // nothing to read and no prompt to chase.
    [SkippableFact]
    public async Task HandingInNeedsARatingAndSomethingWritten()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceReviewService reviews = Resolve<IPerformanceReviewService>(provider);
        Guid id = await OpenAsync(provider);

        Assert.Equal(
            ReviewOutcome.AssessmentIncomplete,
            (await reviews.SubmitSelfAssessmentAsync(id, Assessment(rating: null))).Outcome);

        Assert.Equal(
            ReviewOutcome.AssessmentIncomplete,
            (await reviews.SubmitSelfAssessmentAsync(
                id, Assessment() with { Achievements = "  " })).Outcome);
    }

    // Twice would move the submission date and make a late assessment look punctual.
    [SkippableFact]
    public async Task ASelfAssessmentCannotBeHandedInTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceReviewService reviews = Resolve<IPerformanceReviewService>(provider);
        Guid id = await OpenAsync(provider);

        await reviews.SubmitSelfAssessmentAsync(id, Assessment());

        Assert.Equal(
            ReviewOutcome.AlreadySubmitted,
            (await reviews.SubmitSelfAssessmentAsync(id, Assessment(5))).Outcome);
        Assert.Equal(
            ReviewOutcome.AlreadySubmitted,
            (await reviews.SaveSelfAssessmentAsync(id, Assessment(5))).Outcome);
    }

    // The door that must stay shut. A valid id is not permission to write your half
    // into somebody else's review.
    [SkippableFact]
    public async Task SomebodyCannotWriteIntoAnotherPersonsReview()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceReviewService reviews = Resolve<IPerformanceReviewService>(provider);

        // Opened for the manager, while the caller is Bola.
        Guid theirs = await OpenAsync(provider, forEmployee: _managerId);

        Assert.Equal(
            ReviewOutcome.NotFound,
            (await reviews.SubmitSelfAssessmentAsync(theirs, Assessment())).Outcome);
    }

    // Signing off a review nobody rated leaves a finished record saying nothing,
    // and it is the record a pay rise gets argued from.
    [SkippableFact]
    public async Task AReviewCannotBeCompletedWithoutARating()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceReviewService reviews = Resolve<IPerformanceReviewService>(provider);
        Guid id = await OpenAsync(provider);

        Assert.Equal(ReviewOutcome.RatingRequired, (await reviews.CompleteAsync(id)).Outcome);
    }

    [SkippableFact]
    public async Task TheManagersHalfIsRecordedSeparately()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceReviewService reviews = Resolve<IPerformanceReviewService>(provider);
        Guid id = await OpenAsync(provider);

        await reviews.SubmitSelfAssessmentAsync(id, Assessment());

        PerformanceReviewDto rated = (await reviews.RecordManagerReviewAsync(
            id,
            new ManagerReviewRequest
            {
                ManagerRating = 3,
                Strengths = "Steady under pressure.",
                Improvements = "Ask for help sooner.",
            })).Review!;

        Assert.Equal(3, rated.ManagerRating);

        // And the employee's half is untouched by it.
        Assert.Equal(4, rated.SelfRating);
        Assert.Equal("Shipped the migration.", rated.SelfAssessment!.Achievements);

        // No calibration yet, so the manager's is the one that counts.
        Assert.Equal(3, rated.Rating);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task RatingsOutsideOneToFiveAreRefused(int rating)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceReviewService reviews = Resolve<IPerformanceReviewService>(provider);
        Guid id = await OpenAsync(provider);

        Assert.Equal(
            ReviewOutcome.RatingOutOfRange,
            (await reviews.RecordManagerReviewAsync(
                id, new ManagerReviewRequest { ManagerRating = rating })).Outcome);
    }

    // Calibration compares finished judgements. Applying it to one still being
    // written is adjusting a number nobody has settled on.
    [SkippableFact]
    public async Task OnlyAFinishedReviewCanBeCalibrated()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceReviewService reviews = Resolve<IPerformanceReviewService>(provider);
        Guid id = await OpenAsync(provider);

        await reviews.RecordManagerReviewAsync(
            id, new ManagerReviewRequest { ManagerRating = 4 });

        Assert.Equal(ReviewOutcome.NotCompleted, (await reviews.CalibrateAsync(id, 3)).Outcome);
    }

    // The evidence anybody arguing about a rating would need: what the manager said,
    // and what HR changed it to.
    [SkippableFact]
    public async Task CalibrationSitsBesideTheManagersRatingRatherThanOverIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceReviewService reviews = Resolve<IPerformanceReviewService>(provider);
        Guid id = await OpenAsync(provider);

        await reviews.RecordManagerReviewAsync(
            id, new ManagerReviewRequest { ManagerRating = 4 });
        await reviews.CompleteAsync(id);

        PerformanceReviewDto calibrated = (await reviews.CalibrateAsync(id, 3)).Review!;

        Assert.Equal(4, calibrated.ManagerRating);
        Assert.Equal(3, calibrated.CalibratedRating);

        // And the one that counts is HR's.
        Assert.Equal(3, calibrated.Rating);
    }

    // Finished is finished, for the same reason a goal's is: reopening would leave
    // CompletedAt describing a completion that no longer happened.
    [SkippableFact]
    public async Task ACompletedReviewDoesNotReopen()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceReviewService reviews = Resolve<IPerformanceReviewService>(provider);
        Guid id = await OpenAsync(provider);

        await reviews.RecordManagerReviewAsync(
            id, new ManagerReviewRequest { ManagerRating = 4 });
        await reviews.CompleteAsync(id);

        Assert.Equal(ReviewOutcome.AlreadyCompleted, (await reviews.CompleteAsync(id)).Outcome);
        Assert.Equal(
            ReviewOutcome.AlreadyCompleted,
            (await reviews.RecordManagerReviewAsync(
                id, new ManagerReviewRequest { ManagerRating = 5 })).Outcome);
    }

    // Derived, so it becomes true at midnight and stops the moment it is signed off.
    [SkippableFact]
    public async Task APastDueReviewReadsAsOverdueUntilItIsFinished()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        IPerformanceReviewService reviews = Resolve<IPerformanceReviewService>(provider);

        Guid id = (await reviews.OpenAsync(new OpenReviewRequest
        {
            EmployeeId = _employeeId,
            ReviewerId = _managerId,
            ReviewType = ReviewType.Probation,
            Period = "Probation",
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5),
        })).Review!.Id;

        Assert.True((await reviews.GetAsync(id))!.IsOverdue);

        await reviews.RecordManagerReviewAsync(
            id, new ManagerReviewRequest { ManagerRating = 3 });

        Assert.False((await reviews.CompleteAsync(id)).Review!.IsOverdue);
    }

    [SkippableFact]
    public async Task AReviewNeedsAPeriod()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            ReviewOutcome.PeriodRequired,
            (await Resolve<IPerformanceReviewService>(provider).OpenAsync(new OpenReviewRequest
            {
                EmployeeId = _employeeId,
                ReviewType = ReviewType.Annual,
                Period = "   ",
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
            })).Outcome);
    }
}
