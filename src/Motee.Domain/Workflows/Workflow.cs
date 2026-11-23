using Motee.Domain.Common;

namespace Motee.Domain.Workflows;

public enum WorkflowTriggerMode
{
    Manual,
    Automatic,
}

// The lifecycle moment a relative schedule is anchored to.
public enum WorkflowTriggerEvent
{
    RecruitmentInitiated,
    PreboardingInitiated,
    OnboardingInitiated,
    OffboardingInitiated,
}

public enum ScheduleKind
{
    // A calendar date and time.
    Fixed,

    // A delay after a lifecycle event.
    Relative,
}

public enum ScheduleOffsetUnit
{
    Minutes,
    Hours,
    Days,
}

public enum WorkflowScopeKind
{
    All,
    Department,
}

// Who does a task: anybody holding an access level, or one named person.
public enum AssigneeKind
{
    AccessLevel,
    Employee,
}

public enum WorkflowTaskPriority
{
    Low,
    Normal,
    High,
    Critical,
}

public enum WorkflowStatus
{
    Draft,
    Active,
    Archived,
}

public enum WorkflowKind
{
    // Seeded recruitment, preboarding, onboarding and offboarding. Read-only,
    // so a company cannot break the flows the product itself drives.
    System,

    Custom,
}

// A fact about the person a run is for, which conditional work is judged
// against.
public enum WorkflowCondition
{
    RemoteWorker,
    CompanyCar,
    Contractor,
    VisaHolder,
}

// A workflow: an ordered list of work for one action.
//
// Unlike the approvals engine, each task separates the doer from an optional
// reviewer — approvals ask one person to decide, a workflow asks one person to
// do and sometimes another to check.
public class Workflow : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public WorkflowTriggerMode TriggerMode { get; set; } = WorkflowTriggerMode.Manual;

    // Null when it is started by hand. Flattened into columns rather than a
    // jsonb union: "which workflows fire on onboarding" is a question the
    // scheduler asks in SQL, and jsonb cannot be filtered there.
    public ScheduleKind? ScheduleKind { get; set; }

    public DateOnly? ScheduleOn { get; set; }

    public TimeOnly? ScheduleAt { get; set; }

    public WorkflowTriggerEvent? TriggerEvent { get; set; }

    public int? OffsetValue { get; set; }

    public ScheduleOffsetUnit? OffsetUnit { get; set; }

    // Tasks inherit this when resolving who does the work: a
    // department-scoped workflow should not page the whole company.
    public WorkflowScopeKind ScopeKind { get; set; } = WorkflowScopeKind.All;

    public Guid? ScopeDepartmentId { get; set; }

    public WorkflowKind WorkflowKind { get; set; } = WorkflowKind.Custom;

    // A new workflow is a draft, so nothing goes live the moment it is saved.
    public WorkflowStatus Status { get; set; } = WorkflowStatus.Draft;

    // Bumped only when the task list changes materially, so a live run is not
    // invalidated by somebody fixing a typo.
    public int Version { get; set; } = 1;

    // The day this version starts applying. Null means now.
    public DateOnly? EffectiveFrom { get; set; }

    public Guid? OwnerEmployeeId { get; set; }

    // Restrict to one employment type, e.g. permanent staff only.
    public string? EmploymentType { get; set; }

    public Guid? LastModifiedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One piece of work on a workflow.
public class WorkflowTask : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid WorkflowId { get; set; }

    // Position in the list, from one.
    public int Sequence { get; set; }

    public required string Title { get; set; }

    // Guidance for whoever picks it up. Not part of what the workflow does, so
    // rewording it is not a new version.
    public string? Description { get; set; }

    public AssigneeKind AssigneeKind { get; set; } = AssigneeKind.AccessLevel;

    public Guid? AssigneeAccessLevelId { get; set; }

    public Guid? AssigneeEmployeeId { get; set; }

    // Optional, and by access level only: a reviewer checks on behalf of a
    // function rather than as an individual.
    public bool HasReviewer { get; set; }

    public Guid? ReviewerAccessLevelId { get; set; }

    // Days from the run's anchor that this is due.
    public int? DueDayOffset { get; set; }

    public int? ExpectedDurationDays { get; set; }

    public WorkflowTaskPriority Priority { get; set; } = WorkflowTaskPriority.Normal;

    // Days past due before it escalates to its reviewer.
    public int? EscalateAfterDays { get; set; }

    // Work that must finish first. IT cannot activate an account before HR has
    // approved the record, and payroll should not finalise before bank details
    // are verified.
    public List<Guid> DependsOn { get; set; } = [];

    // Work sharing a group starts together rather than queueing: HR documents,
    // payroll setup and IT setup can all begin once the contract is signed.
    public string? ParallelGroup { get; set; }

    // The work only applies when this holds. Shipping equipment to a remote
    // worker, or immigration checks for a visa holder, should not sit on
    // everybody's list looking unfinished.
    public WorkflowCondition? Condition { get; set; }

    // Whether the condition applies when present or when absent — "only if a
    // visa holder" against "unless a contractor".
    //
    // The client has only the first, which makes some rules impossible to
    // state: its own seeded benefits enrolment is tagged "contractor" with a
    // comment saying contractors are not enrolled, so as written it enrols
    // exactly the people it means to exclude and nobody else. The builder needs
    // an "only if / unless" toggle to match.
    public bool ConditionHolds { get; set; } = true;
}

public enum WorkflowRefusal
{
    None,
    TitleTooShort,
    DepartmentRequired,
    NoTasks,

    ScheduleRequired,
    ScheduleDateRequired,
    TriggerEventRequired,
    OffsetRequired,

