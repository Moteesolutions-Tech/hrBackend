using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Api.Http;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Domain.Authorization;
using Motee.Domain.Employees;

namespace Motee.Api.Controllers;

// HR's private notes and reminders about one person.
//
// Everything here is gated on employee.notes. The screen gates the tab's
// visibility on that key but its Add and Edit buttons on
// organization.employees, so anybody who could edit an employee could write
// HR-private notes about them — the permission that names the feature was
// doing half the job.
//
// The audience is applied on read, by data scope: HR reads everything, a line
// manager reads only what was marked for managers. That field is the one that
// decides whether writing something frank about somebody is safe to do, and
// the screen renders it as a pill and filters on nothing.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/employees/{employeeId:guid}/notes")]
public class EmployeeNotesController(
    IEmployeeNoteService notes,
    IUserPermissions userPermissions) : ApiControllerBase
{
    private const string Module = "employee.notes";

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<EmployeeNoteDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        Guid employeeId,
        CancellationToken cancellationToken) =>
        Ok(await notes.ForAsync(
            employeeId, await CanReadPrivateAsync(cancellationToken), cancellationToken));

    [HttpPost]
    [ProducesResponseType<EmployeeNoteDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Add(
        Guid employeeId,
        EmployeeNoteRequest request,
        CancellationToken cancellationToken)
    {
        NoteResult result = await notes.AddAsync(employeeId, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Note!, "Note added.")
            : Failure<EmployeeNoteDto>(StatusFor(result.Outcome), MessageFor(result));
    }

    [HttpPut("{noteId:guid}")]
    [ProducesResponseType<EmployeeNoteDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid employeeId,
        Guid noteId,
        EmployeeNoteRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await notes.UpdateAsync(employeeId, noteId, request, cancellationToken),
            "Note updated.");

    // Its own route, because pinning is the only ordering control the panel has
    // and burying it in the edit form means re-sending the whole note to move
    // it to the top.
    [HttpPost("{noteId:guid}/pin")]
    [ProducesResponseType<EmployeeNoteDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Pin(
        Guid employeeId,
        Guid noteId,
        [FromQuery] bool pinned = true,
        CancellationToken cancellationToken = default) =>
        Respond(
            await notes.PinAsync(employeeId, noteId, pinned, cancellationToken),
            pinned ? "Note pinned." : "Note unpinned.");

    [HttpDelete("{noteId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Remove(
        Guid employeeId,
        Guid noteId,
        CancellationToken cancellationToken)
    {
        NoteOutcome outcome = await notes.RemoveAsync(employeeId, noteId, cancellationToken);

        return outcome == NoteOutcome.Succeeded
            ? Ok<object?>(null, "Note removed.")
            : Failure<object?>(MoteeStatusCodes.NotFound, "Note not found.");
    }

    // Everything marked for managers, plus the private ones where the caller's
    // reach is wider than their own team. Mirrors the sickness and
    // occupational-health rule: a line manager holds edit on their own reports,
    // so gating on the action would hand them everything.
    private async Task<bool> CanReadPrivateAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirst(MoteeClaimTypes.Subject)?.Value, out Guid userId))
        {
            return false;
        }

        ResolvedPermissions held = await userPermissions.ForAsync(userId, cancellationToken);

        if (held.IsOwner)
        {
            return true;
        }

        DataScope scope = AccessDecision.Resolve(
            held.Permissions, Module, PermissionAction.View);

        return scope.Kind is not (DataScopeKind.None
            or DataScopeKind.Self
            or DataScopeKind.DirectReports);
    }

    private IActionResult Respond(NoteResult result, string message) =>
        result.Succeeded
            ? Ok(result.Note!, message)
            : Failure<EmployeeNoteDto>(StatusFor(result.Outcome), MessageFor(result));

    private static string StatusFor(NoteOutcome outcome) => outcome switch
    {
        NoteOutcome.NotFound => MoteeStatusCodes.NotFound,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(NoteResult result) => result.Outcome switch
    {
        NoteOutcome.NotFound => "Note not found.",
        _ => result.Refusal switch
        {
            NoteRefusal.BodyRequired => "Write something in the note.",
            NoteRefusal.BodyTooLong =>
                $"A note can be at most {EmployeeNotes.MaximumBodyLength} characters.",
            NoteRefusal.ReminderNeedsADate =>
                "Give the reminder a date — a reminder with none reminds nobody.",
            NoteRefusal.ReminderInThePast => "A reminder cannot be set for a day already past.",
            NoteRefusal.EmployeeNotFound => "That person does not work here.",
            _ => "That change was not accepted.",
        },
    };
}

// The caller's own reminders that have come due.
//
// Its own controller because the route has no employee in it: a reminder
// belongs to whoever set it, across everybody they wrote one about.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/my-reminders")]
public class MyRemindersController(IEmployeeNoteService notes) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<EmployeeNoteDto>>(StatusCodes.Status200OK)]
    [RequiresPermission("employee.notes", PermissionAction.View)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await notes.MyRemindersAsync(cancellationToken));
}
