using Motee.Domain.Projects;

namespace Motee.Application.Projects;

// The risk and issue register.
//
// An HRIS rollout touches employee data, payroll, access and documents, so this
// carries more weight here than on a generic project.
public interface IProjectRiskService
{
    Task<IReadOnlyList<ProjectRiskDto>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<RiskTally> TallyAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<ProjectRiskResult> AddAsync(
        Guid projectId,
        RiskRequest request,
        CancellationToken cancellationToken = default);

    Task<ProjectRiskResult> UpdateAsync(
        Guid projectId,
        Guid riskId,
        RiskRequest request,
        CancellationToken cancellationToken = default);

    Task<ProjectRiskResult> DeleteAsync(
        Guid projectId,
        Guid riskId,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectRiskDto
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required RiskKind RiskKind { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public Guid? OwnerEmployeeId { get; init; }

    public string? OwnerName { get; init; }

    public required RiskImpact Impact { get; init; }

    public required RiskStatus Status { get; init; }

    public DateOnly? DueOn { get; init; }

    public required bool IsOverdue { get; init; }

    // The clickable trail back to what this threatens.
    public required IReadOnlyList<Guid> RelatedTaskIds { get; init; }

    public required IReadOnlyList<Guid> RelatedMilestoneIds { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record RiskTally
{
    public required int Total { get; init; }

    public required int Open { get; init; }

    public required int Issues { get; init; }

    public required int HighImpactOpen { get; init; }
}

public sealed record ProjectRiskResult
{
    public required ProjectOutcome Outcome { get; init; }

    public ProjectRiskDto? Risk { get; init; }

    public ProjectRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == ProjectOutcome.Succeeded;

    public static ProjectRiskResult Failed(ProjectOutcome outcome) => new() { Outcome = outcome };

    public static ProjectRiskResult Refused(ProjectRefusal refusal) =>
        new() { Outcome = ProjectOutcome.Refused, Refusal = refusal };

    public static ProjectRiskResult Ok(ProjectRiskDto risk) =>
        new() { Outcome = ProjectOutcome.Succeeded, Risk = risk };

    public static ProjectRiskResult Done() => new() { Outcome = ProjectOutcome.Succeeded };
}

public sealed record RiskRequest
{
    public RiskKind RiskKind { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public Guid? OwnerEmployeeId { get; init; }

    public RiskImpact Impact { get; init; }

    public RiskStatus Status { get; init; }

    public DateOnly? DueOn { get; init; }

    public IReadOnlyList<Guid>? RelatedTaskIds { get; init; }

    public IReadOnlyList<Guid>? RelatedMilestoneIds { get; init; }
}
