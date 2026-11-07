using Motee.Domain.Common;

namespace Motee.Domain.Checklists;

// Who does the task. The form's own select offers only four of these; the
// type, the labels and the eight tasks the client shipped all carry Finance as
// well — "submit bank details to Finance" is one of them — so all five stay
// and the form is the thing that is short.
public enum ResponsibleParty
{
    Hr,
    Manager,
    It,
    Employee,
    Finance,
}

public enum ChecklistTaskState
{
    Done,

    // Not done, and its due date has passed. Needs a date to be late against,
    // so a starter with no start date recorded never lands here.
    Overdue,

    Outstanding,
}

public enum ChecklistProgressState
{
    NotStarted,
    InProgress,
    Completed,

    // Any required task missed. Beats InProgress however much else is done,
    // because a missed required task is what the screen exists to surface.
    Overdue,
}

// One task on the company's onboarding template.
//
// When it falls due is a single signed offset from the starter's first day:
// negative is before they arrive, zero is day one, positive is after. The
// frontend carries two incompatible vocabularies for this — the type declares
// on_start | same_day | day_1 … day_90 while the form that writes it offers
// before_start | on_start | after_start plus a separate number — and both are
// reaching for exactly this. Two fields would also let "day_30" be saved with
// an offset of 3, which is a row nothing can read sensibly.
public class ChecklistItem : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The form collects one name; the frontend type carries both `title` and
    // `taskName` and sets them to the same value everywhere. This is that one
    // value.
    public required string Title { get; set; }

    public string? Description { get; set; }

    // Free text rather than an enum: the form has no category field at all,
    // while the shipped template uses seven of them, so a fixed list would be
    // wrong the first time somebody added a task.
    public string? Category { get; set; }

    public ResponsibleParty ResponsibleParty { get; set; }

    // Days from the starter's first day. Signed — see the class comment.
    public int DueDayOffset { get; set; }

    public bool IsRequired { get; set; } = true;

    // Retired rather than deleted once anybody has completed it, so the
    // history of what was asked keeps reading correctly.
    public bool IsActive { get; set; } = true;

    public int Sequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One task, done.
//
// Only completions are stored. There is no row for a task somebody has not
// done yet: a starter's checklist is the active template resolved against
// their start date, and the absence of a completion is the outstanding state.
// Materialising a row per task per hire would mean a company editing its
// template had to decide what to do with hundreds of rows nobody had touched.
public class ChecklistCompletion : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid ChecklistItemId { get; set; }

    public DateOnly CompletedOn { get; set; }

    // Who ticked it. Null for a completion recorded by a job rather than a
    // person.
    public Guid? CompletedByUserId { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum ChecklistRefusal
{
    None,
    TitleRequired,
    TitleTooLong,
    DescriptionTooLong,
    OffsetOutOfRange,

    // Deleting a task somebody has already completed. Retired instead.
    InUse,
}

// Enough of a resolved task to work out a starter's progress, without the
// caller needing the item or the employee.
public sealed record ChecklistTaskFacts
{
    public required ChecklistTaskState State { get; init; }

    public required bool IsRequired { get; init; }
}

public sealed record ChecklistProgress
{
    public required ChecklistProgressState State { get; init; }

    // Over every active task, which is what the progress bar is drawn from.
    public required int Completed { get; init; }

    public required int Total { get; init; }

    // The figure the badge is decided on. Reported alongside the counts above
    // so a checklist that is finished on the required tasks but has an
    // optional check-in outstanding does not need reconciling by guesswork.
    public required int RequiredOutstanding { get; init; }

    public required int Overdue { get; init; }
}

public static class ChecklistRules
{
    // The form's own limit.
    public const int ShortestTitle = 2;

    public const int LongestTitle = 100;

    public const int LongestDescription = 500;

    // A year after, three months before. Wider than any real onboarding and
    // narrow enough to catch a figure that was meant to be something else.
    public const int EarliestOffset = -90;

    public const int LatestOffset = 365;

    public static ChecklistRefusal Refusal(ChecklistItem item)
    {
        string title = item.Title?.Trim() ?? string.Empty;

        if (title.Length < ShortestTitle)
        {
            return ChecklistRefusal.TitleRequired;
        }

        if (title.Length > LongestTitle)
        {
            return ChecklistRefusal.TitleTooLong;
        }

        if ((item.Description?.Trim().Length ?? 0) > LongestDescription)
        {
            return ChecklistRefusal.DescriptionTooLong;
        }

        return item.DueDayOffset < EarliestOffset || item.DueDayOffset > LatestOffset
            ? ChecklistRefusal.OffsetOutOfRange
            : ChecklistRefusal.None;
    }

    // Null where the starter has no start date recorded. Reporting a date
    // there would invent one, and reporting the task as late would blame
    // somebody for a deadline nobody set.
    public static DateOnly? DueOn(ChecklistItem item, DateOnly? startDate) =>
        startDate is DateOnly start ? start.AddDays(item.DueDayOffset) : null;

    public static ChecklistTaskState StateOf(
        ChecklistItem item,
        DateOnly? startDate,
        DateOnly? completedOn,
        DateOnly asAt)
    {
        // Lateness is about the deadline, not about when it was eventually
        // done — a task finished three weeks late is still done.
        if (completedOn is not null)
        {
            return ChecklistTaskState.Done;
        }

        return DueOn(item, startDate) is DateOnly dueOn && dueOn < asAt
            ? ChecklistTaskState.Overdue
            : ChecklistTaskState.Outstanding;
    }

    public static ChecklistProgress Progress(IReadOnlyList<ChecklistTaskFacts> tasks)
    {
        int completed = tasks.Count(task => task.State == ChecklistTaskState.Done);

        int overdue = tasks.Count(task => task.State == ChecklistTaskState.Overdue);

        int requiredOutstanding = tasks.Count(task =>
            task.IsRequired && task.State != ChecklistTaskState.Done);

        bool anyRequiredOverdue = tasks.Any(task =>
            task.IsRequired && task.State == ChecklistTaskState.Overdue);

        return new ChecklistProgress
        {
            Completed = completed,
            Total = tasks.Count,
            RequiredOutstanding = requiredOutstanding,
            Overdue = overdue,

            // An optional check-in nobody booked is not a compliance problem,
            // so only a required task overdue makes the checklist overdue. And
            // an empty template is finished rather than permanently unstarted.
            State = anyRequiredOverdue
                ? ChecklistProgressState.Overdue
                : requiredOutstanding == 0
                    ? ChecklistProgressState.Completed
                    : completed == 0
                        ? ChecklistProgressState.NotStarted
                        : ChecklistProgressState.InProgress,
        };
    }
}

public sealed record ChecklistSeed
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string Category { get; init; }

    public required ResponsibleParty ResponsibleParty { get; init; }

    public required int DueDayOffset { get; init; }

    public required bool IsRequired { get; init; }
}

