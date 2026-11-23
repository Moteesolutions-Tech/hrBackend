using Motee.Domain.Projects;

namespace Motee.Tests.Projects;

// Progress, resourcing and money, all read off the rows underneath rather
// than stored alongside them.
//
// The one that matters most is over-allocation: percentages are only worth
// recording if somebody is told when they add up to more than a person.
//
// Written before the roll-ups.
public class ProjectRollupTests
{
    private static readonly DateOnly Start = new(2026, 6, 15);

    private static readonly Guid Ada = Guid.NewGuid();
    private static readonly Guid Bola = Guid.NewGuid();

    private static ProjectTask Task(
        ProjectTaskStatus status = ProjectTaskStatus.NotStarted,
        int percentComplete = 0,
        int days = 10,
        Guid? assignee = null,
        decimal estimatedHours = 0m,
        params Guid[] dependsOn) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Task",
            Status = status,
            PercentComplete = percentComplete,
            StartOn = Start,
            EndOn = Start.AddDays(days),
            AssigneeEmployeeId = assignee,
            EstimatedHours = estimatedHours == 0m ? null : estimatedHours,
            DependsOn = [.. dependsOn],
        };

    private static ProjectAllocation Allocation(
        Guid employeeId,
        decimal percent = 50m,
        decimal? hourlyRate = null) => new()
        {
            EmployeeId = employeeId,
            ProjectRole = "Engineer",
            AllocationPercent = percent,
            StartOn = Start,
            HourlyRate = hourlyRate,
        };

    private static ProjectTimeEntry Entry(
        Guid employeeId,
        decimal hours,
        TimeEntryStatus status = TimeEntryStatus.Approved) => new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            WorkedOn = Start,
            Hours = hours,
            Status = status,
        };

    private static AllocationLine Line(
        Guid employeeId,
        decimal percent,
        string projectName = "HRIS Implementation",
        ProjectStatus status = ProjectStatus.Active) => new()
        {
            ProjectId = Guid.NewGuid(),
            ProjectName = projectName,
            ProjectStatus = status,
            EmployeeId = employeeId,
            EmployeeName = "Ada Okafor",
            AllocationPercent = percent,
        };

    // ── progress ────────────────────────────────────────────────────────────

    [Fact]
    public void NoTasksIsNoProgress()
    {
        Assert.Equal(0, ProjectRollups.Progress([]));
    }

    [Fact]
    public void ProgressAveragesAcrossTheTasks()
    {
        Assert.Equal(
            50,
            ProjectRollups.Progress(
                [Task(percentComplete: 20), Task(percentComplete: 80)]));
    }

    [Fact]
    public void ACompletedTaskCountsWholeWhateverItsProgressSays()
    {
        Assert.Equal(
            50,
            ProjectRollups.Progress(
                [Task(ProjectTaskStatus.Completed, percentComplete: 0), Task()]));
    }

    // Dropped work is out of scope, not partial progress towards it, so it
    // leaves both sides of the average.
    [Fact]
    public void CancelledTasksLeaveTheAverageEntirely()
    {
        Assert.Equal(
            100,
            ProjectRollups.Progress(
                [
                    Task(ProjectTaskStatus.Completed, percentComplete: 100),
                    Task(ProjectTaskStatus.Cancelled),
                ]));
    }

    [Fact]
    public void AProjectOfOnlyCancelledWorkShowsNoProgress()
    {
        Assert.Equal(0, ProjectRollups.Progress([Task(ProjectTaskStatus.Cancelled)]));
    }

    [Fact]
    public void CriticalPathProgressLooksOnlyAtTheCriticalTasks()
    {
        ProjectTask first = Task(ProjectTaskStatus.Completed, percentComplete: 100, days: 60);
        ProjectTask second = Task(percentComplete: 0, days: 60, dependsOn: first.Id);
        ProjectTask aside = Task(percentComplete: 0, days: 2);

        Assert.Equal(50, ProjectRollups.CriticalPathProgress([first, second, aside]));
    }

    [Fact]
    public void NoCriticalPathIsNoCriticalProgress()
    {
        Assert.Equal(0, ProjectRollups.CriticalPathProgress([]));
    }

    // ── resourcing ──────────────────────────────────────────────────────────

    [Fact]
    public void AllOfOnePersonIsOneFullTimeEquivalent()
    {
        Assert.Equal(1.0m, ProjectRollups.TotalAllocation([Allocation(Ada, 100m)]));
    }

    [Fact]
    public void AddsUpEverybodysShareOfTheirTime()
    {
        Assert.Equal(
            1.25m,
            ProjectRollups.TotalAllocation([Allocation(Ada, 50m), Allocation(Bola, 75m)]));
    }

    [Fact]
    public void NobodyIsOverCommittedUntilTheyPassFullTime()
    {
        Assert.Empty(ProjectRollups.OverAllocations([Line(Ada, 60m), Line(Bola, 100m)]));
    }

    [Fact]
    public void ReportsSomebodyCommittedBeyondFullTimeAndWhereItGoes()
    {
        OverAllocation over = Assert.Single(
            ProjectRollups.OverAllocations(
                [Line(Ada, 60m, "HRIS"), Line(Ada, 60m, "Payroll")]));

        Assert.Equal(Ada, over.EmployeeId);
        Assert.Equal(120m, over.TotalPercent);
        Assert.Equal(["HRIS", "Payroll"], over.Projects.Select(row => row.ProjectName));
    }

    // A finished or abandoned project is not consuming anybody's time.
    [Theory]
    [InlineData(ProjectStatus.Completed)]
    [InlineData(ProjectStatus.Cancelled)]
    public void AClosedProjectNoLongerClaimsAnyone(ProjectStatus status)
    {
        Assert.Empty(
            ProjectRollups.OverAllocations(
                [Line(Ada, 60m), Line(Ada, 60m, status: status)]));
    }

    [Fact]
    public void AProjectOnHoldStillClaimsPeople()
    {
        // Paused is not finished — the team is still booked to it.
        Assert.Single(
            ProjectRollups.OverAllocations(
                [Line(Ada, 60m), Line(Ada, 60m, status: ProjectStatus.OnHold)]));
    }

    [Fact]
    public void TheWorstCommittedPersonComesFirst()
    {
        IReadOnlyList<OverAllocation> over = ProjectRollups.OverAllocations(
            [Line(Ada, 110m), Line(Bola, 90m), Line(Bola, 90m)]);

        Assert.Equal([Bola, Ada], over.Select(row => row.EmployeeId));
    }

    [Fact]
    public void CountsOnePersonsShareOfTheTasks()
    {
        Assert.Equal(
            2,
            ProjectRollups.TaskCountFor(
                [Task(assignee: Ada), Task(assignee: Ada), Task(assignee: Bola), Task()],
                Ada));
    }

    // ── time and money ──────────────────────────────────────────────────────

    // Time awaiting approval is not a cost yet.
    [Fact]
    public void OnlyApprovedTimeCounts()
    {
        Assert.Equal(
            8m,
            ProjectRollups.ApprovedHours(
                [
                    Entry(Ada, 8m),
                    Entry(Ada, 5m, TimeEntryStatus.Submitted),
                    Entry(Ada, 3m, TimeEntryStatus.Draft),
                    Entry(Ada, 2m, TimeEntryStatus.Rejected),
                ]));
    }

    [Fact]
    public void SpendValuesApprovedTimeAtEachPersonsProjectRate()
    {
        Assert.Equal(
            1_100m,
            ProjectRollups.Spend(
                [Allocation(Ada, hourlyRate: 100m), Allocation(Bola, hourlyRate: 50m)],
                [Entry(Ada, 8m), Entry(Bola, 6m), Entry(Bola, 4m, TimeEntryStatus.Submitted)]));
    }

    [Fact]
    public void TimeFromSomebodyWithNoRateCostsNothing()
    {
        Assert.Equal(
            0m,
            ProjectRollups.Spend([Allocation(Ada)], [Entry(Ada, 8m)]));
    }

    // The remaining hours on unfinished work, at the assignee's rate. Derived
    // rather than entered: a stored figure here needs constant upkeep to stay
    // honest.
    [Fact]
    public void CommittedSpendCostsTheWorkStillToDo()
    {
        Assert.Equal(
            400m,
            ProjectRollups.CommittedSpend(
                [Task(percentComplete: 60, assignee: Ada, estimatedHours: 10m)],
                [Allocation(Ada, hourlyRate: 100m)]));
    }

    [Fact]
    public void FinishedAndDroppedWorkIsNoLongerCommitted()
    {
        Assert.Equal(
            0m,
            ProjectRollups.CommittedSpend(
                [
                    Task(ProjectTaskStatus.Completed, 100, assignee: Ada, estimatedHours: 10m),
                    Task(ProjectTaskStatus.Cancelled, assignee: Ada, estimatedHours: 10m),
                ],
                [Allocation(Ada, hourlyRate: 100m)]));
    }

    [Fact]
    public void WorkWithNoEstimateIsNotCountedAsFree()
    {
        // Nought is what an unestimated task costs in this model. Named so the
        // next person reads it as a known gap rather than a calculation.
        Assert.Equal(
            0m,
            ProjectRollups.CommittedSpend(
                [Task(assignee: Ada)], [Allocation(Ada, hourlyRate: 100m)]));
    }

    [Fact]
    public void UnassignedWorkIsNotCosted()
    {
        Assert.Equal(
            0m,
            ProjectRollups.CommittedSpend(
                [Task(estimatedHours: 10m)], [Allocation(Ada, hourlyRate: 100m)]));
    }

    [Fact]
    public void TheForecastIsWhatHasBeenSpentPlusWhatIsLeft()
    {
        Assert.Equal(1_200m, ProjectRollups.Forecast(spend: 800m, committed: 400m));
    }

    [Fact]
    public void WhatIsLeftOfTheBudgetAccountsForTheForecastNotJustTheSpend()
    {
        Assert.Equal(300m, ProjectRollups.RemainingBudget(1_500m, forecast: 1_200m));
    }

    [Fact]
    public void AProjectWithNoBudgetHasNothingLeftOfIt()
    {
        Assert.Equal(-1_200m, ProjectRollups.RemainingBudget(null, forecast: 1_200m));
    }

    [Fact]
    public void UtilisationIsSpendAgainstBudget()
    {
        Assert.Equal(40m, ProjectRollups.UtilisationPercent(400m, 1_000m));
    }

    // Unrounded, so a screen can show enough precision to be honest: a real
    // spend that rounds to nought reads as nothing spent at all.
    [Fact]
    public void UtilisationIsNotRounded()
    {
        Assert.Equal(0.4m, ProjectRollups.UtilisationPercent(4m, 1_000m));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public void WithoutABudgetThereIsNothingToUtilise(double? budget)
    {
        Assert.Equal(0m, ProjectRollups.UtilisationPercent(400m, (decimal?)budget));
    }
}
