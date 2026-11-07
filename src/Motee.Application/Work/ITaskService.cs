using Motee.Application.Common;
using Motee.Domain.Work;

namespace Motee.Application.Work;

// Tasks — the assigned and personal list behind My Tasks, the HR Tasks page
// and both dashboard widgets.
//
// Every route here is about the signed-in person. There is no "list all tasks"
// and no employee id in a query, because a task list is somebody's own work:
// the only people who can see one are whoever has to do it and whoever gave it
// to them. The frontend declares no permission module for tasks at all, so the
// org chart decides who may put work on whose list rather than an access level
// nobody has configured.
public interface ITaskService
{
    // The signed-in person's own list, both tabs.
    Task<PagedResult<TaskItemDto>> MineAsync(
        TaskQuery query,
        CancellationToken cancellationToken = default);

    // What they have given out, which is what a manager needs and the client
    // has no screen for yet.
    Task<PagedResult<TaskItemDto>> AssignedByMeAsync(
        TaskQuery query,
        CancellationToken cancellationToken = default);

    // The counts both dashboard widgets show.
    Task<TaskTally> SummaryAsync(CancellationToken cancellationToken = default);

    Task<TaskItemDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TaskResult> CreateAsync(
        TaskItemRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskResult> UpdateAsync(
        Guid id,
        TaskItemRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskResult> CompleteAsync(
        Guid id,
        DateOnly? completedOn = null,
        CancellationToken cancellationToken = default);

    Task<TaskResult> ReopenAsync(Guid id, CancellationToken cancellationToken = default);

    // Only the assignee or the person who raised it. A manager deleting work
    // they assigned is ordinary; a stranger doing it is not.
    Task<TaskResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    // The whole order the client dragged the list into. Ids that are not the
    // caller's own are ignored rather than refused, so one stale id in a drag
    // does not throw the reorder away.
    Task<TaskResult> ReorderAsync(
        IReadOnlyList<Guid> orderedIds,
        CancellationToken cancellationToken = default);
}

public sealed record TaskItemDto
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public string? Notes { get; init; }

    public required Guid AssigneeEmployeeId { get; init; }

    public string? AssigneeName { get; init; }

    public Guid? CreatedByEmployeeId { get; init; }

    public string? CreatedByName { get; init; }

    public required TaskPriority Priority { get; init; }

    public string? Category { get; init; }

    public DateOnly? DueOn { get; init; }

    public DateOnly? CompletedOn { get; init; }

    // Derived from CompletedOn, so the client's boolean still works.
    public required bool Done { get; init; }

    // Derived against the company's own clock, not the hardcoded date in
    // task-utils.ts.
    public required TaskDueState DueState { get; init; }

    // Which tab this belongs on for whoever asked. The same row is Personal to
    // the person who wrote it for themselves and Assigned to their manager.
    public required TaskSource Source { get; init; }

    public string? Link { get; init; }

    public required int Sequence { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record TaskQuery : PagedQuery
{
    public bool? Done { get; init; }

    public TaskPriority? Priority { get; init; }

    public TaskSource? Source { get; init; }

    public string? Category { get; init; }

    // Overdue only, for the widget that leads with them.
    public bool? OverdueOnly { get; init; }

    public string? Search { get; init; }
}

public sealed record TaskItemRequest
{
    public required string Title { get; init; }

    public string? Notes { get; init; }

    // Omitted means the signed-in person is writing it for themselves, which
    // is the Personal tab. Naming somebody else requires that they report to
    // the caller.
    public Guid? AssigneeEmployeeId { get; init; }

    public TaskPriority Priority { get; init; } = TaskPriority.Medium;

    public string? Category { get; init; }

    public DateOnly? DueOn { get; init; }

    public string? Link { get; init; }
}

public enum TaskOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,
    Refused,

    // The caller is neither the assignee nor the author. Distinct from
    // NotFound because the id is not the secret here — somebody following a
    // stale link deserves to be told what happened.
    Forbidden,

    // The signed-in account has no employee record, so it has no list. The
    // account owner is permanently in this state.
    NoEmployeeRecord,
}

public sealed record TaskResult
{
    public required TaskOutcome Outcome { get; init; }

    public TaskItemDto? Task { get; init; }

    public TaskRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == TaskOutcome.Succeeded;

    public static TaskResult Failed(TaskOutcome outcome) => new() { Outcome = outcome };

    public static TaskResult Refused(TaskRefusal refusal) =>
        new() { Outcome = TaskOutcome.Refused, Refusal = refusal };

    public static TaskResult Ok(TaskItemDto task) =>
        new() { Outcome = TaskOutcome.Succeeded, Task = task };

    public static TaskResult Done() => new() { Outcome = TaskOutcome.Succeeded };
}
