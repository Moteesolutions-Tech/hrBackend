using Microsoft.EntityFrameworkCore;
using Motee.Application.Projects;
using Motee.Application.Tenancy;
using Motee.Domain.Employees;
using Motee.Domain.Projects;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Projects;

// The risk and issue register.
internal sealed class ProjectRiskService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : IProjectRiskService
{
    private readonly Common.TenantCalendar _calendar =
        new(dbContext, currentTenant, timeProvider);

    public async Task<IReadOnlyList<ProjectRiskDto>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        List<ProjectRisk> risks = await dbContext.ProjectRisks
            .AsNoTracking()
            .Where(risk => risk.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(risks, cancellationToken);
    }

    public async Task<RiskTally> TallyAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        List<ProjectRisk> risks = await dbContext.ProjectRisks
            .AsNoTracking()
            .Where(risk => risk.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        return new RiskTally
        {
            Total = risks.Count,
            Open = risks.Count(risk => risk.Status != RiskStatus.Closed),
            Issues = risks.Count(risk => risk.RiskKind == RiskKind.Issue),
            HighImpactOpen = risks.Count(risk =>
                risk.Impact == RiskImpact.High && risk.Status != RiskStatus.Closed),
        };
    }

    public async Task<ProjectRiskResult> AddAsync(
        Guid projectId,
        RiskRequest request,
        CancellationToken cancellationToken = default)
    {
        Project? project = await dbContext.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == projectId, cancellationToken);

        if (project is null)
        {
            return ProjectRiskResult.Failed(ProjectOutcome.NotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        ProjectRisk risk = new()
        {
            Id = Guid.NewGuid(),
            TenantId = project.TenantId,
            ProjectId = projectId,
            Title = string.Empty,
            CreatedAt = now,
        };

        Apply(risk, request, now);

        ProjectRefusal refusal = ProjectRules.RiskRefusal(risk);

        if (refusal != ProjectRefusal.None)
        {
            return ProjectRiskResult.Refused(refusal);
        }

        if (request.OwnerEmployeeId is Guid ownerId
            && !await EmployedAsync(ownerId, cancellationToken))
        {
            return ProjectRiskResult.Failed(ProjectOutcome.EmployeeNotFound);
        }

        dbContext.ProjectRisks.Add(risk);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectRiskResult.Ok((await DescribeAsync([risk], cancellationToken))[0]);
    }

    public async Task<ProjectRiskResult> UpdateAsync(
        Guid projectId,
        Guid riskId,
        RiskRequest request,
        CancellationToken cancellationToken = default)
    {
        ProjectRisk? risk = await dbContext.ProjectRisks
            .FirstOrDefaultAsync(
                row => row.Id == riskId && row.ProjectId == projectId, cancellationToken);

        if (risk is null)
        {
            return ProjectRiskResult.Failed(ProjectOutcome.NotFound);
        }

        Apply(risk, request, timeProvider.GetUtcNow());

        ProjectRefusal refusal = ProjectRules.RiskRefusal(risk);

        if (refusal != ProjectRefusal.None)
        {
            return ProjectRiskResult.Refused(refusal);
        }

        if (request.OwnerEmployeeId is Guid ownerId
            && !await EmployedAsync(ownerId, cancellationToken))
        {
            return ProjectRiskResult.Failed(ProjectOutcome.EmployeeNotFound);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectRiskResult.Ok((await DescribeAsync([risk], cancellationToken))[0]);
    }

    public async Task<ProjectRiskResult> DeleteAsync(
        Guid projectId,
        Guid riskId,
        CancellationToken cancellationToken = default)
    {
        ProjectRisk? risk = await dbContext.ProjectRisks
            .FirstOrDefaultAsync(
                row => row.Id == riskId && row.ProjectId == projectId, cancellationToken);

        if (risk is null)
        {
            return ProjectRiskResult.Failed(ProjectOutcome.NotFound);
        }

        dbContext.ProjectRisks.Remove(risk);

        // A milestone listing this risk would otherwise point at nothing.
        List<ProjectMilestone> milestones = await dbContext.ProjectMilestones
            .Where(row => row.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        foreach (ProjectMilestone milestone in milestones.Where(row =>
            row.RiskIds.Contains(riskId)))
        {
            // Replaced rather than mutated: the jsonb converter compares by
            // serialising, so an in-place edit is not seen as a change.
            milestone.RiskIds = [.. milestone.RiskIds.Where(id => id != riskId)];
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return ProjectRiskResult.Done();
    }

    private Task<bool> EmployedAsync(Guid employeeId, CancellationToken cancellationToken) =>
        dbContext.Employees.AnyAsync(
            employee => employee.Id == employeeId
                && employee.Status != EmployeeStatus.Deleted,
            cancellationToken);

    private static void Apply(ProjectRisk risk, RiskRequest request, DateTimeOffset now)
    {
        risk.RiskKind = request.RiskKind;
        risk.Title = request.Title?.Trim() ?? string.Empty;
        risk.Description = string.IsNullOrWhiteSpace(request.Description)
            ? null
            : request.Description.Trim();
        risk.OwnerEmployeeId = request.OwnerEmployeeId;
        risk.Impact = request.Impact;
        risk.Status = request.Status;
        risk.DueOn = request.DueOn;
        risk.RelatedTaskIds = [.. (request.RelatedTaskIds ?? []).Distinct()];
        risk.RelatedMilestoneIds = [.. (request.RelatedMilestoneIds ?? []).Distinct()];
        risk.UpdatedAt = now;
    }

    private async Task<List<ProjectRiskDto>> DescribeAsync(
        IReadOnlyList<ProjectRisk> risks,
        CancellationToken cancellationToken)
    {
        if (risks.Count == 0)
        {
            return [];
        }

        List<Guid> ownerIds =
        [
            .. risks
                .Where(risk => risk.OwnerEmployeeId is not null)
                .Select(risk => risk.OwnerEmployeeId!.Value)
                .Distinct(),
        ];

        Dictionary<Guid, string> owners = ownerIds.Count == 0
            ? []
            : await dbContext.Employees
                .AsNoTracking()
                .Where(employee => ownerIds.Contains(employee.Id))
                .Select(employee => new
                {
                    employee.Id,
                    Name = employee.FirstName + " " + employee.LastName,
                })
                .ToDictionaryAsync(row => row.Id, row => row.Name, cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return
        [
            .. risks
                // Open first, then worst impact, then oldest: the order the
                // register is worked through.
                .OrderBy(risk => risk.Status == RiskStatus.Closed)
                .ThenByDescending(risk => risk.Impact)
                .ThenBy(risk => risk.CreatedAt)
                .Select(risk => new ProjectRiskDto
                {
                    Id = risk.Id,
                    ProjectId = risk.ProjectId,
                    RiskKind = risk.RiskKind,
                    Title = risk.Title,
                    Description = risk.Description,
                    OwnerEmployeeId = risk.OwnerEmployeeId,
                    OwnerName = risk.OwnerEmployeeId is Guid ownerId
                        ? owners.GetValueOrDefault(ownerId)
                        : null,
                    Impact = risk.Impact,
                    Status = risk.Status,
                    DueOn = risk.DueOn,
                    IsOverdue = risk.Status != RiskStatus.Closed
                        && risk.DueOn is DateOnly due
                        && due < today,
                    RelatedTaskIds = [.. risk.RelatedTaskIds],
                    RelatedMilestoneIds = [.. risk.RelatedMilestoneIds],
                    CreatedAt = risk.CreatedAt,
                }),
        ];
    }
}
