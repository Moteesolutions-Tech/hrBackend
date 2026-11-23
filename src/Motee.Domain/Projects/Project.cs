using Motee.Domain.Common;

namespace Motee.Domain.Projects;

public enum ProjectStatus
{
    Planning,
    Active,
    OnHold,
    Completed,
    Cancelled,
}

public enum ProjectTaskStatus
{
    NotStarted,
    InProgress,
    AtRisk,
    Blocked,
    Completed,
    Cancelled,
}

public enum ProjectPriority
{
    Low,
    Medium,
    High,
    Critical,
}

public enum MilestoneStatus
{
    NotStarted,
    InProgress,
    AtRisk,
    Completed,
}

public enum TimeEntryStatus
{
    Draft,
    Submitted,
    Approved,
    Rejected,
}

public enum RiskKind
{
    Risk,
    Issue,
}

public enum RiskImpact
{
    Low,
    Medium,
    High,
}

public enum RiskStatus
{
    Open,
    Monitoring,
    Closed,
}

// The client's own category list for an HRIS rollout.
public enum ProjectDocumentCategory
{
    Charter,
    BusinessRequirements,
    ProcessMaps,
    DataMigrationPlan,
    PayrollIntegrationSpec,
    UatPlan,
    TrainingPlan,
    GoLiveChecklist,
    RiskRegister,
    StatusReport,
    VendorContract,

    // A place to file one. Change requests as a feature are not built, and
    // nothing here tracks scope.
    ChangeRequest,

    Other,
}

