using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Projects;
using Motee.Application.Tenancy;
using Motee.Domain.Employees;
using Motee.Domain.Projects;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Projects;

// Projects, their work, their dates and their people.
//
// Everything a project screen counts is read off the rows underneath on the way
// out: progress, the critical path, conflicting dates, health, what is due next
// and the money. Nothing is stored twice, so nothing can disagree.
internal sealed class ProjectService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IProjectService
{
    private readonly Common.TenantCalendar _calendar =
        new(dbContext, currentTenant, timeProvider);

    public async Task<PagedResult<ProjectRowDto>> ListAsync(
        ProjectQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Project> projects = dbContext.Projects.AsNoTracking();

        if (query.Status is ProjectStatus status)
        {
            projects = projects.Where(project => project.Status == status);
        }

        if (query.Priority is ProjectPriority priority)
        {
            projects = projects.Where(project => project.Priority == priority);
        }

        if (query.OwnerEmployeeId is Guid ownerId)
        {
            projects = projects.Where(project => project.OwnerEmployeeId == ownerId);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            projects = projects.Where(project => project.DepartmentId == departmentId);
        }

        if (query.OpenOnly == true)
        {
            projects = projects.Where(project =>
                project.Status == ProjectStatus.Planning
                || project.Status == ProjectStatus.Active);
        }

        if (query.ClosedOnly == true)
        {
            projects = projects.Where(project =>
                project.Status == ProjectStatus.Completed
                || project.Status == ProjectStatus.Cancelled);
        }

        if (query.BudgetedOnly == true)
        {
            projects = projects.Where(project => project.Budget > 0m);
        }

        if (query.StaffedOnly == true)
        {
            projects = projects.Where(project =>
                dbContext.ProjectAllocations.Any(
                    allocation => allocation.ProjectId == project.Id));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            projects = projects.Where(project =>
                EF.Functions.ILike(project.Name, pattern, "\\")
                || EF.Functions.ILike(project.Code, pattern, "\\")
                || (project.Client != null
                    && EF.Functions.ILike(project.Client, pattern, "\\")));
        }

        int total = await projects.CountAsync(cancellationToken);

        List<Project> page = await projects
            .OrderByDescending(project => project.StartOn)
            .ThenBy(project => project.Code)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<ProjectRowDto>
        {
            Items = await RowsAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<ProjectDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Project? project = await dbContext.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        return project is null ? null : await DescribeAsync(project, cancellationToken);
    }

    public async Task<ProjectSummaryDto> SummaryAsync(
        CancellationToken cancellationToken = default)
    {
        List<Project> projects = await dbContext.Projects
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        List<ProjectAllocation> allocations = await dbContext.ProjectAllocations
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        List<ProjectTimeEntry> entries = await dbContext.ProjectTimeEntries
            .AsNoTracking()
            .Where(entry => entry.Status == TimeEntryStatus.Approved)
            .ToListAsync(cancellationToken);

        List<ProjectTask> tasks = await dbContext.ProjectTasks
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        IReadOnlyList<OverAllocation> over = await OverAllocationsAsync(
            projects, allocations, cancellationToken);

        IReadOnlyList<Project> open =
        [
            .. projects.Where(project =>
                project.Status is ProjectStatus.Planning or ProjectStatus.Active),
        ];

        // Only projects still running have a claim on anybody's time.
        IReadOnlyList<Guid> claimed =
        [
            .. allocations
                .Where(allocation => Running(projects, allocation.ProjectId))
                .Select(allocation => allocation.EmployeeId)
                .Distinct(),
        ];

        return new ProjectSummaryDto
        {
            TotalProjects = projects.Count,
            OpenProjects = open.Count,
            StaffedProjects = projects.Count(project =>
                allocations.Exists(allocation => allocation.ProjectId == project.Id)),
            BudgetedProjects = projects.Count(project => (project.Budget ?? 0m) > 0m),
            PeopleAssigned = claimed.Count,
            TotalBudget = projects.Sum(project => project.Budget ?? 0m),
            TotalSpend = projects.Sum(project => ProjectRollups.Spend(
                [.. allocations.Where(row => row.ProjectId == project.Id)],
                [.. entries.Where(row => row.ProjectId == project.Id)])),
            AverageProgressPercent = open.Count == 0
                ? 0
                : (int)Math.Round(
                    open.Average(project => ProjectRollups.Progress(
                        [.. tasks.Where(task => task.ProjectId == project.Id)])),
                    MidpointRounding.AwayFromZero),
            OverAllocations = over,
        };
    }

    public async Task<ProjectResult> CreateAsync(
        ProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        Project project = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Code = string.Empty,
            Name = string.Empty,
            CreatedByUserId = UserId(),
            CreatedAt = now,
        };

        Apply(project, request, now);

        ProjectRefusal refusal = ProjectRules.Refusal(project);

        if (refusal != ProjectRefusal.None)
        {
            return ProjectResult.Refused(refusal);
        }

        if (request.OwnerEmployeeId is Guid ownerId
            && !await EmployedAsync(ownerId, cancellationToken))
        {
            return ProjectResult.Failed(ProjectOutcome.EmployeeNotFound);
        }

        if (await CodeTakenAsync(project.Code, null, cancellationToken))
        {
            return ProjectResult.Failed(ProjectOutcome.CodeTaken);
        }

        dbContext.Projects.Add(project);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    public async Task<ProjectResult> UpdateAsync(
        Guid id,
        ProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        Project? project = await FindAsync(id, cancellationToken);

        if (project is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        Apply(project, request, timeProvider.GetUtcNow());

        ProjectRefusal refusal = ProjectRules.Refusal(project);

        if (refusal != ProjectRefusal.None)
        {
            return ProjectResult.Refused(refusal);
        }

        if (request.OwnerEmployeeId is Guid ownerId
            && !await EmployedAsync(ownerId, cancellationToken))
        {
            return ProjectResult.Failed(ProjectOutcome.EmployeeNotFound);
        }

        if (await CodeTakenAsync(project.Code, id, cancellationToken))
        {
            return ProjectResult.Failed(ProjectOutcome.CodeTaken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    public async Task<ProjectResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Project? project = await FindAsync(id, cancellationToken);

        if (project is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        // Tasks, milestones, allocations, logged time, risks, documents and
        // go-live ticks all go with it, in the database. Orphaned hours would
        // otherwise keep counting towards the spend of a project that no
        // longer exists.
        dbContext.Projects.Remove(project);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Done();
    }

    // ── work ────────────────────────────────────────────────────────────────

    public async Task<ProjectResult> AddTaskAsync(
        Guid projectId,
        ProjectTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        Project? project = await FindAsync(projectId, cancellationToken);

        if (project is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        ProjectTask task = new()
        {
            Id = Guid.NewGuid(),
            TenantId = project.TenantId,
            ProjectId = projectId,
            Name = string.Empty,
            CreatedAt = now,
        };

        Apply(task, request, now);

        ProjectRefusal refusal = ProjectRules.TaskRefusal(task);

        if (refusal != ProjectRefusal.None)
        {
            return ProjectResult.Refused(refusal);
        }

        if (request.AssigneeEmployeeId is Guid assignee
            && !await EmployedAsync(assignee, cancellationToken))
        {
            return ProjectResult.Failed(ProjectOutcome.EmployeeNotFound);
        }

        if (!await DependenciesExistAsync(projectId, task.DependsOn, null, cancellationToken))
        {
            return ProjectResult.Refused(ProjectRefusal.UnknownTask);
        }

        dbContext.ProjectTasks.Add(task);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    public async Task<ProjectResult> UpdateTaskAsync(
        Guid projectId,
        Guid taskId,
        ProjectTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        (Project? project, ProjectTask? task) =
            await FindTaskAsync(projectId, taskId, cancellationToken);

        if (project is null || task is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        Apply(task, request, timeProvider.GetUtcNow());

        ProjectRefusal refusal = ProjectRules.TaskRefusal(task);

        if (refusal != ProjectRefusal.None)
        {
            return ProjectResult.Refused(refusal);
        }

        if (request.AssigneeEmployeeId is Guid assignee
            && !await EmployedAsync(assignee, cancellationToken))
        {
            return ProjectResult.Failed(ProjectOutcome.EmployeeNotFound);
        }

        // A task cannot depend on itself: it would wait for itself forever.
        if (!await DependenciesExistAsync(
            projectId, task.DependsOn, taskId, cancellationToken))
        {
            return ProjectResult.Refused(ProjectRefusal.UnknownTask);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    public async Task<ProjectResult> SetTaskStatusAsync(
        Guid projectId,
        Guid taskId,
        ProjectTaskStatus status,
        CancellationToken cancellationToken = default)
    {
        (Project? project, ProjectTask? task) =
            await FindTaskAsync(projectId, taskId, cancellationToken);

        if (project is null || task is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        List<ProjectTask> siblings = await dbContext.ProjectTasks
            .AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        ProjectRefusal refusal = ProjectRules.StatusRefusal(task, status, siblings);

        if (refusal != ProjectRefusal.None)
        {
            return ProjectResult.Refused(refusal);
        }

        ProjectRules.Moved(task, status);
        task.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    public async Task<ProjectResult> SetTaskProgressAsync(
        Guid projectId,
        Guid taskId,
        int percentComplete,
        CancellationToken cancellationToken = default)
    {
        (Project? project, ProjectTask? task) =
            await FindTaskAsync(projectId, taskId, cancellationToken);

        if (project is null || task is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        if (percentComplete is < 0 or > 100)
        {
            return ProjectResult.Refused(ProjectRefusal.PercentOutOfRange);
        }

        ProjectRules.Progressed(task, percentComplete);
        task.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    public async Task<ProjectResult> DeleteTaskAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        (Project? project, ProjectTask? task) =
            await FindTaskAsync(projectId, taskId, cancellationToken);

        if (project is null || task is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        dbContext.ProjectTasks.Remove(task);

        // A dependency pointing at a task that is gone, and a milestone
        // listing it, are both left behind by the delete. The schedule treats
        // an unknown dependency as satisfied so nothing is stranded, but the
        // stale ids are cleared out all the same.
        List<ProjectTask> dependents = await dbContext.ProjectTasks
            .Where(row => row.ProjectId == projectId && row.Id != taskId)
            .ToListAsync(cancellationToken);

        foreach (ProjectTask dependent in dependents.Where(row =>
            row.DependsOn.Contains(taskId)))
        {
            // Replaced rather than mutated: the jsonb converter compares by
            // serialising, so an in-place edit is not seen as a change.
            dependent.DependsOn = [.. dependent.DependsOn.Where(id => id != taskId)];
        }

        List<ProjectMilestone> milestones = await dbContext.ProjectMilestones
            .Where(row => row.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        foreach (ProjectMilestone milestone in milestones.Where(row =>
            row.TaskIds.Contains(taskId)))
        {
            milestone.TaskIds = [.. milestone.TaskIds.Where(id => id != taskId)];
        }

        List<ProjectRisk> risks = await dbContext.ProjectRisks
            .Where(row => row.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        foreach (ProjectRisk risk in risks.Where(row =>
            row.RelatedTaskIds.Contains(taskId)))
        {
            risk.RelatedTaskIds = [.. risk.RelatedTaskIds.Where(id => id != taskId)];
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    // ── dates ───────────────────────────────────────────────────────────────

    public async Task<ProjectResult> AddMilestoneAsync(
        Guid projectId,
        MilestoneRequest request,
        CancellationToken cancellationToken = default)
    {
        Project? project = await FindAsync(projectId, cancellationToken);

        if (project is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        ProjectMilestone milestone = new()
        {
            Id = Guid.NewGuid(),
            TenantId = project.TenantId,
            ProjectId = projectId,
            Name = string.Empty,
            CreatedAt = now,
        };

        Apply(milestone, request, now);

        ProjectRefusal refusal = ProjectRules.MilestoneRefusal(milestone);

        if (refusal != ProjectRefusal.None)
        {
            return ProjectResult.Refused(refusal);
        }

        if (request.ResponsibleEmployeeId is Guid responsible
            && !await EmployedAsync(responsible, cancellationToken))
        {
            return ProjectResult.Failed(ProjectOutcome.EmployeeNotFound);
        }

        dbContext.ProjectMilestones.Add(milestone);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    public async Task<ProjectResult> UpdateMilestoneAsync(
        Guid projectId,
        Guid milestoneId,
        MilestoneRequest request,
        CancellationToken cancellationToken = default)
    {
        (Project? project, ProjectMilestone? milestone) =
            await FindMilestoneAsync(projectId, milestoneId, cancellationToken);

        if (project is null || milestone is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        Apply(milestone, request, timeProvider.GetUtcNow());

        ProjectRefusal refusal = ProjectRules.MilestoneRefusal(milestone);

        if (refusal != ProjectRefusal.None)
        {
            return ProjectResult.Refused(refusal);
        }

        if (request.ResponsibleEmployeeId is Guid responsible
            && !await EmployedAsync(responsible, cancellationToken))
        {
            return ProjectResult.Failed(ProjectOutcome.EmployeeNotFound);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    public async Task<ProjectResult> ToggleMilestoneAsync(
        Guid projectId,
        Guid milestoneId,
        CancellationToken cancellationToken = default)
    {
        (Project? project, ProjectMilestone? milestone) =
            await FindMilestoneAsync(projectId, milestoneId, cancellationToken);

        if (project is null || milestone is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        Milestones.Toggle(milestone);

        // Reaching a milestone records the day it was actually met, which is
        // the figure a slipped date is measured against.
        milestone.ActualOn = Milestones.Reached(milestone)
            ? milestone.ActualOn ?? await _calendar.TodayAsync(cancellationToken)
            : null;

        milestone.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    public async Task<ProjectResult> SetMilestoneApprovalAsync(
        Guid projectId,
        Guid milestoneId,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        (Project? project, ProjectMilestone? milestone) =
            await FindMilestoneAsync(projectId, milestoneId, cancellationToken);

        if (project is null || milestone is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        milestone.ApprovedAt = approved ? timeProvider.GetUtcNow() : null;
        milestone.ApprovedByUserId = approved ? UserId() : null;
        milestone.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    public async Task<ProjectResult> DeleteMilestoneAsync(
        Guid projectId,
        Guid milestoneId,
        CancellationToken cancellationToken = default)
    {
        (Project? project, ProjectMilestone? milestone) =
            await FindMilestoneAsync(projectId, milestoneId, cancellationToken);

        if (project is null || milestone is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        dbContext.ProjectMilestones.Remove(milestone);

        List<ProjectRisk> risks = await dbContext.ProjectRisks
            .Where(row => row.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        foreach (ProjectRisk risk in risks.Where(row =>
            row.RelatedMilestoneIds.Contains(milestoneId)))
        {
            risk.RelatedMilestoneIds =
                [.. risk.RelatedMilestoneIds.Where(id => id != milestoneId)];
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    // ── people ──────────────────────────────────────────────────────────────

    public async Task<ProjectResult> SetAllocationAsync(
        Guid projectId,
        AllocationRequest request,
        CancellationToken cancellationToken = default)
    {
        Project? project = await FindAsync(projectId, cancellationToken);

        if (project is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        Employee? employee = await dbContext.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Id == request.EmployeeId
                    && row.Status != EmployeeStatus.Deleted,
                cancellationToken);

        if (employee is null)
        {
            return ProjectResult.Failed(ProjectOutcome.EmployeeNotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        ProjectAllocation? allocation = await dbContext.ProjectAllocations
            .FirstOrDefaultAsync(
                row => row.ProjectId == projectId && row.EmployeeId == request.EmployeeId,
                cancellationToken);

        bool isNew = allocation is null;

        allocation ??= new ProjectAllocation
        {
            Id = Guid.NewGuid(),
            TenantId = project.TenantId,
            ProjectId = projectId,
            EmployeeId = request.EmployeeId,
            ProjectRole = string.Empty,
            CreatedAt = now,
        };

        // Falls back to their job title: the role on the project is usually
        // the role they already hold, and an empty label reads as a bug.
        allocation.ProjectRole = string.IsNullOrWhiteSpace(request.ProjectRole)
            ? employee.JobTitle ?? "Team member"
            : request.ProjectRole.Trim();

        allocation.AllocationPercent = request.AllocationPercent;
        allocation.StartOn = request.StartOn ?? project.StartOn;
        allocation.EndOn = request.EndOn;
        allocation.HourlyRate = request.HourlyRate;
        allocation.UpdatedAt = now;

        ProjectRefusal refusal = ProjectRules.AllocationRefusal(allocation);

        if (refusal != ProjectRefusal.None)
        {
            return ProjectResult.Refused(refusal);
        }

        if (isNew)
        {
            dbContext.ProjectAllocations.Add(allocation);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    public async Task<ProjectResult> RemoveAllocationAsync(
        Guid projectId,
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        Project? project = await FindAsync(projectId, cancellationToken);

        if (project is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        ProjectAllocation? allocation = await dbContext.ProjectAllocations
            .FirstOrDefaultAsync(
                row => row.ProjectId == projectId && row.EmployeeId == employeeId,
                cancellationToken);

        if (allocation is null)
        {
            return ProjectResult.Failed(ProjectOutcome.NotFound);
        }

        dbContext.ProjectAllocations.Remove(allocation);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectResult.Ok(await DescribeAsync(project, cancellationToken));
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private Task<Project?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Projects.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

    private async Task<(Project? Project, ProjectTask? Task)> FindTaskAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        Project? project = await FindAsync(projectId, cancellationToken);

        if (project is null)
        {
            return (null, null);
        }

        ProjectTask? task = await dbContext.ProjectTasks
            .FirstOrDefaultAsync(
                row => row.Id == taskId && row.ProjectId == projectId, cancellationToken);

        return (project, task);
    }

    private async Task<(Project? Project, ProjectMilestone? Milestone)> FindMilestoneAsync(
        Guid projectId,
        Guid milestoneId,
        CancellationToken cancellationToken)
    {
        Project? project = await FindAsync(projectId, cancellationToken);

        if (project is null)
        {
            return (null, null);
        }

        ProjectMilestone? milestone = await dbContext.ProjectMilestones
            .FirstOrDefaultAsync(
                row => row.Id == milestoneId && row.ProjectId == projectId,
                cancellationToken);

        return (project, milestone);
    }

    private Task<bool> EmployedAsync(Guid employeeId, CancellationToken cancellationToken) =>
        dbContext.Employees.AnyAsync(
            employee => employee.Id == employeeId
                && employee.Status != EmployeeStatus.Deleted,
            cancellationToken);

    private async Task<bool> CodeTakenAsync(
        string code,
        Guid? exceptId,
        CancellationToken cancellationToken) =>
        await dbContext.Projects.AnyAsync(
            project => project.Code == code && project.Id != exceptId,
            cancellationToken);

    // Every dependency has to be a task on this project, and never the task
    // itself: a task waiting on itself waits forever.
    private async Task<bool> DependenciesExistAsync(
        Guid projectId,
        IReadOnlyList<Guid> dependsOn,
        Guid? self,
        CancellationToken cancellationToken)
    {
        if (dependsOn.Count == 0)
        {
            return true;
        }

        if (self is Guid id && dependsOn.Contains(id))
        {
            return false;
        }

        int found = await dbContext.ProjectTasks.CountAsync(
            task => task.ProjectId == projectId && dependsOn.Contains(task.Id),
            cancellationToken);

        return found == dependsOn.Count;
    }

    private static bool Running(IReadOnlyList<Project> projects, Guid projectId) =>
        projects.FirstOrDefault(project => project.Id == projectId) is Project found
            && found.Status is not (ProjectStatus.Completed or ProjectStatus.Cancelled);

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private void Apply(Project project, ProjectRequest request, DateTimeOffset now)
    {
        project.Code = ProjectRules.NormaliseCode(request.Code);
        project.Name = request.Name?.Trim() ?? string.Empty;
        project.Description = Blank(request.Description);
        project.Status = request.Status;
        project.Priority = request.Priority;
        project.Client = Blank(request.Client);
        project.OwnerEmployeeId = request.OwnerEmployeeId;
        project.DepartmentId = request.DepartmentId;
        project.StartOn = request.StartOn;
        project.EndOn = request.EndOn;
        project.Budget = request.Budget;
        project.CostCentreCode = Blank(request.CostCentreCode)?.ToUpperInvariant();
        project.UpdatedAt = now;
    }

    private static void Apply(ProjectTask task, ProjectTaskRequest request, DateTimeOffset now)
    {
        task.Name = request.Name?.Trim() ?? string.Empty;
        task.Description = Blank(request.Description);
        task.Status = request.Status;
        task.Priority = request.Priority;
        task.StartOn = request.StartOn;
        task.EndOn = request.EndOn;
        task.PercentComplete = request.PercentComplete;
        task.AssigneeEmployeeId = request.AssigneeEmployeeId;
        task.DependsOn = [.. (request.DependsOn ?? []).Distinct()];
        task.EstimatedHours = request.EstimatedHours;
        task.Phase = Blank(request.Phase);
        task.UpdatedAt = now;
    }

    private static void Apply(
        ProjectMilestone milestone,
        MilestoneRequest request,
        DateTimeOffset now)
    {
        milestone.Name = request.Name?.Trim() ?? string.Empty;
        milestone.Description = Blank(request.Description);
        milestone.TargetOn = request.TargetOn;
        milestone.ActualOn = request.ActualOn;
        milestone.Status = request.Status;
        milestone.PercentComplete = request.PercentComplete;
        milestone.ResponsibleEmployeeId = request.ResponsibleEmployeeId;
        milestone.TaskIds = [.. (request.TaskIds ?? []).Distinct()];
        milestone.RiskIds = [.. (request.RiskIds ?? []).Distinct()];
        milestone.UpdatedAt = now;
    }

    private async Task<IReadOnlyList<OverAllocation>> OverAllocationsAsync(
        IReadOnlyList<Project> projects,
        IReadOnlyList<ProjectAllocation> allocations,
        CancellationToken cancellationToken)
    {
        if (allocations.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, string> names = await NamesAsync(
            [.. allocations.Select(allocation => allocation.EmployeeId).Distinct()],
            cancellationToken);

        List<AllocationLine> lines = [];

        foreach (ProjectAllocation allocation in allocations)
        {
            if (projects.FirstOrDefault(project => project.Id == allocation.ProjectId)
                is not Project project)
            {
                continue;
            }

            lines.Add(new AllocationLine
            {
                ProjectId = project.Id,
                ProjectName = project.Name,
                ProjectStatus = project.Status,
                EmployeeId = allocation.EmployeeId,
                EmployeeName = names.GetValueOrDefault(allocation.EmployeeId) ?? "Unknown",
                AllocationPercent = allocation.AllocationPercent,
            });
        }

        return ProjectRollups.OverAllocations(lines);
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(
        IReadOnlyList<Guid> employeeIds,
        CancellationToken cancellationToken) =>
        employeeIds.Count == 0
            ? []
            : await dbContext.Employees
                .AsNoTracking()
                .Where(employee => employeeIds.Contains(employee.Id))
                .Select(employee => new
                {
                    employee.Id,
                    Name = employee.FirstName + " " + employee.LastName,
                })
                .ToDictionaryAsync(row => row.Id, row => row.Name, cancellationToken);

    private async Task<List<ProjectRowDto>> RowsAsync(
        IReadOnlyList<Project> projects,
        CancellationToken cancellationToken)
    {
        if (projects.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. projects.Select(project => project.Id)];

        List<ProjectTask> tasks = await dbContext.ProjectTasks
            .AsNoTracking()
            .Where(task => ids.Contains(task.ProjectId))
            .ToListAsync(cancellationToken);

        List<ProjectAllocation> allocations = await dbContext.ProjectAllocations
            .AsNoTracking()
            .Where(allocation => ids.Contains(allocation.ProjectId))
            .ToListAsync(cancellationToken);

        List<ProjectTimeEntry> entries = await dbContext.ProjectTimeEntries
            .AsNoTracking()
            .Where(entry =>
                ids.Contains(entry.ProjectId) && entry.Status == TimeEntryStatus.Approved)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, int> milestoneCounts = await dbContext.ProjectMilestones
            .AsNoTracking()
            .Where(milestone => ids.Contains(milestone.ProjectId))
            .GroupBy(milestone => milestone.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.ProjectId, row => row.Count, cancellationToken);

        Dictionary<Guid, string> owners = await NamesAsync(
            [
                .. projects
                    .Where(project => project.OwnerEmployeeId is not null)
                    .Select(project => project.OwnerEmployeeId!.Value)
                    .Distinct(),
            ],
            cancellationToken);

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        return
        [
            .. projects.Select(project => Row(
                project,
                [.. tasks.Where(task => task.ProjectId == project.Id)],
                [.. allocations.Where(row => row.ProjectId == project.Id)],
                [.. entries.Where(row => row.ProjectId == project.Id)],
                milestoneCounts.GetValueOrDefault(project.Id),
                owners,
                departments)),
        ];
    }

    private static ProjectRowDto Row(
        Project project,
        IReadOnlyList<ProjectTask> tasks,
        IReadOnlyList<ProjectAllocation> allocations,
        IReadOnlyList<ProjectTimeEntry> approved,
        int milestoneCount,
        Dictionary<Guid, string> owners,
        Dictionary<Guid, string> departments) => new()
        {
            Id = project.Id,
            Code = project.Code,
            Name = project.Name,
            Client = project.Client,
            Status = project.Status,
            Priority = project.Priority,
            OwnerEmployeeId = project.OwnerEmployeeId,
            OwnerName = project.OwnerEmployeeId is Guid ownerId
                ? owners.GetValueOrDefault(ownerId)
                : null,
            DepartmentId = project.DepartmentId,
            DepartmentName = project.DepartmentId is Guid departmentId
                ? departments.GetValueOrDefault(departmentId)
                : null,
            StartOn = project.StartOn,
            EndOn = project.EndOn,
            Budget = project.Budget,
            CostCentreCode = project.CostCentreCode,
            ProgressPercent = ProjectRollups.Progress(tasks),
            TaskCount = tasks.Count,
            CompletedTaskCount = tasks.Count(task =>
                task.Status == ProjectTaskStatus.Completed),
            MilestoneCount = milestoneCount,
            TeamSize = allocations.Count,
            TotalAllocationFte = ProjectRollups.TotalAllocation(allocations),
            Spend = ProjectRollups.Spend(allocations, approved),
        };

    private async Task<ProjectDto> DescribeAsync(
        Project project,
        CancellationToken cancellationToken)
    {
        List<ProjectTask> tasks = await dbContext.ProjectTasks
            .AsNoTracking()
            .Where(task => task.ProjectId == project.Id)
            .OrderBy(task => task.StartOn)
            .ThenBy(task => task.EndOn)
            .ToListAsync(cancellationToken);

        List<ProjectMilestone> milestones = await dbContext.ProjectMilestones
            .AsNoTracking()
            .Where(milestone => milestone.ProjectId == project.Id)
            .OrderBy(milestone => milestone.TargetOn)
            .ToListAsync(cancellationToken);

        List<ProjectAllocation> allocations = await dbContext.ProjectAllocations
            .AsNoTracking()
            .Where(allocation => allocation.ProjectId == project.Id)
            .ToListAsync(cancellationToken);

        List<ProjectTimeEntry> entries = await dbContext.ProjectTimeEntries
            .AsNoTracking()
            .Where(entry => entry.ProjectId == project.Id)
            .ToListAsync(cancellationToken);

        List<ProjectRisk> risks = await dbContext.ProjectRisks
            .AsNoTracking()
            .Where(risk => risk.ProjectId == project.Id)
            .ToListAsync(cancellationToken);

        IReadOnlyList<ProjectTimeEntry> approved =
            [.. entries.Where(entry => entry.Status == TimeEntryStatus.Approved)];

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        decimal spend = ProjectRollups.Spend(allocations, approved);
        decimal committed = ProjectRollups.CommittedSpend(tasks, allocations);
        decimal forecast = ProjectRollups.Forecast(spend, committed);

        HashSet<Guid> critical = [.. ProjectSchedule.CriticalPath(tasks)];

        // What each person is committed to everywhere else, so the team panel
        // can flag an over-booked colleague without a second call.
        Dictionary<Guid, decimal> committedEverywhere =
            await CommitmentsAsync(
                [.. allocations.Select(allocation => allocation.EmployeeId)],
                cancellationToken);

        decimal worstOver = allocations.Count == 0
            ? 0m
            : allocations.Max(allocation =>
                committedEverywhere.GetValueOrDefault(allocation.EmployeeId));

        Dictionary<Guid, Person> people = await PeopleAsync(
            [
                .. tasks
                    .Where(task => task.AssigneeEmployeeId is not null)
                    .Select(task => task.AssigneeEmployeeId!.Value)
                    .Concat(milestones
                        .Where(milestone => milestone.ResponsibleEmployeeId is not null)
                        .Select(milestone => milestone.ResponsibleEmployeeId!.Value))
                    .Concat(allocations.Select(allocation => allocation.EmployeeId))
                    .Distinct(),
            ],
            cancellationToken);

        Dictionary<Guid, string> owners = project.OwnerEmployeeId is Guid ownerId
            ? await NamesAsync([ownerId], cancellationToken)
            : [];

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        return new ProjectDto
        {
            Project = Row(
                project, tasks, allocations, approved, milestones.Count, owners, departments),
            Description = project.Description,
            CreatedByUserId = project.CreatedByUserId,
            CreatedAt = project.CreatedAt,
            Tasks =
            [
                .. tasks.Select(task =>
                {
                    IReadOnlyList<ProjectTask> waiting = ProjectSchedule.Blockers(task, tasks);

                    return new ProjectTaskDto
                    {
                        Id = task.Id,
                        Name = task.Name,
                        Description = task.Description,
                        Status = task.Status,
                        Priority = task.Priority,
                        StartOn = task.StartOn,
                        EndOn = task.EndOn,
                        PercentComplete = task.PercentComplete,
                        AssigneeEmployeeId = task.AssigneeEmployeeId,
                        AssigneeName = task.AssigneeEmployeeId is Guid assignee
                            ? people.GetValueOrDefault(assignee)?.Name
                            : null,
                        DependsOn = [.. task.DependsOn],
                        EstimatedHours = task.EstimatedHours,
                        Phase = task.Phase,
                        IsBlocked = waiting.Count > 0,
                        WaitingOn = [.. waiting.Select(blocker => blocker.Name)],
                        IsOnCriticalPath = critical.Contains(task.Id),
                        IsOverdue = task.Status
                            is not (ProjectTaskStatus.Completed or ProjectTaskStatus.Cancelled)
                            && task.EndOn < today,
                        ApprovedHours = approved
                            .Where(entry => entry.TaskId == task.Id)
                            .Sum(entry => entry.Hours),
                    };
                }),
            ],
            Milestones =
            [
                .. milestones.Select(milestone => new ProjectMilestoneDto
                {
                    Id = milestone.Id,
                    Name = milestone.Name,
                    Description = milestone.Description,
                    TargetOn = milestone.TargetOn,
                    ActualOn = milestone.ActualOn,
                    Status = milestone.Status,
                    PercentComplete = milestone.PercentComplete,
                    IsReached = Milestones.Reached(milestone),
                    IsOverdue = !Milestones.Reached(milestone) && milestone.TargetOn < today,
                    ResponsibleEmployeeId = milestone.ResponsibleEmployeeId,
                    ResponsibleName = milestone.ResponsibleEmployeeId is Guid responsible
                        ? people.GetValueOrDefault(responsible)?.Name
                        : null,
                    TaskIds = [.. milestone.TaskIds],
                    RiskIds = [.. milestone.RiskIds],
                    IsApproved = Milestones.IsApproved(milestone),
                    ApprovedByUserId = milestone.ApprovedByUserId,
                    ApprovedAt = milestone.ApprovedAt,
                }),
            ],
            Allocations =
            [
                .. allocations
                    .OrderByDescending(allocation => allocation.AllocationPercent)
                    .Select(allocation => new ProjectAllocationDto
                    {
                        EmployeeId = allocation.EmployeeId,
                        EmployeeName = people.GetValueOrDefault(allocation.EmployeeId)?.Name,
                        JobTitle = people.GetValueOrDefault(allocation.EmployeeId)?.JobTitle,
                        DepartmentName = people
                            .GetValueOrDefault(allocation.EmployeeId)?.DepartmentName,
                        ProjectRole = allocation.ProjectRole,
                        AllocationPercent = allocation.AllocationPercent,
                        StartOn = allocation.StartOn,
                        EndOn = allocation.EndOn,
                        HourlyRate = allocation.HourlyRate,
                        TaskCount = ProjectRollups.TaskCountFor(tasks, allocation.EmployeeId),
                        ApprovedHours = approved
                            .Where(entry => entry.EmployeeId == allocation.EmployeeId)
                            .Sum(entry => entry.Hours),
                        CommittedPercentEverywhere =
                            committedEverywhere.GetValueOrDefault(allocation.EmployeeId),
                    }),
            ],
            RiskCount = risks.Count,
            OpenRiskCount = risks.Count(risk => risk.Status != RiskStatus.Closed),
            CriticalPathProgressPercent = ProjectRollups.CriticalPathProgress(tasks),
            CriticalPathTaskIds = [.. ProjectSchedule.CriticalPath(tasks)],
            ScheduleConflicts = ProjectSchedule.Conflicts(tasks),
            Health = ProjectHealthCheck.Of(
                tasks, milestones, project.Budget, forecast, worstOver, today),
            Alert = CriticalPathAlerts.Of(tasks, milestones),
            NextActions = NextActions.Of(tasks, milestones, today),
            ApprovedHours = ProjectRollups.ApprovedHours(entries),
            CommittedSpend = committed,
            ForecastFinalCost = forecast,
            RemainingBudget = ProjectRollups.RemainingBudget(project.Budget, forecast),
            BudgetUtilisationPercent =
                ProjectRollups.UtilisationPercent(spend, project.Budget),
        };
    }

    // How much of each person's time is claimed across every project still
    // running.
    private async Task<Dictionary<Guid, decimal>> CommitmentsAsync(
        IReadOnlyList<Guid> employeeIds,
        CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
        {
            return [];
        }

        return await dbContext.ProjectAllocations
            .AsNoTracking()
            .Where(allocation =>
                employeeIds.Contains(allocation.EmployeeId)
                && dbContext.Projects.Any(project =>
                    project.Id == allocation.ProjectId
                    && project.Status != ProjectStatus.Completed
                    && project.Status != ProjectStatus.Cancelled))
            .GroupBy(allocation => allocation.EmployeeId)
            .Select(group => new
            {
                EmployeeId = group.Key,
                Percent = group.Sum(allocation => allocation.AllocationPercent),
            })
            .ToDictionaryAsync(row => row.EmployeeId, row => row.Percent, cancellationToken);
    }

    private async Task<Dictionary<Guid, Person>> PeopleAsync(
        IReadOnlyList<Guid> employeeIds,
        CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        return await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                employee.JobTitle,
                employee.DepartmentId,
            })
            .ToDictionaryAsync(
                row => row.Id,
                row => new Person(
                    row.Name,
                    row.JobTitle,
                    row.DepartmentId is Guid id ? departments.GetValueOrDefault(id) : null),
                cancellationToken);
    }

    private static string Escaped(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record Person(string Name, string? JobTitle, string? DepartmentName);
}
