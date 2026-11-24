using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Tenancy;
using Motee.Domain.Audit;
using Motee.Domain.Employees;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Employees;

// HR's private notes and reminders about one person.
//
// The audience filter is applied here rather than left to the caller, and
// includePrivate is resolved by the controller from the caller's data scope —
// so a manager asking for somebody's notes gets the ones marked for managers
// and there is no parameter they could pass to get the rest.
internal sealed class EmployeeNoteService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    IRequestContext requestContext,
    IAuditTrail audit,
    TimeProvider timeProvider) : IEmployeeNoteService
{
    private const string Module = "employee.notes";

    private readonly TenantCalendar _calendar = new(dbContext, currentTenant, timeProvider);

    public async Task<IReadOnlyList<EmployeeNoteDto>> ForAsync(
        Guid employeeId,
        bool includePrivate,
        CancellationToken cancellationToken = default)
    {
        List<EmployeeNote> notes = await dbContext.EmployeeNotes
            .AsNoTracking()
            .Where(note => note.EmployeeId == employeeId)
            .ToListAsync(cancellationToken);

        if (!includePrivate)
        {
            notes = [.. notes.Where(EmployeeNotes.IsReadableByManager)];
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return [.. EmployeeNotes.InReadingOrder(notes).Select(note => Describe(note, today))];
    }

    public async Task<NoteResult> AddAsync(
        Guid employeeId,
        EmployeeNoteRequest request,
        CancellationToken cancellationToken = default)
    {
        Employee? employee = await dbContext.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == employeeId, cancellationToken);

        if (employee is null)
        {
            return NoteResult.Refused(NoteRefusal.EmployeeNotFound);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();

        EmployeeNote note = new()
        {
            Id = Guid.NewGuid(),
            TenantId = employee.TenantId,
            EmployeeId = employeeId,
            Body = (request.Body ?? string.Empty).Trim(),
            Kind = request.Kind,
            Audience = request.Audience,
            Pinned = request.Pinned,
            RemindAt = request.Kind == NoteKind.Reminder ? request.RemindAt : null,

            // From the request, not the payload: the author of a private note
            // is not something a client gets to claim.
            AuthorUserId = AuthorId(),
            AuthorName = await AuthorNameAsync(cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };

        NoteRefusal refusal = EmployeeNotes.Check(note, today);

        if (refusal != NoteRefusal.None)
        {
            return NoteResult.Refused(refusal);
        }

        dbContext.EmployeeNotes.Add(note);

        // The sentence says a note was added and never what it said. These are
        // the notes somebody writes before a difficult conversation, and
        // reproducing one into the audit trail would put it in a table this
        // module does not gate.
        audit.Record(
            AuditAction.Create,
            Module,
            note.Kind == NoteKind.Reminder ? "Reminder added" : "Note added",
            entityId: note.Id,
            entityType: nameof(EmployeeNote));

        await dbContext.SaveChangesAsync(cancellationToken);

        return NoteResult.Ok(Describe(note, today));
    }

    public async Task<NoteResult> UpdateAsync(
        Guid employeeId,
        Guid noteId,
        EmployeeNoteRequest request,
        CancellationToken cancellationToken = default)
    {
        EmployeeNote? note = await dbContext.EmployeeNotes
            .FirstOrDefaultAsync(
                row => row.Id == noteId && row.EmployeeId == employeeId, cancellationToken);

        if (note is null)
        {
            return NoteResult.Failed(NoteOutcome.NotFound);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        note.Body = (request.Body ?? string.Empty).Trim();
        note.Kind = request.Kind;
        note.Audience = request.Audience;
        note.Pinned = request.Pinned;

        // Moving the date re-arms it. Otherwise pushing a reminder out would
        // leave it already raised and it would never fire again.
        if (request.Kind == NoteKind.Reminder)
        {
            if (note.RemindAt != request.RemindAt)
            {
                note.RemindedAt = null;
            }

            note.RemindAt = request.RemindAt;
        }
        else
        {
            note.RemindAt = null;
            note.RemindedAt = null;
        }

        NoteRefusal refusal = EmployeeNotes.Check(note, today);

        if (refusal != NoteRefusal.None)
        {
            return NoteResult.Refused(refusal);
        }

        note.UpdatedAt = timeProvider.GetUtcNow();

        audit.Record(
            AuditAction.Update,
            Module,
            "Note updated",
            entityId: note.Id,
            entityType: nameof(EmployeeNote));

        await dbContext.SaveChangesAsync(cancellationToken);

        return NoteResult.Ok(Describe(note, today));
    }

    public async Task<NoteResult> PinAsync(
        Guid employeeId,
        Guid noteId,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        EmployeeNote? note = await dbContext.EmployeeNotes
            .FirstOrDefaultAsync(
                row => row.Id == noteId && row.EmployeeId == employeeId, cancellationToken);

        if (note is null)
        {
            return NoteResult.Failed(NoteOutcome.NotFound);
        }

        note.Pinned = pinned;
        note.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return NoteResult.Ok(Describe(note, await _calendar.TodayAsync(cancellationToken)));
    }

    public async Task<NoteOutcome> RemoveAsync(
        Guid employeeId,
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        EmployeeNote? note = await dbContext.EmployeeNotes
            .FirstOrDefaultAsync(
                row => row.Id == noteId && row.EmployeeId == employeeId, cancellationToken);

        if (note is null)
        {
            return NoteOutcome.NotFound;
        }

        dbContext.EmployeeNotes.Remove(note);

        audit.Record(
            AuditAction.Delete,
            Module,
            "Note removed",
            entityId: note.Id,
            entityType: nameof(EmployeeNote));

        await dbContext.SaveChangesAsync(cancellationToken);

        return NoteOutcome.Succeeded;
    }

    public async Task<IReadOnlyList<EmployeeNoteDto>> MyRemindersAsync(
        CancellationToken cancellationToken = default)
    {
        if (AuthorId() is not Guid userId)
        {
            return [];
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        List<EmployeeNote> mine = await dbContext.EmployeeNotes
            .AsNoTracking()
            .Where(note => note.AuthorUserId == userId
                && note.Kind == NoteKind.Reminder
                && note.RemindAt != null
                && note.RemindAt <= today)
            .OrderBy(note => note.RemindAt)
            .ToListAsync(cancellationToken);

        return [.. mine.Select(note => Describe(note, today))];
    }

    private static EmployeeNoteDto Describe(EmployeeNote note, DateOnly today) => new()
    {
        Id = note.Id,
        EmployeeId = note.EmployeeId,
        AuthorName = note.AuthorName,
        Body = note.Body,
        Kind = note.Kind,
        Audience = note.Audience,
        Pinned = note.Pinned,
        RemindAt = note.RemindAt,
        ReminderIsDue = note.Kind == NoteKind.Reminder
            && note.RemindAt is DateOnly remind
            && remind <= today,
        CreatedAt = note.CreatedAt,
    };

    private Guid? AuthorId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private async Task<string?> AuthorNameAsync(CancellationToken cancellationToken)
    {
        if (AuthorId() is not Guid userId)
        {
            return null;
        }

        return await dbContext.Users
            .IgnoreQueryFilters()
            .Where(user => user.Id == userId)
            .Select(user => $"{user.FirstName} {user.LastName}")
            .FirstOrDefaultAsync(cancellationToken);
    }
}