    TaskTitleRequired,
    AssigneeRequired,
    ReviewerRequired,

    // A dependency on work that comes later, or on itself: the run would
    // deadlock.
    DependencyComesLater,
    UnknownDependency,

    // Editing or deleting one of the seeded workflows.
    Seeded,

    // Starting a workflow that is not live, or not live yet.
    NotRunnable,

    UnknownWorkflow,
    UnknownTask,
    NotYours,
}

public static class WorkflowRules
{
    public const int ShortestTitle = 3;

    public static WorkflowRefusal Refusal(
        Workflow workflow,
        IReadOnlyList<WorkflowTask> tasks)
    {
        if ((workflow.Title ?? string.Empty).Trim().Length < ShortestTitle)
        {
            return WorkflowRefusal.TitleTooShort;
        }

        if (workflow.ScopeKind == WorkflowScopeKind.Department
            && workflow.ScopeDepartmentId is null)
        {
            return WorkflowRefusal.DepartmentRequired;
        }

        // A manual workflow is started by hand, so schedule fields left behind
        // by an earlier edit are not a reason to refuse it.
        if (workflow.TriggerMode == WorkflowTriggerMode.Automatic)
        {
            WorkflowRefusal schedule = ScheduleRefusal(workflow);

            if (schedule != WorkflowRefusal.None)
            {
                return schedule;
            }
        }

        if (tasks.Count == 0)
        {
            return WorkflowRefusal.NoTasks;
        }

        foreach (WorkflowTask task in tasks)
        {
            WorkflowRefusal refusal = TaskRefusal(task);

            if (refusal != WorkflowRefusal.None)
            {
                return refusal;
            }
        }

        return WorkflowRefusal.None;
    }

    public static WorkflowRefusal TaskRefusal(WorkflowTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Title))
        {
            return WorkflowRefusal.TaskTitleRequired;
        }

        bool hasAssignee = task.AssigneeKind == AssigneeKind.AccessLevel
            ? task.AssigneeAccessLevelId is not null
            : task.AssigneeEmployeeId is not null;

        if (!hasAssignee)
        {
            return WorkflowRefusal.AssigneeRequired;
        }

        // The toggle on with nobody behind it leaves work that can be handed in
        // and never signed off.
        return task.HasReviewer && task.ReviewerAccessLevelId is null
            ? WorkflowRefusal.ReviewerRequired
            : WorkflowRefusal.None;
    }

    // Dependencies arrive as positions in the submitted list, because the ids
    // are minted on save. A dependency on later work, or on itself, would
    // deadlock the run.
    public static WorkflowRefusal DependencyRefusal(
        IReadOnlyList<IReadOnlyList<int>> dependsOnPositions)
    {
        for (int position = 0; position < dependsOnPositions.Count; position++)
        {
            foreach (int dependency in dependsOnPositions[position])
            {
                if (dependency < 0 || dependency >= dependsOnPositions.Count)
                {
                    return WorkflowRefusal.UnknownDependency;
                }

                if (dependency >= position)
                {
                    return WorkflowRefusal.DependencyComesLater;
                }
            }
        }

        return WorkflowRefusal.None;
    }

    // The seeded flows drive recruitment, preboarding, onboarding and
    // offboarding in the product itself.
    public static bool CanEdit(Workflow workflow) =>
        workflow.WorkflowKind != WorkflowKind.System;

    public static bool CanDelete(Workflow workflow) =>
        workflow.WorkflowKind != WorkflowKind.System;

    // Live, and live yet. The status exists so a half-written workflow does not
    // fire, and the effective date so a version written for next quarter does
    // not fire today.
    public static bool IsRunnable(Workflow workflow, DateOnly today) =>
        workflow.Status == WorkflowStatus.Active
            && (workflow.EffectiveFrom is not DateOnly from || from <= today);

    // Whether the version should be bumped: the version tracks what a workflow
    // does, so retitling it or rewording guidance leaves a live run alone.
    public static bool DiffersMaterially(
        IReadOnlyList<WorkflowTask> before,
        IReadOnlyList<WorkflowTask> after)
    {
        if (before.Count != after.Count)
        {
            return true;
        }

        for (int index = 0; index < before.Count; index++)
        {
            if (!string.Equals(
                Signature(before[index]), Signature(after[index]), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static WorkflowRefusal ScheduleRefusal(Workflow workflow)
    {
        if (workflow.ScheduleKind is not ScheduleKind kind)
        {
            return WorkflowRefusal.ScheduleRequired;
        }

        if (kind == Workflows.ScheduleKind.Fixed)
        {
            return workflow.ScheduleOn is null
                ? WorkflowRefusal.ScheduleDateRequired
                : WorkflowRefusal.None;
        }

        if (workflow.TriggerEvent is null)
        {
            return WorkflowRefusal.TriggerEventRequired;
        }

        // Nought is legitimate: fire as the event lands.
        return workflow.OffsetValue is not int offset || offset < 0
            ? WorkflowRefusal.OffsetRequired
            : WorkflowRefusal.None;
    }

    private static string Signature(WorkflowTask task) =>
        string.Join(
            '|',
            task.Title.Trim(),
            task.AssigneeKind,
            task.AssigneeAccessLevelId,
            task.AssigneeEmployeeId,
            task.HasReviewer,
            task.ReviewerAccessLevelId,
            task.DueDayOffset,
            task.ExpectedDurationDays,
            task.Priority,
            task.EscalateAfterDays,
            task.Condition,
            task.ParallelGroup,
            task.DependsOn.Count);
}
