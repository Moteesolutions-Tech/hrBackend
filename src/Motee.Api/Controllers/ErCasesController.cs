using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Files;
using Motee.Application.Relations;
using Motee.Domain.Authorization;
using Motee.Domain.Files;
using Motee.Domain.Relations;

namespace Motee.Api.Controllers;

// Employee-relations cases: grievance, disciplinary and everything adjacent.
//
// The register itself is the admin grievance module. One person's own cases are
// the two tabs on their employee record, each under its own key — an HR manager
// may be given a named employee's grievances without the whole register — and
// those are the two routes at the bottom.
//
// Raising one, and reading your own, need no module grant at all: somebody has
// to be able to raise a grievance about their own manager.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/er-cases")]
public class ErCasesController(IErCaseService cases) : ApiControllerBase
{
    private const string Module = "admin.grievance";

    // Above the purpose's own 20 MB ceiling, so an oversized file is refused by
    // the policy with a message somebody can act on rather than dropped by the
    // server mid-upload.
    private const long MaxRequestBytes = 30L * 1024 * 1024;

    [HttpGet]
    [ProducesResponseType<PagedResult<CaseRowDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] CaseQuery query,
        CancellationToken cancellationToken) =>
        Ok(await cases.ListAsync(query, cancellationToken));

    [HttpGet("summary")]
    [ProducesResponseType<CaseSummaryDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await cases.SummaryAsync(cancellationToken));

    // Cases the caller raised, or is the subject of. No module grant: a
    // grievance about your own manager is not something to need permission for.
    [HttpGet("mine")]
    [Authorize]
    [ProducesResponseType<PagedResult<CaseRowDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(
        [FromQuery] CaseQuery query,
        CancellationToken cancellationToken) =>
        Ok(await cases.MineAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<CaseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        CaseDto? found = await cases.GetAsync(id, cancellationToken);

        // A restricted case the caller is not named on reads as not found
        // rather than as forbidden: that there is a case about somebody is
        // itself the confidential part.
        return found is null
            ? Failure<CaseDto>(MoteeStatusCodes.NotFound, "Case not found.")
            : Ok(found);
    }

    [HttpGet("{id:guid}/activity")]
    [ProducesResponseType<IReadOnlyList<CaseActivityDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Activity(Guid id, CancellationToken cancellationToken) =>
        Ok(await cases.ActivityAsync(id, cancellationToken));

    // Raising one needs no module grant either.
    [HttpPost]
    [Authorize]
    [ProducesResponseType<CaseDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Raise(
        CaseRequest request,
        CancellationToken cancellationToken)
    {
        CaseResult result = await cases.RaiseAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Case!, "Case raised.")
            : Failure<CaseDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<CaseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        CaseRequest request,
        CancellationToken cancellationToken) =>
        Respond(await cases.UpdateAsync(id, request, cancellationToken), "Case updated.");

    // Refused with the reasons when a stage in between has not recorded what it
    // exists to record — so the screen can say what is missing rather than
    // greying out a chip.
    [HttpPatch("{id:guid}/stage")]
    [ProducesResponseType<CaseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Move(
        Guid id,
        CaseStageRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await cases.MoveAsync(id, request.Stage, cancellationToken), "Case moved on.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        CaseResult result = await cases.DeleteAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Case deleted.")
            : Failure<object?>(StatusFor(result), MessageFor(result));
    }

    // ── the trail ───────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/notes")]
    [ProducesResponseType<CaseDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> AddNote(
        Guid id,
        NoteRequest request,
        CancellationToken cancellationToken)
    {
        CaseResult result = await cases.AddNoteAsync(id, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Case!, "Note added.")
            : Failure<CaseDto>(StatusFor(result), MessageFor(result));
    }

    [HttpDelete("{id:guid}/notes/{noteId:guid}")]
    [ProducesResponseType<CaseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> RemoveNote(
        Guid id,
        Guid noteId,
        CancellationToken cancellationToken) =>
        Respond(await cases.RemoveNoteAsync(id, noteId, cancellationToken), "Note removed.");

    [HttpPost("{id:guid}/meetings")]
    [ProducesResponseType<CaseDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> RecordMeeting(
        Guid id,
        MeetingRequest request,
        CancellationToken cancellationToken)
    {
        CaseResult result = await cases.RecordMeetingAsync(id, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Case!, "Meeting recorded.")
            : Failure<CaseDto>(StatusFor(result), MessageFor(result));
    }

    [HttpDelete("{id:guid}/meetings/{meetingId:guid}")]
    [ProducesResponseType<CaseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> RemoveMeeting(
        Guid id,
        Guid meetingId,
        CancellationToken cancellationToken) =>
        Respond(
            await cases.RemoveMeetingAsync(id, meetingId, cancellationToken),
            "Meeting removed.");

    [HttpPost("{id:guid}/evidence")]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType<CaseDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> AttachEvidence(
        Guid id,
        [FromForm] IFormFile file,
        [FromForm] string? name,
        [FromForm] string? description,
        CancellationToken cancellationToken)
    {
        await using Stream content = file.OpenReadStream();

        CaseResult result = await cases.AttachEvidenceAsync(
            id,
            new EvidenceUpload
            {
                Name = name,
                Description = description,
                FileName = file.FileName,
                ContentType = file.ContentType,
                Content = content,
                SizeBytes = file.Length,
            },
            cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Case!, "Evidence attached.")
            : Failure<CaseDto>(StatusFor(result), MessageFor(result));
    }

    [HttpGet("{id:guid}/evidence/{evidenceId:guid}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> DownloadEvidence(
        Guid id,
        Guid evidenceId,
        CancellationToken cancellationToken)
    {
        FileContent? content = await cases.OpenEvidenceAsync(
            id, evidenceId, cancellationToken);

        if (content is null)
        {
            return Failure<object?>(MoteeStatusCodes.NotFound, "Evidence not found.");
        }

        // Attachment rather than inline: evidence should download, not render
        // in the tab under our own origin.
        return File(content.Content, content.ContentType, content.FileName);
    }

    [HttpDelete("{id:guid}/evidence/{evidenceId:guid}")]
    [ProducesResponseType<CaseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> RemoveEvidence(
        Guid id,
        Guid evidenceId,
        CancellationToken cancellationToken) =>
        Respond(
            await cases.RemoveEvidenceAsync(id, evidenceId, cancellationToken),
            "Evidence removed.");

    // ── one person's record ─────────────────────────────────────────────────

    // The two tabs on an employee's record, each under its own key. An appeal
    // or an investigation shows on both rather than one of them hiding a case
    // that plainly concerns the person.
    [HttpGet("/api/v{version:apiVersion}/employees/{employeeId:guid}/grievances")]
    [ProducesResponseType<PagedResult<CaseRowDto>>(StatusCodes.Status200OK)]
    [RequiresPermission("employee.grievances", PermissionAction.View)]
    public async Task<IActionResult> Grievances(
        Guid employeeId,
        [FromQuery] CaseQuery query,
        CancellationToken cancellationToken) =>
        Ok(await cases.ListAsync(
            query with { SubjectEmployeeId = employeeId, Group = CaseGroup.Grievance },
            cancellationToken));

    [HttpGet("/api/v{version:apiVersion}/employees/{employeeId:guid}/disciplinaries")]
    [ProducesResponseType<PagedResult<CaseRowDto>>(StatusCodes.Status200OK)]
    [RequiresPermission("employee.disciplinary", PermissionAction.View)]
    public async Task<IActionResult> Disciplinaries(
        Guid employeeId,
        [FromQuery] CaseQuery query,
        CancellationToken cancellationToken) =>
        Ok(await cases.ListAsync(
            query with { SubjectEmployeeId = employeeId, Group = CaseGroup.Disciplinary },
            cancellationToken));

    private IActionResult Respond(CaseResult result, string message) =>
        result.Succeeded
            ? Ok(result.Case!, message)
            : Failure<CaseDto>(StatusFor(result), MessageFor(result));

    private static string StatusFor(CaseResult result) => result.Outcome switch
    {
        // Restricted reads as not found: that a case exists about somebody is
        // itself the confidential part.
        CaseOutcomeKind.NotFound or CaseOutcomeKind.NotVisible => MoteeStatusCodes.NotFound,

        CaseOutcomeKind.EmployeeNotFound => MoteeStatusCodes.NotFound,

        // A state problem rather than a malformed request.
        CaseOutcomeKind.Blocked => MoteeStatusCodes.Conflict,

        CaseOutcomeKind.NoEmployeeRecord => MoteeStatusCodes.Forbidden,

        CaseOutcomeKind.Refused when result.Rejection == FileRejection.TooLarge =>
            MoteeStatusCodes.PayloadTooLarge,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(CaseResult result) => result.Outcome switch
    {
        CaseOutcomeKind.NotFound or CaseOutcomeKind.NotVisible => "Case not found.",
        CaseOutcomeKind.EmployeeNotFound => "That person does not work here.",
        CaseOutcomeKind.NoEmployeeRecord =>
            "Your account has no employee record, so there is nobody to raise a case as.",

        // The reasons, so somebody can act on them rather than guess.
        CaseOutcomeKind.Blocked => string.Join(" ", result.Blockers.Select(Sentence)),

        _ => result.Rejection != FileRejection.None
            ? FileMessage(result.Rejection)
            : ReasonFor(result.Refusal),
    };

    private static string Sentence(string blocker) =>
        blocker.EndsWith('.') ? blocker : blocker + ".";

    private static string ReasonFor(CaseRefusal refusal) => refusal switch
    {
        CaseRefusal.DescriptionTooShort =>
            "Describe what happened — a case is a record somebody will be asked to act on.",
        CaseRefusal.SubjectRequired => "Say who the case is about.",
        CaseRefusal.IncidentInTheFuture => "The incident date is in the future.",
        CaseRefusal.InvestigatorIsSubject =>
            "The investigator cannot be the person the case is about.",
        CaseRefusal.OwnerIsSubject =>
            "The case owner cannot be the person the case is about.",
        CaseRefusal.NoteRequired => "Write something before saving the note.",
        CaseRefusal.AttendeesRequired => "Record who was at the meeting.",
        CaseRefusal.MeetingInTheFuture =>
            "That meeting has not happened yet, so there is nothing to record about it.",
        CaseRefusal.StageNotInFlow => "This kind of case does not have that stage.",
        _ => "Could not save the case.",
    };

    private static string FileMessage(FileRejection rejection) => rejection switch
    {
        FileRejection.Empty => "That file is empty.",
        FileRejection.TooLarge => "That file is larger than this upload allows.",
        FileRejection.DisallowedType => "That file type is not accepted here.",
        FileRejection.ContentDoesNotMatchType =>
            "That file's contents do not match its type.",
        _ => "Could not attach that file.",
    };
}

public sealed record CaseStageRequest
{
    public required CaseStage Stage { get; init; }
}