// A project.
//
// Almost everything a project screen shows is derived: progress, the critical
// path, who is over-committed, spend, forecast and health all come off the
// tasks, allocations and logged hours underneath. Stored here is only what
// somebody typed.
public class Project : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // How the project is referenced everywhere else. Upper-cased, and unique
    // within the company.
    public required string Code { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Planning;

    public ProjectPriority Priority { get; set; } = ProjectPriority.Medium;

    // An external client, or the internal department that asked for the work.
    public string? Client { get; set; }

    // A real person rather than a typed name: the owner appears on the list
    // screen, in the health panel and in exports, and a name typed three times
    // is three different owners. The client's form currently types it — it
    // needs to become a picker.
    public Guid? OwnerEmployeeId { get; set; }

    public Guid? DepartmentId { get; set; }

    public DateOnly StartOn { get; set; }

    public DateOnly EndOn { get; set; }

    public decimal? Budget { get; set; }

    // A code rather than a link: projects charge to a cost centre Finance
    // already books against, and nothing in the product owns that list yet.
    // Matches how workforce requests carry it.
    public string? CostCentreCode { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One piece of work on a project.
public class ProjectTask : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public ProjectTaskStatus Status { get; set; } = ProjectTaskStatus.NotStarted;

    public ProjectPriority? Priority { get; set; }

    public DateOnly StartOn { get; set; }

    public DateOnly EndOn { get; set; }

    // 0–100, and tied to Status: completed work is whole and whole work is
    // completed. See ProjectRules.
    public int PercentComplete { get; set; }

    public Guid? AssigneeEmployeeId { get; set; }

    // Tasks that must finish before this one can start. jsonb: a project's
    // tasks are read whole for every screen that needs them, and the graph
    // walk happens in memory over that set, so a join table would buy a
    // second query and nothing else.
    public List<Guid> DependsOn { get; set; } = [];

    public decimal? EstimatedHours { get; set; }

    // The band it sits in on the Gantt, e.g. "Discovery".
    public string? Phase { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// A date the project is measured against.
//
// Reached is not a column. A milestone is reached when it is complete, and
// storing both invites the pair that cannot be read: "completed" but not
// reached. See Milestones.
public class ProjectMilestone : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public DateOnly TargetOn { get; set; }

    // When it was actually met, where that differs from the target.
    public DateOnly? ActualOn { get; set; }

    public MilestoneStatus Status { get; set; } = MilestoneStatus.NotStarted;

    public int PercentComplete { get; set; }

    public Guid? ResponsibleEmployeeId { get; set; }

    // What has to be done for this to be met, and what threatens it. jsonb for
    // the same reason as a task's dependencies.
    public List<Guid> TaskIds { get; set; } = [];

    public List<Guid> RiskIds { get; set; } = [];

    // Approval is whether somebody signed it, so there is no separate flag to
    // fall out of step with the signature.
    public Guid? ApprovedByUserId { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// How much of one person's time a project has claimed. One row per person per
// project.
public class ProjectAllocation : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid EmployeeId { get; set; }

    // Their role on this project, which is not their job title.
    public required string ProjectRole { get; set; }

    // Percentage of their working time, 1–100.
    public decimal AllocationPercent { get; set; }

    public DateOnly StartOn { get; set; }

    public DateOnly? EndOn { get; set; }

    // The internal charge rate used to cost their time on this project.
    public decimal? HourlyRate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// Hours logged against a project.
//
// Not a Timesheet: that is a week of attendance, derived from clock-ins and
// approved as a whole. This is somebody saying which project their hours went
// to, which is a different question with a different answer. Only approved
// time counts as spend.
public class ProjectTimeEntry : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    // Null for general project work.
    public Guid? TaskId { get; set; }

    public Guid EmployeeId { get; set; }

    public DateOnly WorkedOn { get; set; }

    public decimal Hours { get; set; }

    public string? Notes { get; set; }

    public TimeEntryStatus Status { get; set; } = TimeEntryStatus.Draft;

    public Guid? ResolvedByUserId { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// A risk or an issue on the register.
//
// An HRIS rollout touches employee data, payroll, access and documents, so
// this register carries more weight here than on a generic project — the
// client named it the single biggest gap.
public class ProjectRisk : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    // A risk might happen; an issue already has.
    public RiskKind RiskKind { get; set; } = RiskKind.Risk;

    public required string Title { get; set; }

    public string? Description { get; set; }

    public Guid? OwnerEmployeeId { get; set; }

    public RiskImpact Impact { get; set; } = RiskImpact.Medium;

    public RiskStatus Status { get; set; } = RiskStatus.Open;

    public DateOnly? DueOn { get; set; }

    public List<Guid> RelatedTaskIds { get; set; } = [];

    public List<Guid> RelatedMilestoneIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// A file attached to a project under one of the rollout categories.
//
// Deliberately not an HrDocument: that model's folders, sharing, versioning
// and acknowledgement tracking are for a company-wide compliance library, not
// for hanging a charter off one project. The bytes still go through the one
// file store, so there is only one place files live.
public class ProjectDocument : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public required string Name { get; set; }

    public ProjectDocumentCategory Category { get; set; } = ProjectDocumentCategory.Other;

    public string? Description { get; set; }

    // The bytes go through the one upload path, which already holds the file
    // name, type, size and storage key. Copying them here would be a second
    // set of facts about one file.
    public Guid StoredFileId { get; set; }

    public Guid? UploadedByUserId { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}

// One tick on a project's go-live checklist.
//
// The checks themselves are a fixed template in code (GoLiveTemplate), so only
// the ticks are rows. Copying twenty-five checks into every project would mean
// no check could ever be corrected centrally.
public class GoLiveCompletion : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ProjectId { get; set; }

    public required string ItemKey { get; set; }

    public bool IsDone { get; set; }

    public string? Notes { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ProjectRefusal
{
    None,
    NameTooShort,
    CodeRequired,
    CodeTaken,
    EndBeforeStart,

    TaskNameTooShort,
    TaskEndBeforeStart,
    PercentOutOfRange,

    // Completion and progress disagree.
    CompletedMustBeWhole,
    WholeMustBeCompleted,

    // Starting or finishing work that is still waiting on something else.
    Blocked,

    MilestoneNameTooShort,

    AllocationOutOfRange,

    NotAllocated,
    HoursNotPositive,
    HoursBeyondADay,
    DayBeyondADay,

    RiskTitleTooShort,

    UnknownTask,
    UnknownCheck,
}

public static class ProjectRules
{
    public const int ShortestName = 3;

    public const int ShortestTaskName = 2;

    public const decimal MostHoursInADay = 24m;

    // Upper-cased so "prj-001" and "PRJ-001" are one project, not two.
    public static string NormaliseCode(string? code) =>
        (code ?? string.Empty).Trim().ToUpperInvariant();

    public static ProjectRefusal Refusal(Project project)
    {
        if ((project.Name ?? string.Empty).Trim().Length < ShortestName)
        {
            return ProjectRefusal.NameTooShort;
        }

        if (NormaliseCode(project.Code).Length == 0)
        {
            return ProjectRefusal.CodeRequired;
        }

        return project.EndOn < project.StartOn
            ? ProjectRefusal.EndBeforeStart
            : ProjectRefusal.None;
    }

    public static ProjectRefusal TaskRefusal(ProjectTask task)
    {
        if ((task.Name ?? string.Empty).Trim().Length < ShortestTaskName)
        {
            return ProjectRefusal.TaskNameTooShort;
        }

        if (task.EndOn < task.StartOn)
        {
            return ProjectRefusal.TaskEndBeforeStart;
        }

        if (task.PercentComplete is < 0 or > 100)
        {
            return ProjectRefusal.PercentOutOfRange;
        }

        // Dropped work is out of scope rather than finished, so it may stand
        // at whatever progress it had reached when it was dropped.
        if (task.Status == ProjectTaskStatus.Cancelled)
        {
            return ProjectRefusal.None;
        }

        if (task.Status == ProjectTaskStatus.Completed && task.PercentComplete != 100)
        {
            return ProjectRefusal.CompletedMustBeWhole;
        }

        return task.PercentComplete == 100 && task.Status != ProjectTaskStatus.Completed
            ? ProjectRefusal.WholeMustBeCompleted
            : ProjectRefusal.None;
    }

    // The status moved, so progress follows it.
    public static void Moved(ProjectTask task, ProjectTaskStatus status)
    {
        ProjectTaskStatus was = task.Status;

        task.Status = status;

        if (status == ProjectTaskStatus.Completed)
        {
            task.PercentComplete = 100;
            return;
        }

        // Reopened. How much is left is unknown, so it reads as nearly done
        // rather than inventing a figure — but not as whole, which is the pair
        // this exists to prevent. Cancelled keeps its progress: it is out of
        // scope, not unfinished.
        if (was == ProjectTaskStatus.Completed && status != ProjectTaskStatus.Cancelled)
        {
            task.PercentComplete = Math.Min(task.PercentComplete, 99);
        }
    }

    // Progress moved, so the status follows it.
    public static void Progressed(ProjectTask task, int percent)
    {
        task.PercentComplete = Math.Clamp(percent, 0, 100);

        // Dropped work does not come back because somebody edited a number.
        if (task.Status == ProjectTaskStatus.Cancelled)
        {
            return;
        }

        if (task.PercentComplete == 100)
        {
            task.Status = ProjectTaskStatus.Completed;
            return;
        }

        if (task.Status == ProjectTaskStatus.Completed)
        {
            task.Status = ProjectTaskStatus.InProgress;
            return;
        }

        if (task.PercentComplete > 0 && task.Status == ProjectTaskStatus.NotStarted)
        {
            task.Status = ProjectTaskStatus.InProgress;
        }
    }

    public static ProjectRefusal MilestoneRefusal(ProjectMilestone milestone)
    {
        if ((milestone.Name ?? string.Empty).Trim().Length < ShortestTaskName)
        {
            return ProjectRefusal.MilestoneNameTooShort;
        }

        if (milestone.PercentComplete is < 0 or > 100)
        {
            return ProjectRefusal.PercentOutOfRange;
        }

        return milestone.Status == MilestoneStatus.Completed
            && milestone.PercentComplete != 100
            ? ProjectRefusal.CompletedMustBeWhole
            : ProjectRefusal.None;
    }

    public static ProjectRefusal AllocationRefusal(ProjectAllocation allocation)
    {
        if (allocation.AllocationPercent is <= 0m or > 100m)
        {
            return ProjectRefusal.AllocationOutOfRange;
        }

        return allocation.EndOn is DateOnly endOn && endOn < allocation.StartOn
            ? ProjectRefusal.EndBeforeStart
            : ProjectRefusal.None;
    }

    // otherHoursThatDay is everything else the same person already has logged
    // on that date, across every project. The client guards one entry at a
    // time, so twenty hours on each of three projects passes it — the day is
    // the limit, not the entry.
    public static ProjectRefusal TimeRefusal(
        ProjectTimeEntry entry,
        bool allocated,
        decimal otherHoursThatDay)
    {
        if (!allocated)
        {
            return ProjectRefusal.NotAllocated;
        }

        if (entry.Hours <= 0m)
        {
            return ProjectRefusal.HoursNotPositive;
        }

        if (entry.Hours > MostHoursInADay)
        {
            return ProjectRefusal.HoursBeyondADay;
        }

        return entry.Hours + otherHoursThatDay > MostHoursInADay
            ? ProjectRefusal.DayBeyondADay
            : ProjectRefusal.None;
    }

    public static ProjectRefusal RiskRefusal(ProjectRisk risk) =>
        (risk.Title ?? string.Empty).Trim().Length < ShortestTaskName
            ? ProjectRefusal.RiskTitleTooShort
            : ProjectRefusal.None;

    // A task cannot start or finish while something it waits on is unfinished.
    // Without this the dependency arrows on the Gantt are decorative.
    public static ProjectRefusal StatusRefusal(
        ProjectTask task,
        ProjectTaskStatus next,
        IReadOnlyList<ProjectTask> tasks) =>
        next is ProjectTaskStatus.InProgress or ProjectTaskStatus.Completed
            && ProjectSchedule.IsBlocked(task, tasks)
            ? ProjectRefusal.Blocked
            : ProjectRefusal.None;
}

public static class Milestones
{
    // Derived, never stored.
    public static bool Reached(ProjectMilestone milestone) =>
        milestone.Status == MilestoneStatus.Completed;

    public static bool IsApproved(ProjectMilestone milestone) =>
        milestone.ApprovedAt is not null;

    public static void Toggle(ProjectMilestone milestone)
    {
        if (!Reached(milestone))
        {
            milestone.Status = MilestoneStatus.Completed;
            milestone.PercentComplete = 100;
            return;
        }

        // Un-reaching says the work is not finished after all. It cannot say
        // how much is left, so it reads as in progress rather than untouched.
        milestone.PercentComplete = Math.Min(milestone.PercentComplete, 99);
        milestone.Status = milestone.PercentComplete > 0
            ? MilestoneStatus.InProgress
            : MilestoneStatus.NotStarted;
    }
}
