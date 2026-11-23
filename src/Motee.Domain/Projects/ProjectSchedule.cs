namespace Motee.Domain.Projects;

// A dependency that overruns the start of the task waiting on it. The
// scheduling error a Gantt exists to expose: the arrow says "after", the dates
// say "during".
public sealed record ScheduleConflict
{
    public required Guid TaskId { get; init; }

    public required string TaskName { get; init; }

    public required Guid BlockerId { get; init; }

    public required string BlockerName { get; init; }

    // How many days the dependency overruns the dependent's start.
    public required int OverlapDays { get; init; }
}

// The dependency graph over one project's tasks.
//
// The same "available vs blocked" question the approval engine asks, over
// calendar dates rather than offsets.
public static class ProjectSchedule
{
    // Unfinished work that something else is waiting on.
    //
    // A cancelled predecessor counts as satisfied: it can never become
    // complete, so treating it as outstanding would block its dependents for
    // good. A dependency on a task that no longer exists counts as satisfied
    // for the same reason — there is nothing left to wait for. The client
    // treats an unknown id as unfinished, which strands the dependent.
    public static IReadOnlyList<ProjectTask> Blockers(
        ProjectTask task,
        IReadOnlyList<ProjectTask> tasks)
    {
        if (task.DependsOn.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, ProjectTask> byId = Index(tasks);

        return
        [
            .. task.DependsOn
                .Select(id => byId.GetValueOrDefault(id))
                .Where(blocker => blocker is not null && IsOutstanding(blocker.Status))
                .Select(blocker => blocker!),
        ];
    }

    public static bool IsBlocked(ProjectTask task, IReadOnlyList<ProjectTask> tasks) =>
        Blockers(task, tasks).Count > 0;

    // What could be worked on right now: unfinished, not dropped, and with
    // every dependency met.
    public static IReadOnlyList<ProjectTask> Available(IReadOnlyList<ProjectTask> tasks)
    {
        Dictionary<Guid, ProjectTask> byId = Index(tasks);

        return
        [
            .. tasks.Where(task =>
                IsOutstanding(task.Status) && !IsBlockedBy(task, byId)),
        ];
    }

    public static IReadOnlyList<ScheduleConflict> Conflicts(IReadOnlyList<ProjectTask> tasks)
    {
        Dictionary<Guid, ProjectTask> byId = Index(tasks);

        List<ScheduleConflict> conflicts = [];

        foreach (ProjectTask task in tasks)
        {
            foreach (Guid dependencyId in task.DependsOn)
            {
                if (byId.GetValueOrDefault(dependencyId) is not ProjectTask blocker)
                {
                    continue;
                }

                // A handover on the same day is fine: one finishes, the next
                // starts.
                int overlapDays = blocker.EndOn.DayNumber - task.StartOn.DayNumber;

                if (overlapDays > 0)
                {
                    conflicts.Add(new ScheduleConflict
                    {
                        TaskId = task.Id,
                        TaskName = task.Name,
                        BlockerId = blocker.Id,
                        BlockerName = blocker.Name,
                        OverlapDays = overlapDays,
                    });
                }
            }
        }

        return conflicts;
    }

    // The longest dependency chain through the project, weighted by how long
    // each task runs rather than by how many there are: a two-task chain of
    // ninety-day tasks is more critical than four five-day ones, and the
    // Gantt's bars are already date-driven, so criticality should agree with
    // them.
    public static IReadOnlyList<Guid> CriticalPath(IReadOnlyList<ProjectTask> tasks)
    {
        if (tasks.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, ProjectTask> byId = Index(tasks);
        Dictionary<Guid, Chain> memo = [];
        HashSet<Guid> visiting = [];

        Chain best = new([], 0);

        foreach (ProjectTask task in tasks)
        {
            Chain chain = ChainTo(task.Id, byId, memo, visiting);

            if (chain.Days > best.Days)
            {
                best = chain;
            }
        }

        return best.Path;
    }

    private static Chain ChainTo(
        Guid taskId,
        Dictionary<Guid, ProjectTask> byId,
        Dictionary<Guid, Chain> memo,
        HashSet<Guid> visiting)
    {
        if (memo.TryGetValue(taskId, out Chain? known))
        {
            return known;
        }

        // A dependency loop is a corrupt import, not a reason to recurse
        // forever.
        if (!visiting.Add(taskId))
        {
            return new Chain([], 0);
        }

        try
        {
            if (byId.GetValueOrDefault(taskId) is not ProjectTask task)
            {
                return new Chain([], 0);
            }

            Chain longest = new([], 0);

            foreach (Guid dependencyId in task.DependsOn)
            {
                Chain chain = ChainTo(dependencyId, byId, memo, visiting);

                if (chain.Days > longest.Days)
                {
                    longest = chain;
                }
            }

            // A task that starts and ends on one day still takes a day.
            int ownDays = Math.Max(1, task.EndOn.DayNumber - task.StartOn.DayNumber);

            Chain result = new([.. longest.Path, taskId], longest.Days + ownDays);

            memo[taskId] = result;

            return result;
        }
        finally
        {
            visiting.Remove(taskId);
        }
    }

    private static bool IsBlockedBy(ProjectTask task, Dictionary<Guid, ProjectTask> byId) =>
        task.DependsOn.Exists(id =>
            byId.GetValueOrDefault(id) is ProjectTask blocker
                && IsOutstanding(blocker.Status));

    private static bool IsOutstanding(ProjectTaskStatus status) =>
        status is not (ProjectTaskStatus.Completed or ProjectTaskStatus.Cancelled);

    private static Dictionary<Guid, ProjectTask> Index(IReadOnlyList<ProjectTask> tasks)
    {
        Dictionary<Guid, ProjectTask> byId = new(tasks.Count);

        foreach (ProjectTask task in tasks)
        {
            byId[task.Id] = task;
        }

        return byId;
    }

    private sealed record Chain(IReadOnlyList<Guid> Path, int Days);
}
