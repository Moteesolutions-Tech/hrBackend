using Motee.Application.Common;
using Motee.Domain.Checklists;

namespace Motee.Application.Checklists;

// The onboarding checklist: the company's task template, and how far each new
// starter has got through it.
//
// A starter's checklist is never materialised. It is the active template
// resolved against their start date, with completions joined on — so editing
// the template reaches everybody still onboarding, and a company that moves
// somebody's start date gets every deadline moved with it. Rows per task per
// hire would mean an edit to one task forced a decision about hundreds of rows
// nobody had touched.
public interface IChecklistService
{
    // The template. Retired tasks included by default, because the screen that
    // brings one back has to be able to see it.
    Task<IReadOnlyList<ChecklistItemDto>> ListItemsAsync(
        bool includeRetired = true,
        CancellationToken cancellationToken = default);

    Task<ChecklistResult> CreateItemAsync(
        ChecklistItemRequest request,
        CancellationToken cancellationToken = default);

    Task<ChecklistResult> UpdateItemAsync(
        Guid id,
        ChecklistItemRequest request,
        CancellationToken cancellationToken = default);

    // Up or down one place, which is what the table's arrows do. Swapping with
    // the neighbour rather than taking an absolute position, so two people
    // reordering at once cannot leave a gap.
    Task<ChecklistResult> MoveItemAsync(
        Guid id,
        bool up,
        CancellationToken cancellationToken = default);

    Task<ChecklistResult> RetireItemAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ChecklistResult> RestoreItemAsync(Guid id, CancellationToken cancellationToken = default);

    // Only a task nobody has completed. Once somebody has, it is retired
    // instead, so the record of what was asked keeps reading correctly.
    Task<ChecklistResult> DeleteItemAsync(Guid id, CancellationToken cancellationToken = default);

    // The New Hire Status tab: everybody onboarding, with their progress.
    Task<PagedResult<ChecklistHireDto>> HiresAsync(
        ChecklistHireQuery query,
        CancellationToken cancellationToken = default);

    // One starter's tasks, resolved against their start date.
    Task<EmployeeChecklistDto?> ForEmployeeAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default);

    // The signed-in employee's own. Two of the eight seeded tasks are theirs
    // to do — compliance training and the probation form — and nothing else
    // tells them so.
    Task<EmployeeChecklistDto?> MineAsync(CancellationToken cancellationToken = default);

    Task<ChecklistResult> CompleteAsync(
        Guid employeeId,
        Guid itemId,
        ChecklistCompletionRequest request,
        CancellationToken cancellationToken = default);

    // Unticks it. A task marked done by mistake has to be undoable, or the
    // only way back is a database edit.
    Task<ChecklistResult> ClearAsync(
        Guid employeeId,
        Guid itemId,
        CancellationToken cancellationToken = default);
}

public sealed record ChecklistItemDto
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public string? Category { get; init; }

    public required ResponsibleParty ResponsibleParty { get; init; }

    // Signed days from the starter's first day: negative before they arrive,
    // zero on day one, positive after.
    public required int DueDayOffset { get; init; }

    public required bool IsRequired { get; init; }

    public required bool IsActive { get; init; }

    public required int Sequence { get; init; }

    // Derived: how many starters have completed this task. The figure that
    // decides whether it can be deleted.
    public required int Completions { get; init; }

    public required bool CanDelete { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ChecklistTaskDto
{
    public required ChecklistItemDto Item { get; init; }

    // Null where the starter has no start date recorded, in which case the
    // task cannot be overdue either.
    public DateOnly? DueOn { get; init; }

    public required ChecklistTaskState State { get; init; }

    public DateOnly? CompletedOn { get; init; }

    public string? CompletedByName { get; init; }

    public string? Note { get; init; }
}

public sealed record ChecklistHireDto
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? JobTitle { get; init; }

    public string? DepartmentName { get; init; }

    public DateOnly? StartDate { get; init; }

    public required ChecklistProgress Progress { get; init; }
}

public sealed record EmployeeChecklistDto
{
    public required ChecklistHireDto Hire { get; init; }

    public required IReadOnlyList<ChecklistTaskDto> Tasks { get; init; }
}

public sealed record ChecklistHireQuery : PagedQuery
{
    // How far back to look, counted from the start date. Explicit rather than
    // hidden: a company that has just imported three hundred existing staff
    // has three hundred untouched checklists, and whether those belong on an
    // onboarding screen is their call, not a rule buried in a service. Null
    // means everybody.
    public int? StartedWithinDays { get; init; } = DefaultWindowDays;

    // Long enough to cover the ninety-day probation task the template ends
    // with, plus headroom.
    public const int DefaultWindowDays = 180;

    public ChecklistProgressState? State { get; init; }

    public Guid? DepartmentId { get; init; }

    public string? Search { get; init; }
}

public sealed record ChecklistItemRequest
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public string? Category { get; init; }

    public ResponsibleParty ResponsibleParty { get; init; }

    public int DueDayOffset { get; init; }

    public bool IsRequired { get; init; } = true;
}

public sealed record ChecklistCompletionRequest
{
    // Omitted means today, in the company's own calendar. Set when recording
    // something that happened earlier.
    public DateOnly? CompletedOn { get; init; }

    public string? Note { get; init; }
}

public enum ChecklistOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,
    Refused,
}

public sealed record ChecklistResult
{
    public required ChecklistOutcome Outcome { get; init; }

    public ChecklistItemDto? Item { get; init; }

    // Set by the completion routes, so the caller gets the starter's new
    // progress back rather than having to re-read it.
    public EmployeeChecklistDto? Checklist { get; init; }

    public ChecklistRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == ChecklistOutcome.Succeeded;

    public static ChecklistResult Failed(ChecklistOutcome outcome) => new() { Outcome = outcome };

    public static ChecklistResult Refused(ChecklistRefusal refusal) =>
        new() { Outcome = ChecklistOutcome.Refused, Refusal = refusal };

    public static ChecklistResult Ok(ChecklistItemDto item) =>
        new() { Outcome = ChecklistOutcome.Succeeded, Item = item };

    public static ChecklistResult Progressed(EmployeeChecklistDto checklist) =>
        new() { Outcome = ChecklistOutcome.Succeeded, Checklist = checklist };

    public static ChecklistResult Done() => new() { Outcome = ChecklistOutcome.Succeeded };
}
