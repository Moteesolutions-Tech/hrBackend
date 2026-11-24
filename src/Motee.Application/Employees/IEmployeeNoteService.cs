using Motee.Domain.Employees;

namespace Motee.Application.Employees;

public enum NoteOutcome
{
    Succeeded,
    NotFound,
    Refused,
}

// HR's private notes and reminders about one person.
//
// The confidential filter is applied here rather than left to the caller: a
// manager asking for somebody's notes gets the ones marked for managers, and
// there is no parameter they could pass to get the rest.
public interface IEmployeeNoteService
{
    Task<IReadOnlyList<EmployeeNoteDto>> ForAsync(
        Guid employeeId,
        bool includePrivate,
        CancellationToken cancellationToken = default);

    Task<NoteResult> AddAsync(
        Guid employeeId,
        EmployeeNoteRequest request,
        CancellationToken cancellationToken = default);

    Task<NoteResult> UpdateAsync(
        Guid employeeId,
        Guid noteId,
        EmployeeNoteRequest request,
        CancellationToken cancellationToken = default);

    Task<NoteResult> PinAsync(
        Guid employeeId,
        Guid noteId,
        bool pinned,
        CancellationToken cancellationToken = default);

    Task<NoteOutcome> RemoveAsync(
        Guid employeeId,
        Guid noteId,
        CancellationToken cancellationToken = default);

    // The caller's own reminders that have come due. Theirs, because a
    // reminder is something a particular person asked to be told.
    Task<IReadOnlyList<EmployeeNoteDto>> MyRemindersAsync(
        CancellationToken cancellationToken = default);
}

public sealed record NoteResult
{
    public required NoteOutcome Outcome { get; init; }

    public EmployeeNoteDto? Note { get; init; }

    public NoteRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == NoteOutcome.Succeeded;

    public static NoteResult Ok(EmployeeNoteDto note) =>
        new() { Outcome = NoteOutcome.Succeeded, Note = note };

    public static NoteResult Failed(NoteOutcome outcome) => new() { Outcome = outcome };

    public static NoteResult Refused(NoteRefusal refusal) =>
        new() { Outcome = NoteOutcome.Refused, Refusal = refusal };
}

public sealed record EmployeeNoteDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public string? AuthorName { get; init; }

    public required string Body { get; init; }

    public required NoteKind Kind { get; init; }

    public required NoteAudience Audience { get; init; }

    public required bool Pinned { get; init; }

    public DateOnly? RemindAt { get; init; }

    public required bool ReminderIsDue { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record EmployeeNoteRequest
{
    public required string Body { get; init; }

    public NoteKind Kind { get; init; } = NoteKind.Note;

    // Private unless somebody says otherwise. A note written without thinking
    // about the setting is the one most likely to need protecting.
    public NoteAudience Audience { get; init; } = NoteAudience.HrOnly;

    public bool Pinned { get; init; }

    public DateOnly? RemindAt { get; init; }
}
