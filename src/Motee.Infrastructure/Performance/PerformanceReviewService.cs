using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Performance;
using Motee.Application.Tenancy;
using Motee.Domain.Performance;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Performance;

internal sealed class PerformanceReviewService(
    MoteeDbContext dbContext,
    ICurrentEmployee currentEmployee,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IPerformanceReviewService
{
    public async Task<IReadOnlyList<PerformanceReviewDto>> MineAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return [];
        }

        List<PerformanceReview> mine = await dbContext.PerformanceReviews
            .AsNoTracking()
            .Where(review => review.EmployeeId == employeeId)
            .OrderByDescending(review => review.DueDate)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(mine, cancellationToken);
    }

    public Task<ReviewResult> SaveSelfAssessmentAsync(
        Guid id,
        SelfAssessmentRequest request,
        CancellationToken cancellationToken = default) =>
        WriteSelfAsync(id, request, submitting: false, cancellationToken);

    public Task<ReviewResult> SubmitSelfAssessmentAsync(
        Guid id,
        SelfAssessmentRequest request,
        CancellationToken cancellationToken = default) =>
        WriteSelfAsync(id, request, submitting: true, cancellationToken);

    private async Task<ReviewResult> WriteSelfAsync(
        Guid id,
        SelfAssessmentRequest request,
        bool submitting,
        CancellationToken cancellationToken)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return ReviewResult.Failed(ReviewOutcome.EmployeeNotFound);
        }

        // Scoped to their own review, not just the id. Without the employee clause a
        // valid id would let anybody write a self-assessment into somebody else's
        // review, which is the one half of this record that must be theirs alone.
        PerformanceReview? review = await dbContext.PerformanceReviews
            .FirstOrDefaultAsync(
                candidate => candidate.Id == id && candidate.EmployeeId == employeeId,
                cancellationToken);

        if (review is null)
        {
            return ReviewResult.Failed(ReviewOutcome.NotFound);
        }

        if (review.SelfSubmittedAt is not null)
        {
            // Letting it happen twice would move the submission date and make a late
            // assessment look punctual.
            return ReviewResult.Failed(ReviewOutcome.AlreadySubmitted);
        }

        if (review.Status == ReviewStatus.Completed)
        {
            return ReviewResult.Failed(ReviewOutcome.AlreadyCompleted);
        }

        if (request.SelfRating is int rating && !PerformanceReviews.IsValidRating(rating))
        {
            return ReviewResult.Failed(ReviewOutcome.RatingOutOfRange);
        }

        // Handing in needs a rating and something written. A blank assessment
        // submitted is worse than one still open: the manager has nothing to read
        // and no prompt to chase.
        if (submitting
            && (request.SelfRating is null || string.IsNullOrWhiteSpace(request.Achievements)))
        {
            return ReviewResult.Failed(ReviewOutcome.AssessmentIncomplete);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        review.SelfAssessment = new SelfAssessment
        {
            Achievements = request.Achievements?.Trim(),
            Challenges = request.Challenges?.Trim(),
            DevelopmentAreas = request.DevelopmentAreas?.Trim(),
            ManagerFeedback = request.ManagerFeedback?.Trim(),
        };

        review.SelfRating = request.SelfRating;
        review.UpdatedAt = now;

        if (submitting)
        {
            review.SelfSubmittedAt = now;
        }

        // A draft counts as started too. The screen asks "is this moving", and a
        // review somebody is part way through writing is moving.
        if (review.Status == ReviewStatus.NotStarted)
        {
            review.Status = ReviewStatus.InProgress;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ReviewResult.Ok((await DescribeAsync([review], cancellationToken))[0]);
    }

    public async Task<PagedResult<PerformanceReviewDto>> ListAsync(
        ReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);

        IQueryable<PerformanceReview> source = dbContext.PerformanceReviews.AsNoTracking();

        if (query.EmployeeId is Guid employeeId)
        {
            source = source.Where(review => review.EmployeeId == employeeId);
        }

        if (query.ReviewerId is Guid reviewerId)
        {
            source = source.Where(review => review.ReviewerId == reviewerId);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            source = source.Where(review => dbContext.Employees
                .Any(employee => employee.Id == review.EmployeeId
                    && employee.DepartmentId == departmentId));
        }

        if (query.Status is ReviewStatus status)
        {
            source = source.Where(review => review.Status == status);
        }

        if (query.ReviewType is ReviewType type)
        {
            source = source.Where(review => review.ReviewType == type);
        }

        if (query.OverdueOnly == true)
        {
            source = source.Where(review => review.DueDate < today
                && review.Status != ReviewStatus.Completed);
        }

        int total = await source.CountAsync(cancellationToken);

        List<PerformanceReview> page = await source
            .OrderBy(review => review.DueDate)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<PerformanceReviewDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<PerformanceReviewDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        PerformanceReview? review = await dbContext.PerformanceReviews
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return review is null ? null : (await DescribeAsync([review], cancellationToken))[0];
    }

    public async Task<ReviewResult> OpenAsync(
        OpenReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Period))
        {
            return ReviewResult.Failed(ReviewOutcome.PeriodRequired);
        }

        if (!await dbContext.Employees.AnyAsync(
                employee => employee.Id == request.EmployeeId, cancellationToken))
        {
            return ReviewResult.Failed(ReviewOutcome.EmployeeNotFound);
        }

        if (request.ReviewerId is Guid reviewerId
            && !await dbContext.Employees.AnyAsync(
                employee => employee.Id == reviewerId, cancellationToken))
        {
            return ReviewResult.Failed(ReviewOutcome.ReviewerNotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        PerformanceReview review = new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = request.EmployeeId,
            ReviewerId = request.ReviewerId,
            ReviewType = request.ReviewType,
            Period = request.Period.Trim(),
            Status = ReviewStatus.NotStarted,
            DueDate = request.DueDate,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.PerformanceReviews.Add(review);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ReviewResult.Ok((await DescribeAsync([review], cancellationToken))[0]);
    }

    public async Task<ReviewResult> RecordManagerReviewAsync(
        Guid id,
        ManagerReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        PerformanceReview? review = await dbContext.PerformanceReviews
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (review is null)
        {
            return ReviewResult.Failed(ReviewOutcome.NotFound);
        }

        if (review.Status == ReviewStatus.Completed)
        {
            return ReviewResult.Failed(ReviewOutcome.AlreadyCompleted);
        }

        if (request.ManagerRating is int rating && !PerformanceReviews.IsValidRating(rating))
        {
            return ReviewResult.Failed(ReviewOutcome.RatingOutOfRange);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        review.ManagerRating = request.ManagerRating;
        review.Strengths = request.Strengths?.Trim();
        review.Improvements = request.Improvements?.Trim();
        review.Comments = request.Comments?.Trim();
        review.UpdatedAt = now;

        if (review.Status == ReviewStatus.NotStarted)
        {
            review.Status = ReviewStatus.InProgress;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ReviewResult.Ok((await DescribeAsync([review], cancellationToken))[0]);
    }

    public async Task<ReviewResult> CompleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        PerformanceReview? review = await dbContext.PerformanceReviews
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (review is null)
        {
            return ReviewResult.Failed(ReviewOutcome.NotFound);
        }

        if (review.Status == ReviewStatus.Completed)
        {
            return ReviewResult.Failed(ReviewOutcome.AlreadyCompleted);
        }

        // Signing off a review nobody rated leaves a finished record that says
        // nothing, and it is the record somebody's pay rise is argued from.
        if (review.ManagerRating is null)
        {
            return ReviewResult.Failed(ReviewOutcome.RatingRequired);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        review.Status = ReviewStatus.Completed;
        review.CompletedAt = now;
        review.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return ReviewResult.Ok((await DescribeAsync([review], cancellationToken))[0]);
    }

    public async Task<ReviewResult> CalibrateAsync(
        Guid id,
        int rating,
        CancellationToken cancellationToken = default)
    {
        if (!PerformanceReviews.IsValidRating(rating))
        {
            return ReviewResult.Failed(ReviewOutcome.RatingOutOfRange);
        }

        PerformanceReview? review = await dbContext.PerformanceReviews
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (review is null)
        {
            return ReviewResult.Failed(ReviewOutcome.NotFound);
        }

        // Calibration compares finished managers' judgements against each other.
        // Applying it to one still being written is adjusting a number nobody has
        // settled on yet.
        if (review.Status != ReviewStatus.Completed)
        {
            return ReviewResult.Failed(ReviewOutcome.NotCompleted);
        }

        // Set beside the manager's, never over it. Erasing what they said would
        // destroy the evidence the adjustment was made from, and leave nobody able
        // to answer why a four became a three.
        review.CalibratedRating = rating;
        review.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ReviewResult.Ok((await DescribeAsync([review], cancellationToken))[0]);
    }

    public async Task<ReviewOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        PerformanceReview? review = await dbContext.PerformanceReviews
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (review is null)
        {
            return ReviewOutcome.NotFound;
        }

        dbContext.PerformanceReviews.Remove(review);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ReviewOutcome.Succeeded;
    }

    private async Task<IReadOnlyList<PerformanceReviewDto>> DescribeAsync(
        IReadOnlyList<PerformanceReview> reviews,
        CancellationToken cancellationToken)
    {
        if (reviews.Count == 0)
        {
            return [];
        }

        DateOnly today = await TodayAsync(cancellationToken);

        List<Guid> ids =
        [
            .. reviews.Select(review => review.EmployeeId)
                .Concat(reviews
                    .Where(review => review.ReviewerId is not null)
                    .Select(review => review.ReviewerId!.Value))
                .Distinct(),
        ];

        Dictionary<Guid, Person> people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => ids.Contains(employee.Id))
            .Select(employee => new Person
            {
                Id = employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                JobTitle = employee.JobTitle,
                DepartmentName = dbContext.Departments
                    .Where(department => department.Id == employee.DepartmentId)
                    .Select(department => department.Name)
                    .FirstOrDefault(),
            })
            .ToDictionaryAsync(person => person.Id, cancellationToken);

        return
        [
            .. reviews.Select(review => new PerformanceReviewDto
            {
                Id = review.Id,
                EmployeeId = review.EmployeeId,
                EmployeeName = people.GetValueOrDefault(review.EmployeeId)?.Name ?? "Unknown",
                JobTitle = people.GetValueOrDefault(review.EmployeeId)?.JobTitle,
                DepartmentName = people.GetValueOrDefault(review.EmployeeId)?.DepartmentName,
                ReviewerId = review.ReviewerId,
                ReviewerName = review.ReviewerId is Guid reviewer
                    ? people.GetValueOrDefault(reviewer)?.Name
                    : null,
                ReviewType = review.ReviewType,
                Period = review.Period,
                Status = review.Status,
                IsOverdue = PerformanceReviews.IsOverdue(review, today),
                DueDate = review.DueDate,
                SelfAssessment = review.SelfAssessment is null
                    ? null
                    : new SelfAssessmentDto
                    {
                        Achievements = review.SelfAssessment.Achievements,
                        Challenges = review.SelfAssessment.Challenges,
                        DevelopmentAreas = review.SelfAssessment.DevelopmentAreas,
                        ManagerFeedback = review.SelfAssessment.ManagerFeedback,
                    },
                SelfRating = review.SelfRating,
                SelfSubmittedAt = review.SelfSubmittedAt,
                ManagerRating = review.ManagerRating,
                Strengths = review.Strengths,
                Improvements = review.Improvements,
                Comments = review.Comments,
                CalibratedRating = review.CalibratedRating,
                Rating = PerformanceReviews.FinalRating(review),
                CompletedAt = review.CompletedAt,
                CreatedAt = review.CreatedAt,
            }),
        ];
    }

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        return TenantClock.DayOf(timeProvider.GetUtcNow(), settings?.TimeZoneId);
    }

    private sealed record Person
    {
        public required Guid Id { get; init; }

        public required string Name { get; init; }

        public string? JobTitle { get; init; }

        public string? DepartmentName { get; init; }
    }
}
