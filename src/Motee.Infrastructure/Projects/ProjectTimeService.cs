using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Projects;
using Motee.Domain.Projects;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Projects;

// Hours logged against a project.
//
// Two rules the client cannot enforce. Time can only be logged by somebody
// actually on the project, which it checks, and a person's day cannot add up to
// more than a day — which it does not: it guards one entry at a time, so twenty
// hours on each of three projects passes.
internal sealed class ProjectTimeService(
    MoteeDbContext dbContext,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IProjectTimeService
{
    public async Task<PagedResult<TimeEntryDto>> ListAsync(
        TimeEntryQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<ProjectTimeEntry> entries = dbContext.ProjectTimeEntries.AsNoTracking();

        if (query.ProjectId is Guid projectId)
        {
            entries = entries.Where(entry => entry.ProjectId == projectId);
        }

        if (query.TaskId is Guid taskId)
        {
            entries = entries.Where(entry => entry.TaskId == taskId);
        }

        if (query.EmployeeId is Guid employeeId)
        {
            entries = entries.Where(entry => entry.EmployeeId == employeeId);
        }

        if (query.Status is TimeEntryStatus status)
        {
            entries = entries.Where(entry => entry.Status == status);
        }

        if (query.From is DateOnly from)
        {
            entries = entries.Where(entry => entry.WorkedOn >= from);
        }

        if (query.To is DateOnly to)
        {
            entries = entries.Where(entry => entry.WorkedOn <= to);
        }

        int total = await entries.CountAsync(cancellationToken);

        List<ProjectTimeEntry> page = await entries
            .OrderByDescending(entry => entry.WorkedOn)
            .ThenByDescending(entry => entry.CreatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<TimeEntryDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<TimeTally> TallyAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        List<ProjectTimeEntry> entries = await dbContext.ProjectTimeEntries
            .AsNoTracking()
            .Where(entry => entry.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        return new TimeTally
        {
            ApprovedHours = ProjectRollups.ApprovedHours(entries),
            AwaitingApprovalHours = entries
                .Where(entry => entry.Status == TimeEntryStatus.Submitted)
                .Sum(entry => entry.Hours),
            TotalHours = entries.Sum(entry => entry.Hours),
            Entries = entries.Count,
        };
    }

    public async Task<TimeEntryResult> LogAsync(
        Guid projectId,
        TimeEntryRequest request,
        CancellationToken cancellationToken = default)
    {
        Project? project = await dbContext.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == projectId, cancellationToken);

        if (project is null)
        {
            return TimeEntryResult.Failed(ProjectOutcome.NotFound);
        }

        if (request.TaskId is Guid taskId
            && !await dbContext.ProjectTasks.AnyAsync(
                task => task.Id == taskId && task.ProjectId == projectId, cancellationToken))
        {
            return TimeEntryResult.Refused(ProjectRefusal.UnknownTask);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        ProjectTimeEntry entry = new()
        {
            Id = Guid.NewGuid(),
            TenantId = project.TenantId,
            ProjectId = projectId,
            TaskId = request.TaskId,
            EmployeeId = request.EmployeeId,
            WorkedOn = request.WorkedOn,
            Hours = request.Hours,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),

            // The screen's button says "log time", not "save a draft".
            Status = request.Status ?? TimeEntryStatus.Submitted,
            CreatedAt = now,
            UpdatedAt = now,
        };

        bool allocated = await dbContext.ProjectAllocations.AnyAsync(
            allocation => allocation.ProjectId == projectId
                && allocation.EmployeeId == request.EmployeeId,
            cancellationToken);

        // Everything else the same person has already logged that day, across
        // every project.
        decimal otherHours = await dbContext.ProjectTimeEntries
            .Where(row => row.EmployeeId == request.EmployeeId
                && row.WorkedOn == request.WorkedOn
                && row.Status != TimeEntryStatus.Rejected)
            .SumAsync(row => row.Hours, cancellationToken);

        ProjectRefusal refusal = ProjectRules.TimeRefusal(entry, allocated, otherHours);

        if (refusal != ProjectRefusal.None)
        {
            return TimeEntryResult.Refused(refusal);
        }

        dbContext.ProjectTimeEntries.Add(entry);

        await dbContext.SaveChangesAsync(cancellationToken);

        return TimeEntryResult.Ok((await DescribeAsync([entry], cancellationToken))[0]);
    }

    public async Task<TimeEntryResult> SetStatusAsync(
        Guid projectId,
        Guid entryId,
        TimeEntryStatus status,
        CancellationToken cancellationToken = default)
    {
        ProjectTimeEntry? entry = await dbContext.ProjectTimeEntries
            .FirstOrDefaultAsync(
                row => row.Id == entryId && row.ProjectId == projectId, cancellationToken);

        if (entry is null)
        {
            return TimeEntryResult.Failed(ProjectOutcome.NotFound);
        }

        entry.Status = status;

        // Who settled it and when. Approving is the moment the hours become a
        // cost, so it is worth being able to say who did it.
        bool settled = status is TimeEntryStatus.Approved or TimeEntryStatus.Rejected;

        entry.ResolvedAt = settled ? timeProvider.GetUtcNow() : null;
        entry.ResolvedByUserId = settled ? UserId() : null;
        entry.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return TimeEntryResult.Ok((await DescribeAsync([entry], cancellationToken))[0]);
    }

    public async Task<TimeEntryResult> DeleteAsync(
        Guid projectId,
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        ProjectTimeEntry? entry = await dbContext.ProjectTimeEntries
            .FirstOrDefaultAsync(
                row => row.Id == entryId && row.ProjectId == projectId, cancellationToken);

        if (entry is null)
        {
            return TimeEntryResult.Failed(ProjectOutcome.NotFound);
        }

        dbContext.ProjectTimeEntries.Remove(entry);

        await dbContext.SaveChangesAsync(cancellationToken);

        return TimeEntryResult.Done();
    }

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private async Task<List<TimeEntryDto>> DescribeAsync(
        IReadOnlyList<ProjectTimeEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        List<Guid> employeeIds = [.. entries.Select(entry => entry.EmployeeId).Distinct()];
        List<Guid> projectIds = [.. entries.Select(entry => entry.ProjectId).Distinct()];

        List<Guid> taskIds =
        [
            .. entries
                .Where(entry => entry.TaskId is not null)
                .Select(entry => entry.TaskId!.Value)
                .Distinct(),
        ];

        Dictionary<Guid, string> people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
            })
            .ToDictionaryAsync(row => row.Id, row => row.Name, cancellationToken);

        Dictionary<Guid, string> projects = await dbContext.Projects
            .AsNoTracking()
            .Where(project => projectIds.Contains(project.Id))
            .ToDictionaryAsync(
                project => project.Id, project => project.Name, cancellationToken);

        Dictionary<Guid, string> tasks = taskIds.Count == 0
            ? []
            : await dbContext.ProjectTasks
                .AsNoTracking()
                .Where(task => taskIds.Contains(task.Id))
                .ToDictionaryAsync(task => task.Id, task => task.Name, cancellationToken);

        return
        [
            .. entries.Select(entry => new TimeEntryDto
            {
                Id = entry.Id,
                ProjectId = entry.ProjectId,
                ProjectName = projects.GetValueOrDefault(entry.ProjectId),
                TaskId = entry.TaskId,
                TaskName = entry.TaskId is Guid taskId
                    ? tasks.GetValueOrDefault(taskId)
                    : null,
                EmployeeId = entry.EmployeeId,
                EmployeeName = people.GetValueOrDefault(entry.EmployeeId),
                WorkedOn = entry.WorkedOn,
                Hours = entry.Hours,
                Notes = entry.Notes,
                Status = entry.Status,
                ResolvedByUserId = entry.ResolvedByUserId,
                ResolvedAt = entry.ResolvedAt,
                CreatedAt = entry.CreatedAt,
            }),
        ];
    }
}
