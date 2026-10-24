using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Performance;
using Motee.Domain.Employees;
using Motee.Domain.Performance;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Performance;

internal sealed class PerformanceFeedbackService(
    MoteeDbContext dbContext,
    ICurrentEmployee currentEmployee,
    TimeProvider timeProvider) : IPerformanceFeedbackService
{
    public async Task<IReadOnlyList<PerformanceFeedbackDto>> AboutMeAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return [];
        }

        List<PerformanceFeedback> about = await dbContext.PerformanceFeedback
            .AsNoTracking()
            .Where(feedback => feedback.AboutEmployeeId == employeeId)
            .OrderByDescending(feedback => feedback.CreatedAt)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(about, cancellationToken);
    }

    public async Task<IReadOnlyList<FeedbackRequestDto>> MyRequestsAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return [];
        }

        return await DescribeRequestsAsync(
            await dbContext.FeedbackRequests
                .AsNoTracking()
                .Where(request => request.AskedByEmployeeId == employeeId)
                .OrderByDescending(request => request.CreatedAt)
                .ToListAsync(cancellationToken),
            cancellationToken);
    }

    public async Task<IReadOnlyList<FeedbackRequestDto>> AwaitingMeAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return [];
        }

        // Open ones only. A colleague's list of things to do should not be a
        // history of everything anybody ever asked them.
        return await DescribeRequestsAsync(
            await dbContext.FeedbackRequests
                .AsNoTracking()
                .Where(request => request.AskedOfEmployeeId == employeeId
                    && request.Status == FeedbackRequestStatus.Pending)
                .OrderBy(request => request.CreatedAt)
                .ToListAsync(cancellationToken),
            cancellationToken);
    }

    public async Task<FeedbackRequestResult> AskAsync(
        AskForFeedbackRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return FeedbackRequestResult.Failed(FeedbackOutcome.EmployeeNotFound);
        }

        if (FeedbackRules.IsSelfDirected(employeeId, request.ColleagueId))
        {
            return FeedbackRequestResult.Failed(FeedbackOutcome.CannotBeAboutYourself);
        }

        if (!await dbContext.Employees.AnyAsync(
                employee => employee.Id == request.ColleagueId, cancellationToken))
        {
            return FeedbackRequestResult.Failed(FeedbackOutcome.EmployeeNotFound);
        }

        // A second ask of the same person is a nudge, not a new question — and two
        // in their list look like two different ones.
        if (await dbContext.FeedbackRequests.AnyAsync(
                candidate => candidate.AskedByEmployeeId == employeeId
                    && candidate.AskedOfEmployeeId == request.ColleagueId
                    && candidate.Status == FeedbackRequestStatus.Pending,
                cancellationToken))
        {
            return FeedbackRequestResult.Failed(FeedbackOutcome.AlreadyAsked);
        }

        FeedbackRequest ask = new()
        {
            Id = Guid.NewGuid(),
            AskedByEmployeeId = employeeId,
            AskedOfEmployeeId = request.ColleagueId,
            Context = request.Context?.Trim(),
            Status = FeedbackRequestStatus.Pending,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        dbContext.FeedbackRequests.Add(ask);
        await dbContext.SaveChangesAsync(cancellationToken);

        return FeedbackRequestResult.Ok(
            (await DescribeRequestsAsync([ask], cancellationToken))[0]);
    }

    public async Task<FeedbackRequestResult> DeclineAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return FeedbackRequestResult.Failed(FeedbackOutcome.EmployeeNotFound);
        }

        // Scoped to the person asked, not just the id: declining is theirs to do,
        // and an id alone would let anybody clear somebody else's queue.
        FeedbackRequest? ask = await dbContext.FeedbackRequests
            .FirstOrDefaultAsync(
                candidate => candidate.Id == requestId
                    && candidate.AskedOfEmployeeId == employeeId,
                cancellationToken);

        if (ask is null)
        {
            return FeedbackRequestResult.Failed(FeedbackOutcome.NotFound);
        }

        if (ask.Status != FeedbackRequestStatus.Pending)
        {
            return FeedbackRequestResult.Failed(FeedbackOutcome.AlreadyResolved);
        }

        ask.Status = FeedbackRequestStatus.Declined;
        ask.RespondedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return FeedbackRequestResult.Ok(
            (await DescribeRequestsAsync([ask], cancellationToken))[0]);
    }

    public async Task<FeedbackResult> GiveAsync(
        GiveFeedbackRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return FeedbackResult.Failed(FeedbackOutcome.EmployeeNotFound);
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return FeedbackResult.Failed(FeedbackOutcome.MessageRequired);
        }

        if (FeedbackRules.IsSelfDirected(request.AboutEmployeeId, employeeId))
        {
            return FeedbackResult.Failed(FeedbackOutcome.CannotBeAboutYourself);
        }

        Employee? about = await dbContext.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(
                employee => employee.Id == request.AboutEmployeeId, cancellationToken);

        if (about is null)
        {
            return FeedbackResult.Failed(FeedbackOutcome.EmployeeNotFound);
        }

        Guid? writerManagerId = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .Select(employee => employee.ManagerId)
            .FirstOrDefaultAsync(cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        FeedbackRequest? answering = null;

        if (request.RequestId is Guid requestId)
        {
            // Checked against the caller, so a valid id cannot be used to close
            // somebody else's request by answering it.
            answering = await dbContext.FeedbackRequests
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == requestId
                        && candidate.AskedOfEmployeeId == employeeId,
                    cancellationToken);

            if (answering is null)
            {
                return FeedbackResult.Failed(FeedbackOutcome.NotFound);
            }

            if (answering.Status != FeedbackRequestStatus.Pending)
            {
                return FeedbackResult.Failed(FeedbackOutcome.AlreadyResolved);
            }

            answering.Status = FeedbackRequestStatus.Answered;
            answering.RespondedAt = now;
        }

        PerformanceFeedback feedback = new()
        {
            Id = Guid.NewGuid(),
            AboutEmployeeId = request.AboutEmployeeId,
            FromEmployeeId = employeeId,

            // Worked out here rather than taken from the request. A report
            // labelling their own feedback "manager" would give it weight it has
            // not earned, and the label is what a 360 is read through.
            Type = FeedbackRules.Classify(
                request.AboutEmployeeId, employeeId, about.ManagerId, writerManagerId),
            Message = request.Message.Trim(),
            RequestId = answering?.Id,
            CreatedAt = now,
        };

        dbContext.PerformanceFeedback.Add(feedback);
        await dbContext.SaveChangesAsync(cancellationToken);

        return FeedbackResult.Ok((await DescribeAsync([feedback], cancellationToken))[0]);
    }

    public async Task<PagedResult<PerformanceFeedbackDto>> ListAsync(
        FeedbackQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<PerformanceFeedback> source = dbContext.PerformanceFeedback.AsNoTracking();

        if (query.AboutEmployeeId is Guid aboutId)
        {
            source = source.Where(feedback => feedback.AboutEmployeeId == aboutId);
        }

        if (query.Type is FeedbackType type)
        {
            source = source.Where(feedback => feedback.Type == type);
        }

        int total = await source.CountAsync(cancellationToken);

        List<PerformanceFeedback> page = await source
            .OrderByDescending(feedback => feedback.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<PerformanceFeedbackDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    private async Task<IReadOnlyList<PerformanceFeedbackDto>> DescribeAsync(
        IReadOnlyList<PerformanceFeedback> feedback,
        CancellationToken cancellationToken)
    {
        if (feedback.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, Person> people = await PeopleAsync(
            [
                .. feedback.Select(entry => entry.AboutEmployeeId)
                    .Concat(feedback.Select(entry => entry.FromEmployeeId))
                    .Distinct(),
            ],
            cancellationToken);

        return
        [
            .. feedback.Select(entry => new PerformanceFeedbackDto
            {
                Id = entry.Id,
                AboutEmployeeId = entry.AboutEmployeeId,
                AboutEmployeeName =
                    people.GetValueOrDefault(entry.AboutEmployeeId)?.Name ?? "Unknown",
                FromEmployeeId = entry.FromEmployeeId,
                FromName = people.GetValueOrDefault(entry.FromEmployeeId)?.Name ?? "Unknown",
                FromJobTitle = people.GetValueOrDefault(entry.FromEmployeeId)?.JobTitle,
                Type = entry.Type,
                Message = entry.Message,
                RequestId = entry.RequestId,
                CreatedAt = entry.CreatedAt,
            }),
        ];
    }

    private async Task<IReadOnlyList<FeedbackRequestDto>> DescribeRequestsAsync(
        IReadOnlyList<FeedbackRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, Person> people = await PeopleAsync(
            [
                .. requests.Select(request => request.AskedByEmployeeId)
                    .Concat(requests.Select(request => request.AskedOfEmployeeId))
                    .Distinct(),
            ],
            cancellationToken);

        return
        [
            .. requests.Select(request => new FeedbackRequestDto
            {
                Id = request.Id,
                AskedByEmployeeId = request.AskedByEmployeeId,
                AskedByName = people.GetValueOrDefault(request.AskedByEmployeeId)?.Name
                    ?? "Unknown",
                AskedOfEmployeeId = request.AskedOfEmployeeId,
                AskedOfName = people.GetValueOrDefault(request.AskedOfEmployeeId)?.Name
                    ?? "Unknown",
                Context = request.Context,
                Status = request.Status,
                CreatedAt = request.CreatedAt,
                RespondedAt = request.RespondedAt,
            }),
        ];
    }

    private async Task<Dictionary<Guid, Person>> PeopleAsync(
        List<Guid> ids,
        CancellationToken cancellationToken) =>
        await dbContext.Employees
            .AsNoTracking()
            .Where(employee => ids.Contains(employee.Id))
            .Select(employee => new Person
            {
                Id = employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                JobTitle = employee.JobTitle,
            })
            .ToDictionaryAsync(person => person.Id, cancellationToken);

    private sealed record Person
    {
        public required Guid Id { get; init; }

        public required string Name { get; init; }

        public string? JobTitle { get; init; }
    }
}
