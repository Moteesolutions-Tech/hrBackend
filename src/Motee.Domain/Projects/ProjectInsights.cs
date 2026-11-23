namespace Motee.Domain.Projects;

public enum HealthStatus
{
    Unknown,
    Green,
    Amber,
    Red,
}

public sealed record ProjectHealth
{
    public required HealthStatus Overall { get; init; }

    public required HealthStatus Schedule { get; init; }

    public required HealthStatus Budget { get; init; }

    public required HealthStatus Resources { get; init; }

    public required HealthStatus CriticalPath { get; init; }

    // Always unknown. Nothing tracks scope creep: change requests are not
    // built, so there is no baseline to compare against. Left honest rather
    // than reported green.
    public required HealthStatus Scope { get; init; }
}

// A traffic light per area of a project.
//
// Today is passed in. The client reads it from the browser, so a project is
// green or red depending on where the person looking at it is sitting — here
// it is the company's own day.
public static class ProjectHealthCheck
{
    // Where the forecast has to reach, as a share of the budget, before it is
    // worth warning about.
    public const decimal BudgetWarningShare = 0.85m;

    public static ProjectHealth Of(
        IReadOnlyList<ProjectTask> tasks,
        IReadOnlyList<ProjectMilestone> milestones,
        decimal? budget,
        decimal forecast,
        decimal worstOverAllocationPercent,
        DateOnly today)
    {
        HealthStatus schedule = ScheduleHealth(tasks, milestones, today);
        HealthStatus money = BudgetHealth(budget, forecast);
        HealthStatus resources = ResourceHealth(worstOverAllocationPercent);
        HealthStatus criticalPath = CriticalPathHealth(tasks, today);

        // Scope is left out of the overall: it is permanently unknown, so
        // counting it would leave every project in the product unable to be
        // green. An unknown budget is left out for the same reason — a project
        // with no budget is not an unhealthy project.
        HealthStatus[] known = [schedule, money, resources, criticalPath];

        HealthStatus overall = known.Contains(HealthStatus.Red)
            ? HealthStatus.Red
            : known.Contains(HealthStatus.Amber)
                ? HealthStatus.Amber
                : HealthStatus.Green;

        return new ProjectHealth
        {
            Overall = overall,
            Schedule = schedule,
            Budget = money,
            Resources = resources,
            CriticalPath = criticalPath,
            Scope = HealthStatus.Unknown,
        };
    }

    private static HealthStatus ScheduleHealth(
        IReadOnlyList<ProjectTask> tasks,
        IReadOnlyList<ProjectMilestone> milestones,
        DateOnly today)
    {
        bool missedMilestone = milestones.Any(milestone =>
            !Milestones.Reached(milestone) && milestone.TargetOn < today);

        // Dates that contradict the dependency arrows are as serious as a
        // missed date: the plan cannot be delivered as drawn.
        bool contradictoryDates = ProjectSchedule.Conflicts(tasks).Count > 0;

        if (missedMilestone || contradictoryDates)
        {
            return HealthStatus.Red;
        }

        return tasks.Any(task => IsOverdue(task, today))
            ? HealthStatus.Amber
            : HealthStatus.Green;
    }

    private static HealthStatus BudgetHealth(decimal? budget, decimal forecast)
    {
        // Unknown rather than green: no budget is not the same as on budget.
        if (budget is not decimal amount || amount == 0m)
        {
            return HealthStatus.Unknown;
        }

        if (forecast > amount)
        {
            return HealthStatus.Red;
        }

        return forecast / amount > BudgetWarningShare
            ? HealthStatus.Amber
            : HealthStatus.Green;
    }

    private static HealthStatus ResourceHealth(decimal worstOverAllocationPercent) =>
        worstOverAllocationPercent > 120m
            ? HealthStatus.Red
            : worstOverAllocationPercent > 100m
                ? HealthStatus.Amber
                : HealthStatus.Green;

    private static HealthStatus CriticalPathHealth(
        IReadOnlyList<ProjectTask> tasks,
        DateOnly today)
    {
        HashSet<Guid> critical = [.. ProjectSchedule.CriticalPath(tasks)];

        IReadOnlyList<ProjectTask> onPath =
            [.. tasks.Where(task => critical.Contains(task.Id))];

        if (onPath.Any(task => ProjectSchedule.IsBlocked(task, tasks)))
        {
            return HealthStatus.Red;
        }

        return onPath.Any(task => IsOverdue(task, today))
            ? HealthStatus.Amber
            : HealthStatus.Green;
    }

    private static bool IsOverdue(ProjectTask task, DateOnly today) =>
        task.Status is not (ProjectTaskStatus.Completed or ProjectTaskStatus.Cancelled)
            && task.EndOn < today;
}

public sealed record CriticalPathAlert
{
    // How many tasks the critical path runs through.
    public required int TaskCount { get; init; }

    public required Guid BlockerTaskId { get; init; }

