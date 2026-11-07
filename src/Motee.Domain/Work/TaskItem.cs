using Motee.Domain.Common;

namespace Motee.Domain.Work;

public enum TaskPriority
{
    High,
    Medium,
    Low,
}

// Which tab a task belongs on, from the point of view of whoever is looking.
// The same row is Personal to the person who wrote it for themselves and
// Assigned to everybody else who can see it.
public enum TaskSource
{
    Personal,
    Assigned,
}

public enum TaskDueState
{
    NotDue,

    // Three days or fewer left, the day itself included — the client's own
    // rule, in task-utils.ts.
    DueSoon,

    Overdue,
}

// A task on somebody's list.
//
// Named TaskItem rather than Task on purpose: an entity called Task in a
// codebase where every other line returns one would make every signature a
// guessing game.
//
// Deliberately not a module in the permission catalogue, because the frontend
// does not make it one — there is no organization.tasks anywhere in
// permissions/modules.ts. Who may put work on whose list is decided by the org
// chart instead: see TaskItems.CanAssign.
public class TaskItem : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The client calls this `label` in one place and `title` in another, and
    // the form's own label for the field is "Task description". One value.
    public required string Title { get; set; }

    // The employee form calls this "Notes" and the HR form calls it
    // "Description". Same field.
    public string? Notes { get; set; }

    // Whose list it is on.
    public Guid AssigneeEmployeeId { get; set; }

    // Who put it there. Equal to the assignee for a task somebody wrote for
    // themselves, which is what makes the Personal tab a derivation rather
    // than a flag somebody has to remember to set. Null for a task raised by a
    // background job rather than a person.
    public Guid? CreatedByEmployeeId { get; set; }

    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    // Training, Performance, HR, Personal are the form's presets. Free text
    // underneath, because the assigned tasks arrive carrying whatever the
    // thing they came from was called.
    public string? Category { get; set; }

    // Null is ordinary: the HR form offers "No due date" explicitly, and a
    // task without a deadline is a normal thing to write down.
    public DateOnly? DueOn { get; set; }

    // A date rather than the client's boolean. It answers the same question
    // and also answers "how much did this person clear this week", which the
    // dashboard asks and a boolean cannot.
    public DateOnly? CompletedOn { get; set; }

    // Where the task points — the leave queue, a training course, an
    // onboarding record.
    public string? Link { get; set; }

    // The order the owner dragged their list into.
    public int Sequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum TaskRefusal
{
    None,
    TitleRequired,
    TitleTooLong,
    NotesTooLong,
    AssigneeRequired,

    // Putting work on the list of somebody who does not report to you.
    NotYoursToAssign,
}

public sealed record TaskFacts
{
    public required bool IsDone { get; init; }

    public required TaskDueState DueState { get; init; }

    public required TaskSource Source { get; init; }
}

public sealed record TaskTally
{
    public required int Total { get; init; }

    public required int Done { get; init; }

    public required int Outstanding { get; init; }

    public required int Overdue { get; init; }

    public required int DueSoon { get; init; }

    public required int Assigned { get; init; }

    public required int Personal { get; init; }
}

public static class TaskItems
{
    public const int ShortestTitle = 2;

    public const int LongestTitle = 200;

    public const int LongestNotes = 2000;

    // The client's threshold for "soon", in task-utils.ts.
    public const int DueSoonDays = 3;

    public static TaskRefusal Refusal(TaskItem item)
    {
        string title = item.Title?.Trim() ?? string.Empty;

        if (title.Length < ShortestTitle)
        {
            return TaskRefusal.TitleRequired;
        }

        if (title.Length > LongestTitle)
        {
            return TaskRefusal.TitleTooLong;
        }

        if ((item.Notes?.Trim().Length ?? 0) > LongestNotes)
        {
            return TaskRefusal.NotesTooLong;
        }

        return item.AssigneeEmployeeId == Guid.Empty
            ? TaskRefusal.AssigneeRequired
            : TaskRefusal.None;
    }

    public static bool IsDone(TaskItem item) => item.CompletedOn is not null;

    // Personal only to the person who wrote it for themselves. The same row is
    // Assigned to the manager who raised it, because from their side it is
    // work they gave out rather than work of theirs to do.
    public static TaskSource SourceOf(TaskItem item, Guid viewerEmployeeId) =>
        item.CreatedByEmployeeId == viewerEmployeeId
            && item.AssigneeEmployeeId == viewerEmployeeId
            ? TaskSource.Personal
            : TaskSource.Assigned;

    public static TaskDueState DueStateOf(TaskItem item, DateOnly asAt)
    {
        // However late it was, it is finished. Keeping it red forever would
        // make the overdue count a history of mistakes rather than a list of
        // things to do.
        if (IsDone(item) || item.DueOn is not DateOnly dueOn)
        {
            return TaskDueState.NotDue;
        }

        if (dueOn < asAt)
        {
            return TaskDueState.Overdue;
        }

        return dueOn.DayNumber - asAt.DayNumber <= DueSoonDays
            ? TaskDueState.DueSoon
            : TaskDueState.NotDue;
    }

    // Writing a task for yourself is nobody else's business; putting one on
    // somebody else's list is only a manager's. Decided against the org chart
    // rather than a permission, because the frontend declares no module for
    // tasks at all — and handing work to somebody who does not report to you
    // is a request, not a task.
    public static bool CanAssign(
        Guid actorEmployeeId,
        Guid assigneeEmployeeId,
        Guid? managerOfAssignee) =>
        actorEmployeeId == assigneeEmployeeId
            || (managerOfAssignee is Guid manager && manager == actorEmployeeId);

    // A drag produces a whole new order rather than a swap, so the ordering is
    // taken as the list the client ended up with. A repeated or unknown id is
    // dropped instead of being allowed to leave two tasks on one position.
    public static IReadOnlyDictionary<Guid, int> SequenceFor(IReadOnlyList<Guid> ordered)
    {
        Dictionary<Guid, int> sequence = [];

        foreach (Guid id in ordered)
        {
            if (!sequence.ContainsKey(id))
            {
                sequence[id] = sequence.Count;
            }
        }

        return sequence;
    }
}

public static class TaskBoard
{
    public static TaskTally Of(IReadOnlyList<TaskFacts> tasks) => new()
    {
        Total = tasks.Count,
        Done = tasks.Count(task => task.IsDone),
        Outstanding = tasks.Count(task => !task.IsDone),
        Overdue = tasks.Count(task => task.DueState == TaskDueState.Overdue),
        DueSoon = tasks.Count(task => task.DueState == TaskDueState.DueSoon),
        Assigned = tasks.Count(task => task.Source == TaskSource.Assigned),
        Personal = tasks.Count(task => task.Source == TaskSource.Personal),
    };
}
