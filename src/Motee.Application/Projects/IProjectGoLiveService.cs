using Motee.Domain.Projects;

namespace Motee.Application.Projects;

// The go-live readiness checklist.
//
// The checks are a fixed template in the domain; only the ticks are stored. The
// list that comes back is the template joined to whatever this project has
// ticked, so a project that has never been looked at still returns all
// twenty-five checks rather than an empty list.
public interface IProjectGoLiveService
{
    Task<GoLiveChecklistDto?> GetAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<GoLiveResult> SetAsync(
        Guid projectId,
        string itemKey,
        GoLiveTickRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record GoLiveCheckDto
{
    public required string Key { get; init; }

    public required GoLiveCategory Category { get; init; }

    public required string Label { get; init; }

    public required bool IsDone { get; init; }

    public string? Notes { get; init; }

    public Guid? UpdatedByUserId { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record GoLiveChecklistDto
{
    public required Guid ProjectId { get; init; }

    public required IReadOnlyList<GoLiveCheckDto> Items { get; init; }

    public required GoLiveReadiness Readiness { get; init; }
}

public sealed record GoLiveTickRequest
{
    public required bool IsDone { get; init; }

    public string? Notes { get; init; }
}

public sealed record GoLiveResult
{
    public required ProjectOutcome Outcome { get; init; }

    public GoLiveChecklistDto? Checklist { get; init; }

    public ProjectRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == ProjectOutcome.Succeeded;

    public static GoLiveResult Failed(ProjectOutcome outcome) => new() { Outcome = outcome };

    public static GoLiveResult Refused(ProjectRefusal refusal) =>
        new() { Outcome = ProjectOutcome.Refused, Refusal = refusal };

    public static GoLiveResult Ok(GoLiveChecklistDto checklist) =>
        new() { Outcome = ProjectOutcome.Succeeded, Checklist = checklist };
}