    public required string BlockerTaskName { get; init; }

    public Guid? ImpactMilestoneId { get; init; }

    public string? ImpactMilestoneName { get; init; }

    public Guid? OwnerEmployeeId { get; init; }

    public required DateOnly DueOn { get; init; }
}

public static class CriticalPathAlerts
{
    // Null while the critical path moves. Something being critical is not an
    // alert; something critical being stuck is.
    public static CriticalPathAlert? Of(
        IReadOnlyList<ProjectTask> tasks,
        IReadOnlyList<ProjectMilestone> milestones)
    {
        IReadOnlyList<Guid> path = ProjectSchedule.CriticalPath(tasks);

        if (path.Count == 0)
        {
            return null;
        }

        Dictionary<Guid, ProjectTask> byId = [];

        foreach (ProjectTask task in tasks)
        {
            byId[task.Id] = task;
        }

        ProjectTask? blocker = path
            .Select(id => byId.GetValueOrDefault(id))
            .FirstOrDefault(task =>
                task is not null && ProjectSchedule.IsBlocked(task, tasks));

        if (blocker is null)
        {
            return null;
        }

        IReadOnlyList<ProjectMilestone> unmet =
        [
            .. milestones
                .Where(milestone => !Milestones.Reached(milestone))
                .OrderBy(milestone => milestone.TargetOn),
        ];

        // The milestone that actually waits on the stuck task, where one does.
        // The client reports the earliest unmet milestone whatever it depends
        // on, which names an unrelated date as the impact.
        ProjectMilestone? impact =
            unmet.FirstOrDefault(milestone => milestone.TaskIds.Contains(blocker.Id))
            ?? unmet.FirstOrDefault();

        return new CriticalPathAlert
        {
            TaskCount = path.Count,
            BlockerTaskId = blocker.Id,
            BlockerTaskName = blocker.Name,
            ImpactMilestoneId = impact?.Id,
            ImpactMilestoneName = impact?.Name,
            OwnerEmployeeId = blocker.AssigneeEmployeeId,
            DueOn = blocker.EndOn,
        };
    }
}

public enum NextActionBucket
{
    Overdue,
    Today,
    ThisWeek,
}

public enum NextActionKind
{
    Task,
    Milestone,
}

public sealed record NextAction
{
    public required NextActionBucket Bucket { get; init; }

    public required NextActionKind Kind { get; init; }

    public required Guid Id { get; init; }

    public required string Label { get; init; }

    public Guid? OwnerEmployeeId { get; init; }

    public required DateOnly DueOn { get; init; }

    public ProjectPriority? Priority { get; init; }

    // A task status or a milestone status, so one list can carry both.
    public required string Status { get; init; }
}

// What is due now, derived from unfinished tasks and unmet milestones.
//
// Nothing new is recorded: owner, due date, priority and status already live
// on those rows, and a second copy of them would be a second answer.
public static class NextActions
{
    public const int WindowDays = 7;

    public static IReadOnlyList<NextAction> Of(
        IReadOnlyList<ProjectTask> tasks,
        IReadOnlyList<ProjectMilestone> milestones,
        DateOnly today)
    {
        DateOnly horizon = today.AddDays(WindowDays);

        List<NextAction> actions = [];

        foreach (ProjectTask task in tasks)
        {
            if (task.Status is ProjectTaskStatus.Completed or ProjectTaskStatus.Cancelled)
            {
                continue;
            }

            if (BucketOf(task.EndOn, today, horizon) is not NextActionBucket bucket)
            {
                continue;
            }

            actions.Add(new NextAction
            {
                Bucket = bucket,
                Kind = NextActionKind.Task,
                Id = task.Id,
                Label = task.Name,
                OwnerEmployeeId = task.AssigneeEmployeeId,
                DueOn = task.EndOn,
                Priority = task.Priority,
                Status = task.Status.ToString(),
            });
        }

        foreach (ProjectMilestone milestone in milestones)
        {
            if (Milestones.Reached(milestone))
            {
                continue;
            }

            if (BucketOf(milestone.TargetOn, today, horizon) is not NextActionBucket bucket)
            {
                continue;
            }

            actions.Add(new NextAction
            {
                Bucket = bucket,
                Kind = NextActionKind.Milestone,
                Id = milestone.Id,
                Label = milestone.Name,
                OwnerEmployeeId = milestone.ResponsibleEmployeeId,
                DueOn = milestone.TargetOn,
                Status = milestone.Status.ToString(),
            });
        }

        return [.. actions.OrderBy(action => action.DueOn)];
    }

    private static NextActionBucket? BucketOf(DateOnly due, DateOnly today, DateOnly horizon)
    {
        if (due < today)
        {
            return NextActionBucket.Overdue;
        }

        if (due == today)
        {
            return NextActionBucket.Today;
        }

        return due <= horizon ? NextActionBucket.ThisWeek : null;
    }
}