// The onboarding template a new tenant starts with — the eight tasks the
// client shipped, in their order.
//
// Seeded live rather than as drafts, unlike the benefits catalogue: these are
// process steps every employer does, and none of them asserts a contract, a
// provider or a figure that could be wrong. The worst case is a company
// retiring a task it does differently, which is one click.
public static class ChecklistSeeds
{
    public static readonly IReadOnlyList<ChecklistSeed> All =
    [
        new()
        {
            Title = "Complete employment paperwork",
            Description = "Signed offer letter, contract and any confidentiality agreement.",
            Category = "Documentation",
            ResponsibleParty = ResponsibleParty.Hr,

            // Before they arrive: a contract signed on day one is a contract
            // signed late.
            DueDayOffset = -7,
            IsRequired = true,
        },
        new()
        {
            Title = "Set up workstation and accounts",
            Description = "Laptop, email and access to the systems the role needs.",
            Category = "IT Setup",
            ResponsibleParty = ResponsibleParty.It,
            DueDayOffset = 0,
            IsRequired = true,
        },
        new()
        {
            Title = "HR orientation session",
            Description = "Policies, benefits and how things work here.",
            Category = "Orientation",
            ResponsibleParty = ResponsibleParty.Hr,
            DueDayOffset = 3,
            IsRequired = true,
        },
        new()
        {
            Title = "Meet with line manager",
            Description = "A first one-to-one covering the role and what is expected.",
            Category = "Onboarding",
            ResponsibleParty = ResponsibleParty.Manager,
            DueDayOffset = 3,
            IsRequired = true,
        },
        new()
        {
            Title = "Complete compliance training",
            Description = "The mandatory courses for the role, including data protection.",
            Category = "Compliance",
            ResponsibleParty = ResponsibleParty.Employee,
            DueDayOffset = 7,
            IsRequired = true,
        },
        new()
        {
            Title = "Set up payroll details",
            Description = "Bank details and tax identification passed to payroll.",
            Category = "Finance",
            ResponsibleParty = ResponsibleParty.Finance,
            DueDayOffset = 7,
            IsRequired = true,
        },
        new()
        {
            Title = "30-day check-in with manager",
            Description = "How the first month has gone, and anything that needs fixing.",
            Category = "Onboarding",
            ResponsibleParty = ResponsibleParty.Manager,
            DueDayOffset = 30,

            // Optional, as the client has it. A check-in nobody booked should
            // not read as a compliance failure.
            IsRequired = false,
        },
        new()
        {
            Title = "Complete probation review form",
            Description = "The probation self-assessment, ahead of the review.",
            Category = "Performance",
            ResponsibleParty = ResponsibleParty.Employee,
            DueDayOffset = 90,
            IsRequired = true,
        },
    ];
}
