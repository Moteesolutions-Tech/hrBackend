using Microsoft.EntityFrameworkCore;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Application.Onboarding;
using Motee.Application.Tenancy;
using Motee.Domain.Approvals;
using Motee.Domain.Employees;
using Motee.Domain.Onboarding;
using Motee.Infrastructure.Employees;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Onboarding;

internal sealed class OnboardingService(
    MoteeDbContext dbContext,
    IApprovalService approvals,
    AvatarLinker avatars,
    ICurrentEmployee currentEmployee,
    TimeProvider timeProvider) : IOnboardingService
{
    // What the engine files these approvals under. The engine never reads it — it stores
    // the pair and hands it back — but keeping it a named constant means the string that
    // starts a review and the string that finds one cannot drift apart.
    private const string SubjectType = "onboarding";

    public async Task<PagedResult<OnboardingDto>> ListAsync(
        OnboardingQuery query,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        IQueryable<OnboardingRecord> matching = Filter(query, today);

        int total = await matching.CountAsync(cancellationToken);

        List<OnboardingDto> items = await Project(matching)
            // The ones who started soonest first: whoever is furthest into their first
            // weeks with something still outstanding is the one HR need to see.
            .OrderBy(record => record.StartDate ?? DateOnly.MaxValue)
            .ThenBy(record => record.EmployeeName)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<OnboardingDto>
        {
            Items = await HydrateAsync(items, today, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<OnboardingDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        return await OneAsync(
            dbContext.OnboardingRecords.Where(item => item.Id == id), today, cancellationToken);
    }

    public async Task<OnboardingDto?> MineAsync(CancellationToken cancellationToken = default)
    {
        // Resolved through the shared service rather than inline. Users are not
        // tenant-scoped, so this query needs its own tenant clause, and having one
        // implementation of it beats repeating the clause per module and eventually
        // forgetting it.
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return null;
        }

        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        return await OneAsync(
            dbContext.OnboardingRecords.Where(item => item.EmployeeId == employeeId),
            today,
            cancellationToken);
    }

    private async Task<OnboardingDto?> OneAsync(
        IQueryable<OnboardingRecord> records,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        List<OnboardingDto> found = await Project(records).Take(1).ToListAsync(cancellationToken);

        return (await HydrateAsync(found, today, cancellationToken)).FirstOrDefault();
    }

    public async Task<OnboardingRecord> EnsureAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        OnboardingRecord? existing = await dbContext.OnboardingRecords
            .FirstOrDefaultAsync(record => record.EmployeeId == employeeId, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        OnboardingRecord record = new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            Stage = OnboardingStage.PreBoarding,
            Submission = OnboardingSubmission.NotStarted,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.OnboardingRecords.Add(record);

        // Not saved here. The caller creating the employee owns the transaction, and an
        // onboarding record committed alongside an employee that then fails to save
        // would point at nobody.
        return record;
    }

    public async Task<OnboardingResult> SubmitAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        OnboardingRecord? record = await dbContext.OnboardingRecords
            .FirstOrDefaultAsync(item => item.EmployeeId == employeeId, cancellationToken);

        if (record is null)
        {
            return OnboardingResult.Failed(OnboardingOutcome.NotFound);
        }

        if (!OnboardingLifecycle.CanSubmit(record.Submission))
        {
            return OnboardingResult.Failed(OnboardingOutcome.NotAllowed);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        record.Submission = OnboardingSubmission.Submitted;
        record.SubmittedAt = now;
        record.UpdatedAt = now;

        // Only the routes where the joiner filled it in get a review. When HR typed the
        // record themselves — manual entry, bulk upload — there is nothing for a manager
        // to check that HR did not already have in front of them, and a chain there is
        // ceremony that leaves real approvals buried among rubber stamps.
        bool joinerFilledItIn = await dbContext.Employees
            .Where(employee => employee.Id == employeeId)
            .Select(employee => employee.OnboardingMethod)
            .FirstOrDefaultAsync(cancellationToken) == OnboardingMethod.Invite;

        if (joinerFilledItIn)
        {
            ApprovalResult review = await approvals.StartAsync(
                new StartApprovalRequest
                {
                    DocumentType = ApprovalDocumentTypes.Onboarding,
                    SubjectType = SubjectType,
                    SubjectId = record.Id,
                    SubjectEmployeeId = employeeId,
                },
                cancellationToken);

            if (!review.Succeeded)
            {
                // The submission stands even when the review cannot start. Refusing it
                // would leave the joiner unable to hand in their pack because of a
                // configuration problem that is not theirs and that they cannot fix.
                await dbContext.SaveChangesAsync(cancellationToken);

                return OnboardingResult.Failed(OnboardingOutcome.ReviewUnstartable);
            }

            record.ApprovalInstanceId = review.Approval!.Id;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return await ResultFor(record, cancellationToken);
    }

    public async Task<OnboardingResult> MoveAsync(
        Guid id,
        OnboardingStage stage,
        CancellationToken cancellationToken = default)
    {
        OnboardingRecord? record = await dbContext.OnboardingRecords
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (record is null)
        {
            return OnboardingResult.Failed(OnboardingOutcome.NotFound);
        }

        if (!OnboardingLifecycle.CanMove(record.Stage, stage))
        {
            return OnboardingResult.Failed(OnboardingOutcome.NotAllowed);
        }

        record.Stage = stage;
        record.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return await ResultFor(record, cancellationToken);
    }

    public async Task<OnboardingResult> CompleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        OnboardingRecord? record = await dbContext.OnboardingRecords
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (record is null)
        {
            return OnboardingResult.Failed(OnboardingOutcome.NotFound);
        }

        if (OnboardingLifecycle.IsFinal(record.Stage))
        {
            return OnboardingResult.Failed(OnboardingOutcome.NotAllowed);
        }

        // A record with no chain is approved by definition: either HR entered everything
        // themselves, or no chain was configured when they submitted. Neither is the
        // joiner's doing, and neither is a reason to leave them onboarding forever.
        bool approved = !record.ApprovalInstanceId.HasValue
            || await dbContext.ApprovalInstances
                .AnyAsync(
                    instance => instance.Id == record.ApprovalInstanceId.Value
                        && instance.Status == ApprovalStatus.Approved,
                    cancellationToken);

        if (!OnboardingLifecycle.CanComplete(record.Submission, approved))
        {
            return OnboardingResult.Failed(
                record.Submission == OnboardingSubmission.Submitted
                    ? OnboardingOutcome.ReviewIncomplete
                    : OnboardingOutcome.NotAllowed);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        record.Stage = OnboardingStage.Completed;
        record.CompletedAt = now;
        record.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return await ResultFor(record, cancellationToken);
    }

    private IQueryable<OnboardingRecord> Filter(OnboardingQuery query, DateOnly today)
    {
        IQueryable<OnboardingRecord> records = dbContext.OnboardingRecords;

        if (query.Stage.HasValue)
        {
            records = records.Where(record => record.Stage == query.Stage.Value);
        }

        if (query.Submission.HasValue)
        {
            records = records.Where(record => record.Submission == query.Submission.Value);
        }

        if (query.DepartmentId.HasValue)
        {
            records = records.Where(record => dbContext.Employees
                .Any(employee => employee.Id == record.EmployeeId
                    && employee.DepartmentId == query.DepartmentId.Value));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string term = $"%{query.Search.Trim()}%";

            records = records.Where(record => dbContext.Employees
                .Any(employee => employee.Id == record.EmployeeId
                    && (EF.Functions.ILike(employee.FirstName + " " + employee.LastName, term)
                        || EF.Functions.ILike(employee.Email ?? string.Empty, term)
                        || EF.Functions.ILike(employee.JobTitle ?? string.Empty, term))));
        }

        if (query.Overdue.HasValue)
        {
            // The one place the rule has to be SQL, because filtering happens in the
            // database. The projection cannot share this expression — it reaches across
            // to the employee for the start date — so it computes the flag in C# from
            // the fields it already selected, and OnboardingOverdueTests asserts the two
            // agree. A comment would not have caught them drifting; that test will.
            records = query.Overdue.Value
                ? records.Where(record =>
                    record.Submission != OnboardingSubmission.Submitted
                    && record.Stage != OnboardingStage.Completed
                    && dbContext.Employees.Any(employee => employee.Id == record.EmployeeId
                        && employee.StartDate != null
                        && employee.StartDate <= today))
                : records.Where(record =>
                    record.Submission == OnboardingSubmission.Submitted
                    || record.Stage == OnboardingStage.Completed
                    || !dbContext.Employees.Any(employee => employee.Id == record.EmployeeId
                        && employee.StartDate != null
                        && employee.StartDate <= today));
        }

        return records;
    }

    // Their start date has passed and their pack is still not in. Somebody who starts
    // next month is not late; somebody who started last week and has submitted nothing is
    // exactly who HR opened this screen to find.
    private static bool IsOverdue(OnboardingDto record, DateOnly today) =>
        record.Submission != OnboardingSubmission.Submitted
        && record.Stage != OnboardingStage.Completed
        && record.StartDate.HasValue
        && record.StartDate.Value <= today;

    private IQueryable<OnboardingDto> Project(IQueryable<OnboardingRecord> records) =>
        records.Select(record => new OnboardingDto
        {
            Id = record.Id,
            EmployeeId = record.EmployeeId,

            // Read through from the employee rather than copied, so somebody who corrects
            // the spelling of their name is not left misspelled on this screen.
            EmployeeName = dbContext.Employees
                .Where(employee => employee.Id == record.EmployeeId)
                .Select(employee => employee.FirstName + " " + employee.LastName)
                .FirstOrDefault() ?? "Unknown",
            Email = dbContext.Employees
                .Where(employee => employee.Id == record.EmployeeId)
                .Select(employee => employee.Email)
                .FirstOrDefault() ?? string.Empty,
            JobTitle = dbContext.Employees
                .Where(employee => employee.Id == record.EmployeeId)
                .Select(employee => employee.JobTitle)
                .FirstOrDefault(),
            DepartmentName = dbContext.Employees
                .Where(employee => employee.Id == record.EmployeeId)
                .SelectMany(employee => dbContext.Departments
                    .Where(department => department.Id == employee.DepartmentId)
                    .Select(department => department.Name))
                .FirstOrDefault(),
            AvatarFileId = dbContext.Employees
                .Where(employee => employee.Id == record.EmployeeId)
                .Select(employee => employee.AvatarFileId)
                .FirstOrDefault(),
            StartDate = dbContext.Employees
                .Where(employee => employee.Id == record.EmployeeId)
                .Select(employee => employee.StartDate)
                .FirstOrDefault(),
            Method = dbContext.Employees
                .Where(employee => employee.Id == record.EmployeeId)
                .Select(employee => employee.OnboardingMethod)
                .FirstOrDefault(),
            Stage = record.Stage,
            Submission = record.Submission,
            SubmittedAt = record.SubmittedAt,
            CompletedAt = record.CompletedAt,

            // All three are filled after materialising. The review is a service call, the
            // avatar link has to be signed, and the overdue flag needs a comparison the
            // projection would have to reach back across to the employee to make.
            IsOverdue = false,
            AvatarUrl = null,
            Review = null,
        });

    private async Task<OnboardingResult> ResultFor(
        OnboardingRecord record,
        CancellationToken cancellationToken)
    {
        OnboardingDto? dto = await GetAsync(record.Id, cancellationToken);

        return dto is null
            ? OnboardingResult.Failed(OnboardingOutcome.NotFound)
            : OnboardingResult.Ok(dto);
    }

    // Everything a projection could not produce: the flag needing today's date, the link
    // needing signing, and the review living in another service.
    //
    // Batched rather than per row. The list is the screen HR leave open, and a query per
    // joiner is how a page of twenty-five becomes fifty round trips.
    private async Task<IReadOnlyList<OnboardingDto>> HydrateAsync(
        List<OnboardingDto> records,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return records;
        }

        IReadOnlyDictionary<Guid, string> avatarUrls = await avatars.UrlsForAsync(
            records.Select(record => record.AvatarFileId), cancellationToken);

        IReadOnlyDictionary<Guid, ApprovalDto> reviews = await approvals.LatestForSubjectsAsync(
            SubjectType, [.. records.Select(record => record.Id)], cancellationToken);

        return
        [
            .. records.Select(record => record with
            {
                IsOverdue = IsOverdue(record, today),
                AvatarUrl = record.AvatarFileId is Guid fileId
                    && avatarUrls.TryGetValue(fileId, out string? url)
                        ? url
                        : null,
                Review = reviews.GetValueOrDefault(record.Id),
            }),
        ];
    }
}
