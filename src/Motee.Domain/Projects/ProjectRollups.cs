namespace Motee.Domain.Projects;

// One person's claim on one project, flattened across projects so
// over-commitment can be seen at all.
public sealed record AllocationLine
{
    public required Guid ProjectId { get; init; }

    public required string ProjectName { get; init; }

    public required ProjectStatus ProjectStatus { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required decimal AllocationPercent { get; init; }
}

public sealed record OverAllocationProject
{
    public required Guid ProjectId { get; init; }

    public required string ProjectName { get; init; }

    public required decimal AllocationPercent { get; init; }
}

// Somebody committed to more than one person's worth of time. The number that
// makes allocation percentages mean something rather than read as aspiration.
public sealed record OverAllocation
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public required decimal TotalPercent { get; init; }

    public required IReadOnlyList<OverAllocationProject> Projects { get; init; }
}

// Everything a project screen counts, read off the rows underneath.
//
// None of this is stored. A stored progress figure or spend total needs manual
// upkeep to stay honest, and stops agreeing with the tasks and hours it came
// from the first time one of them is corrected.
public static class ProjectRollups
{
    // Weighted by task count — every task counts once. Dropped work leaves
    // both sides of the average: it is out of scope, not partial progress
    // towards it.
    public static int Progress(IReadOnlyList<ProjectTask> tasks)
    {
        IReadOnlyList<ProjectTask> counted =
            [.. tasks.Where(task => task.Status != ProjectTaskStatus.Cancelled)];

        if (counted.Count == 0)
        {
            return 0;
        }

        return (int)Math.Round(
            counted.Sum(PercentOf) / (decimal)counted.Count,
            MidpointRounding.AwayFromZero);
    }

    public static int CriticalPathProgress(IReadOnlyList<ProjectTask> tasks)
    {
        HashSet<Guid> critical = [.. ProjectSchedule.CriticalPath(tasks)];

        return Progress([.. tasks.Where(task => critical.Contains(task.Id))]);
    }

    // Full-time equivalents: all of one person is 1.0.
    public static decimal TotalAllocation(IReadOnlyList<ProjectAllocation> allocations) =>
        allocations.Sum(allocation => allocation.AllocationPercent) / 100m;

    public static IReadOnlyList<OverAllocation> OverAllocations(
        IReadOnlyList<AllocationLine> lines)
    {
        Dictionary<Guid, List<AllocationLine>> byEmployee = [];

        foreach (AllocationLine line in lines)
        {
            // A finished or abandoned project is not consuming anybody's time.
            // One on hold still is — paused is not finished, and the team is
            // still booked to it.
            if (line.ProjectStatus is ProjectStatus.Completed or ProjectStatus.Cancelled)
            {
                continue;
            }

            if (!byEmployee.TryGetValue(line.EmployeeId, out List<AllocationLine>? held))
            {
                held = [];
                byEmployee[line.EmployeeId] = held;
            }

            held.Add(line);
        }

        return
        [
            .. byEmployee
                .Select(pair => new OverAllocation
                {
                    EmployeeId = pair.Key,
                    EmployeeName = pair.Value[0].EmployeeName,
                    TotalPercent = pair.Value.Sum(line => line.AllocationPercent),
                    Projects =
                    [
                        .. pair.Value.Select(line => new OverAllocationProject
                        {
                            ProjectId = line.ProjectId,
                            ProjectName = line.ProjectName,
                            AllocationPercent = line.AllocationPercent,
                        }),
                    ],
                })
                .Where(over => over.TotalPercent > 100m)
                .OrderByDescending(over => over.TotalPercent)
                .ThenBy(over => over.EmployeeName, StringComparer.Ordinal),
        ];
    }

    public static int TaskCountFor(IReadOnlyList<ProjectTask> tasks, Guid employeeId) =>
        tasks.Count(task => task.AssigneeEmployeeId == employeeId);

    // Approved hours only. Time awaiting approval is not a cost yet.
    public static decimal ApprovedHours(IReadOnlyList<ProjectTimeEntry> entries) =>
        entries
            .Where(entry => entry.Status == TimeEntryStatus.Approved)
            .Sum(entry => entry.Hours);

    // Approved time at each person's charge rate on this project.
    public static decimal Spend(
        IReadOnlyList<ProjectAllocation> allocations,
        IReadOnlyList<ProjectTimeEntry> entries)
    {
        Dictionary<Guid, decimal> rates = RatesOf(allocations);

        return entries
            .Where(entry => entry.Status == TimeEntryStatus.Approved)
            .Sum(entry => entry.Hours * rates.GetValueOrDefault(entry.EmployeeId));
    }

    // What the work still to do will cost: each unfinished task's remaining
    // hours at its assignee's rate.
    //
    // Nothing independently records a committed figure, so this is derived. An
    // unestimated or unassigned task contributes nothing — a known gap rather
    // than a calculation.
    public static decimal CommittedSpend(
        IReadOnlyList<ProjectTask> tasks,
        IReadOnlyList<ProjectAllocation> allocations)
    {
        Dictionary<Guid, decimal> rates = RatesOf(allocations);

        return tasks
            .Where(task => task.Status
                is not (ProjectTaskStatus.Completed or ProjectTaskStatus.Cancelled))
            .Sum(task =>
            {
                if (task.AssigneeEmployeeId is not Guid assignee)
                {
                    return 0m;
                }

                decimal remaining = (task.EstimatedHours ?? 0m)
                    * (1m - task.PercentComplete / 100m);

                return remaining * rates.GetValueOrDefault(assignee);
            });
    }

    public static decimal Forecast(decimal spend, decimal committed) => spend + committed;

    // What is left once the forecast is accounted for, not just the spend.
    // This is also the budget variance: under this model they are one number,
    // so there is one function rather than two that must be kept equal.
    public static decimal RemainingBudget(decimal? budget, decimal forecast) =>
        (budget ?? 0m) - forecast;

    // Unrounded, so a screen can pick its own precision: a real spend that
    // rounds to nought reads as nothing spent at all.
    public static decimal UtilisationPercent(decimal spend, decimal? budget) =>
        budget is decimal amount && amount != 0m ? spend / amount * 100m : 0m;

    private static decimal PercentOf(ProjectTask task) =>
        task.Status == ProjectTaskStatus.Completed ? 100m : task.PercentComplete;

    private static Dictionary<Guid, decimal> RatesOf(
        IReadOnlyList<ProjectAllocation> allocations)
    {
        Dictionary<Guid, decimal> rates = new(allocations.Count);

        foreach (ProjectAllocation allocation in allocations)
        {
            rates[allocation.EmployeeId] = allocation.HourlyRate ?? 0m;
        }

        return rates;
    }
}
