using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Performance;
using Motee.Application.Tenancy;
using Motee.Domain.Performance;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Performance;

internal sealed class PerformanceGoalService(
    MoteeDbContext dbContext,
    ICurrentEmployee currentEmployee,
    ICurrentTenant currentTenant,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IPerformanceGoalService
{
    public async Task<IReadOnlyList<PerformanceGoalDto>> MineAsync(
        GoalQuery query,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return [];
        }

        // Their own, whatever the query said about whose. A client passing somebody
        // else's id here would otherwise read a colleague's goals through the
        // self-service door.
        List<PerformanceGoal> mine = await Filter(
                dbContext.PerformanceGoals.AsNoTracking(),
                query with { EmployeeId = employeeId },
                await TodayAsync(cancellationToken))
            .OrderBy(goal => goal.DueDate)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(mine, cancellationToken);
    }

    public async Task<PagedResult<PerformanceGoalDto>> ListAsync(
        GoalQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<PerformanceGoal> source = Filter(
            dbContext.PerformanceGoals.AsNoTracking(),
            query,
            await TodayAsync(cancellationToken));

        int total = await source.CountAsync(cancellationToken);

        List<PerformanceGoal> page = await source
            .OrderBy(goal => goal.DueDate)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<PerformanceGoalDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<PerformanceGoalDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        PerformanceGoal? goal = await dbContext.PerformanceGoals
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return goal is null ? null : (await DescribeAsync([goal], cancellationToken))[0];
    }

    public async Task<GoalResult> CreateMineAsync(
        GoalRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return GoalResult.Failed(GoalOutcome.EmployeeNotFound);
        }

        return await CreateAsync(employeeId, request, cancellationToken);
    }

    public async Task<GoalResult> CreateAsync(
        Guid employeeId,
        GoalRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return GoalResult.Failed(GoalOutcome.TitleRequired);
        }

        if (!await dbContext.Employees.AnyAsync(
                employee => employee.Id == employeeId, cancellationToken))
        {
            return GoalResult.Failed(GoalOutcome.EmployeeNotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        PerformanceGoal goal = new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            Category = request.Category,
            Status = GoalStatus.OnTrack,
            Progress = 0,
            DueDate = request.DueDate,
            GoalType = request.GoalType?.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.PerformanceGoals.Add(goal);
        await dbContext.SaveChangesAsync(cancellationToken);

        return GoalResult.Ok((await DescribeAsync([goal], cancellationToken))[0]);
    }

    public async Task<GoalResult> UpdateAsync(
        Guid id,
        GoalRequest request,
        CancellationToken cancellationToken = default)
    {
        PerformanceGoal? goal = await dbContext.PerformanceGoals
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (goal is null)
        {
            return GoalResult.Failed(GoalOutcome.NotFound);
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return GoalResult.Failed(GoalOutcome.TitleRequired);
        }

        goal.Title = request.Title.Trim();
        goal.Description = request.Description?.Trim();
        goal.Category = request.Category;
        goal.DueDate = request.DueDate;
        goal.GoalType = request.GoalType?.Trim();
        goal.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return GoalResult.Ok((await DescribeAsync([goal], cancellationToken))[0]);
    }

    public async Task<GoalResult> RecordProgressAsync(
        Guid id,
        ProgressUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Progress is < PerformanceGoals.MinimumProgress
            or > PerformanceGoals.MaximumProgress)
        {
            return GoalResult.Failed(GoalOutcome.ProgressOutOfRange);
        }

        PerformanceGoal? goal = await dbContext.PerformanceGoals
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (goal is null)
        {
            return GoalResult.Failed(GoalOutcome.NotFound);
        }

        if (PerformanceGoals.IsFinal(goal.Status))
        {
            return GoalResult.Failed(GoalOutcome.AlreadyFinal);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        goal.Progress = request.Progress;

        // Newest first, matching how the screen reads them.
        goal.Updates.Insert(0, new GoalUpdate
        {
            At = now,
            Progress = request.Progress,
            Note = request.Note?.Trim(),
            ByUserId = Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null,
        });

        goal.UpdatedAt = now;

        // Reaching a hundred does not finish it. Somebody decides a goal is done,
        // and the number arriving there is a claim about progress rather than the
        // decision — a goal can sit at 100 while the work is being checked.
        await dbContext.SaveChangesAsync(cancellationToken);

        return GoalResult.Ok((await DescribeAsync([goal], cancellationToken))[0]);
    }

    public async Task<GoalResult> ChangeStatusAsync(
        Guid id,
        GoalStatus status,
        CancellationToken cancellationToken = default)
    {
        PerformanceGoal? goal = await dbContext.PerformanceGoals
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (goal is null)
        {
            return GoalResult.Failed(GoalOutcome.NotFound);
        }

        // Finished and abandoned are both ends. A goal that comes back is a new goal;
        // reopening would leave CompletedAt describing a completion that no longer
        // happened.
        if (PerformanceGoals.IsFinal(goal.Status))
        {
            return GoalResult.Failed(GoalOutcome.AlreadyFinal);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        goal.Status = status;
        goal.UpdatedAt = now;

        if (status == GoalStatus.Completed)
        {
            goal.CompletedAt = now;

            // Completing at sixty per cent would leave a finished goal with an
            // unfinished bar. The decision carries the number with it.
            goal.Progress = PerformanceGoals.MaximumProgress;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return GoalResult.Ok((await DescribeAsync([goal], cancellationToken))[0]);
    }

    public async Task<GoalOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        PerformanceGoal? goal = await dbContext.PerformanceGoals
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (goal is null)
        {
            return GoalOutcome.NotFound;
        }

        dbContext.PerformanceGoals.Remove(goal);
        await dbContext.SaveChangesAsync(cancellationToken);

        return GoalOutcome.Succeeded;
    }

    private IQueryable<PerformanceGoal> Filter(
        IQueryable<PerformanceGoal> source,
        GoalQuery query,
        DateOnly today)
    {
        if (query.EmployeeId is Guid employeeId)
        {
            source = source.Where(goal => goal.EmployeeId == employeeId);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            source = source.Where(goal => dbContext.Employees
                .Any(employee => employee.Id == goal.EmployeeId
                    && employee.DepartmentId == departmentId));
        }

        if (query.Status is GoalStatus status)
        {
            source = source.Where(goal => goal.Status == status);
        }

        if (query.Category is GoalCategory category)
        {
            source = source.Where(goal => goal.Category == category);
        }

        // Expressed in SQL rather than filtered after loading, because "show me the
        // overdue ones" on a company of eight hundred would otherwise read every
        // goal to find forty.
        if (query.OverdueOnly == true)
        {
            source = source.Where(goal => goal.DueDate < today
                && goal.Status != GoalStatus.Completed
                && goal.Status != GoalStatus.Cancelled);
        }

        return source;
    }

    private async Task<IReadOnlyList<PerformanceGoalDto>> DescribeAsync(
        IReadOnlyList<PerformanceGoal> goals,
        CancellationToken cancellationToken)
    {
        if (goals.Count == 0)
        {
            return [];
        }

        DateOnly today = await TodayAsync(cancellationToken);

        List<Guid> employeeIds = [.. goals.Select(goal => goal.EmployeeId).Distinct()];

        Dictionary<Guid, (string Name, string? Department)> people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                Department = dbContext.Departments
                    .Where(department => department.Id == employee.DepartmentId)
                    .Select(department => department.Name)
                    .FirstOrDefault(),
            })
            .ToDictionaryAsync(row => row.Id, row => (row.Name, row.Department), cancellationToken);

        List<Guid> authors =
        [
            .. goals
                .SelectMany(goal => goal.Updates)
                .Where(update => update.ByUserId is not null)
                .Select(update => update.ByUserId!.Value)
                .Distinct(),
        ];

        Dictionary<Guid, string> names = authors.Count == 0
            ? []
            : await dbContext.Users
                .AsNoTracking()
                .Where(user => authors.Contains(user.Id))
                .ToDictionaryAsync(
                    user => user.Id,
                    user => user.FirstName + " " + user.LastName,
                    cancellationToken);

        return
        [
            .. goals.Select(goal => new PerformanceGoalDto
            {
                Id = goal.Id,
                EmployeeId = goal.EmployeeId,
                EmployeeName = people.TryGetValue(goal.EmployeeId, out (string Name, string? Department) person)
                    ? person.Name
                    : "Unknown",
                DepartmentName = people.GetValueOrDefault(goal.EmployeeId).Department,
                Title = goal.Title,
                Description = goal.Description,
                Category = goal.Category,
                Status = goal.Status,
                IsOverdue = PerformanceGoals.IsOverdue(goal, today),
                Progress = goal.Progress,
                DueDate = goal.DueDate,
                GoalType = goal.GoalType,
                Updates =
                [
                    .. goal.Updates.Select(update => new GoalUpdateDto
                    {
                        At = update.At,
                        Progress = update.Progress,
                        Note = update.Note,
                        ByName = update.ByUserId is Guid author
                            ? names.GetValueOrDefault(author)
                            : null,
                    }),
                ],
                CreatedAt = goal.CreatedAt,
                CompletedAt = goal.CompletedAt,
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
}
