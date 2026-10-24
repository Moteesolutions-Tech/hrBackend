using Motee.Application.Common;
using Motee.Domain.Performance;

namespace Motee.Application.Performance;

// Goals, set by the person or for them.
//
// Both halves are real: somebody writes their own development goals, and a manager
// sets objectives on them. The split is who the goal is about, not who typed it —
// so the self-service methods take no employee id and the administrative ones do.
public interface IPerformanceGoalService
{
    Task<IReadOnlyList<PerformanceGoalDto>> MineAsync(
        GoalQuery query,
        CancellationToken cancellationToken = default);

    Task<GoalResult> CreateMineAsync(
        GoalRequest request,
        CancellationToken cancellationToken = default);

    // A check-in: where it has got to, and why. The employee's own act — a manager
    // moving somebody's number without saying so is how a goal stops being theirs.
    Task<GoalResult> RecordProgressAsync(
        Guid id,
        ProgressUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<PagedResult<PerformanceGoalDto>> ListAsync(
        GoalQuery query,
        CancellationToken cancellationToken = default);

    Task<PerformanceGoalDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<GoalResult> CreateAsync(
        Guid employeeId,
        GoalRequest request,
        CancellationToken cancellationToken = default);

    Task<GoalResult> UpdateAsync(
        Guid id,
        GoalRequest request,
        CancellationToken cancellationToken = default);

    Task<GoalResult> ChangeStatusAsync(
        Guid id,
        GoalStatus status,
        CancellationToken cancellationToken = default);

    Task<GoalOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record GoalUpdateDto
{
    public required DateTimeOffset At { get; init; }

    public required int Progress { get; init; }

    public string? Note { get; init; }

    public string? ByName { get; init; }
}

public sealed record PerformanceGoalDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? DepartmentName { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required GoalCategory Category { get; init; }

    public required GoalStatus Status { get; init; }

    // Past its date and unfinished. Derived on read rather than stored, so it
    // becomes true at midnight without anybody running a job, and stops being true
    // the moment the goal is finished.
    public required bool IsOverdue { get; init; }

    public required int Progress { get; init; }

    public required DateOnly DueDate { get; init; }

    public string? GoalType { get; init; }

    // Newest first. The story of how something reached sixty per cent is the half
    // worth reading at review time.
    public required IReadOnlyList<GoalUpdateDto> Updates { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record GoalRequest
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public required GoalCategory Category { get; init; }

    public required DateOnly DueDate { get; init; }

    public string? GoalType { get; init; }
}

public sealed record ProgressUpdateRequest
{
    public required int Progress { get; init; }

    public string? Note { get; init; }
}

public sealed record GoalQuery : PagedQuery
{
    public Guid? EmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }

    public GoalStatus? Status { get; init; }

    public GoalCategory? Category { get; init; }

    // Past their date and unfinished. Its own filter because the status column
    // cannot answer it — the screen's "Overdue" tab is a date comparison.
    public bool? OverdueOnly { get; init; }
}

public enum GoalOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,

    // Outside nought to a hundred. A goal at 140 per cent is a typo, and storing it
    // puts a broken bar on a screen.
    ProgressOutOfRange,

    // Finished or abandoned. A goal that comes back is a new goal — reopening would
    // leave CompletedAt describing a completion that no longer happened.
    AlreadyFinal,

    TitleRequired,
}

public sealed record GoalResult
{
    public required GoalOutcome Outcome { get; init; }

    public PerformanceGoalDto? Goal { get; init; }

    public bool Succeeded => Outcome == GoalOutcome.Succeeded;

    public static GoalResult Failed(GoalOutcome outcome) => new() { Outcome = outcome };

    public static GoalResult Ok(PerformanceGoalDto goal) =>
        new() { Outcome = GoalOutcome.Succeeded, Goal = goal };
}
