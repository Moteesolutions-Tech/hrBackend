using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Documents;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// The company's filing.
//
// The bytes go through /files first and their id is handed here. Keeping the two
// apart means a large upload that fails does not take a half-made document with it,
// and the browser can show progress on the part that actually takes time.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/documents")]
public class DocumentsController(IDocumentService documents) : ApiControllerBase
{
    private const string Module = "operations.documents";

    // ── what the caller can see ─────────────────────────────────────────────

    // Shared with them by name, assigned to their department, or global — one
    // list, deduplicated. Which route a document arrived by is not the employee's
    // problem.
    [HttpGet("mine")]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<DocumentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await documents.MineAsync(cancellationToken));

    // Saying they have read it. Against the current version, so a reupload asks
    // again rather than inheriting an agreement to something else.
    // ── self-service ────────────────────────────────────────────────────────
    //
    // RequiresEmployee throughout, like the rest of self-service: needing the documents
    // module to keep your own certificates would mean granting every member of staff
    // the screen showing the whole company's filing.
    //
    // Everything here acts on the caller's own drawer, and the service checks ownership
    // on each one. That check is the whole of the safety: GET mine returns what HR
    // assigned as well as what the employee uploaded, and only the second is theirs to
    // move or bin.

    [HttpPost("mine")]
    [RequiresEmployee]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> UploadMine(
        SelfUploadRequest request,
        CancellationToken cancellationToken)
    {
        DocumentResult result = await documents.UploadMineAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Document!, "Uploaded for review.")
            : Failure<DocumentDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpGet("mine/folders")]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<FolderDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MyFolders(CancellationToken cancellationToken) =>
        Ok(await documents.MyFoldersAsync(cancellationToken));

    [HttpPost("mine/folders")]
    [RequiresEmployee]
    [ProducesResponseType<FolderDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateMyFolder(
        MyFolderRequest request,
        CancellationToken cancellationToken)
    {
        FolderResult result = await documents.SaveMyFolderAsync(
            null, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Folder!, "Folder created.")
            : Failure<FolderDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPut("mine/folders/{id:guid}")]
    [RequiresEmployee]
    [ProducesResponseType<FolderDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateMyFolder(
        Guid id,
        MyFolderRequest request,
        CancellationToken cancellationToken)
    {
        FolderResult result = await documents.SaveMyFolderAsync(id, request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Folder!, "Folder updated.")
            : Failure<FolderDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpDelete("mine/folders/{id:guid}")]
    [RequiresEmployee]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteMyFolder(Guid id, CancellationToken cancellationToken)
    {
        DocumentOutcome outcome = await documents.DeleteMyFolderAsync(id, cancellationToken);

        return outcome == DocumentOutcome.Succeeded
            ? Ok<object?>(null, "Folder deleted.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    [HttpPost("mine/{id:guid}/move")]
    [RequiresEmployee]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MoveMine(
        Guid id,
        [FromBody] MoveDocumentRequest? request,
        CancellationToken cancellationToken) =>
        RespondMine(
            await documents.MoveMineAsync(id, request?.FolderId, cancellationToken),
            "Document moved.");

    [HttpPost("mine/{id:guid}/trash")]
    [RequiresEmployee]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> TrashMine(Guid id, CancellationToken cancellationToken) =>
        RespondMine(await documents.TrashMineAsync(id, cancellationToken), "Document binned.");

    [HttpPost("mine/{id:guid}/restore")]
    [RequiresEmployee]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RestoreMine(Guid id, CancellationToken cancellationToken) =>
        RespondMine(await documents.RestoreMineAsync(id, cancellationToken), "Document restored.");

    // Their bin. A separate read because GET mine lists what they can see, and a binned
    // document is not among them — so without this, restore above had no screen to be
    // reached from.
    [HttpGet("mine/trash")]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<DocumentDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MyTrash(CancellationToken cancellationToken) =>
        Ok(await documents.MyTrashAsync(cancellationToken));

    // Gone for good, and only from the bin: a document still in the drawer has to be
    // binned first, so one click cannot destroy it irrecoverably.
    [HttpDelete("mine/{id:guid}")]
    [RequiresEmployee]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteMine(Guid id, CancellationToken cancellationToken) =>
        RespondMine(await documents.DeleteMineAsync(id, cancellationToken), "Document deleted.");

    // ── HR's review queue ───────────────────────────────────────────────────
    //
    // Behind Edit rather than View: accepting an upload files it onto somebody's record,
    // so the privilege that matters is deciding, not reading the queue.

    [HttpGet("reviews")]
    [ProducesResponseType<PagedResult<DocumentDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Reviews(
        [FromQuery] PagedQuery query,
        CancellationToken cancellationToken) =>
        Ok(await documents.ReviewsAsync(query, cancellationToken));

    [HttpPost("reviews/{id:guid}/approve")]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> AcceptUpload(
        Guid id,
        [FromBody] ReviewUploadRequest? request,
        CancellationToken cancellationToken) =>
        RespondMine(
            await documents.AcceptUploadAsync(id, request?.Note, cancellationToken),
            "Document accepted onto the record.");

    [HttpPost("reviews/{id:guid}/reject")]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> RejectUpload(
        Guid id,
        RejectUploadRequest request,
        CancellationToken cancellationToken) =>
        RespondMine(
            await documents.RejectUploadAsync(id, request.Reason, cancellationToken),
            "Document rejected.");

    private IActionResult RespondMine(DocumentResult result, string message) =>
        result.Succeeded
            ? Ok(result.Document!, message)
            : Failure<DocumentDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));

    [HttpPost("mine/{id:guid}/acknowledge")]
    [RequiresEmployee]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken cancellationToken) =>
        Respond(await documents.AcknowledgeAsync(id, cancellationToken), "Acknowledged.");

    // ── folders ─────────────────────────────────────────────────────────────

    // HR's folder tree, which is the shape of the company's filing rather than
    // anybody's own drawer. Their own folders are documents/mine/folders.
    [HttpGet("folders")]
    [ProducesResponseType<IReadOnlyList<FolderDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View, MinimumScope = DataScopeKind.DirectReports)]
    public async Task<IActionResult> Folders(CancellationToken cancellationToken) =>
        Ok(await documents.FoldersAsync(cancellationToken));

    [HttpPost("folders")]
    [ProducesResponseType<FolderDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> CreateFolder(
        FolderRequest request,
        CancellationToken cancellationToken)
    {
        FolderResult result = await documents.CreateFolderAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Folder!, "Folder created.")
            : Failure<FolderDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPut("folders/{id:guid}")]
    [ProducesResponseType<FolderDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> RenameFolder(
        Guid id,
        RenameFolderRequest request,
        CancellationToken cancellationToken)
    {
        FolderResult result =
            await documents.RenameFolderAsync(id, request.Name, cancellationToken);

        return result.Succeeded
            ? Ok(result.Folder!, "Folder renamed.")
            : Failure<FolderDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpDelete("folders/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> DeleteFolder(Guid id, CancellationToken cancellationToken)
    {
        DocumentOutcome outcome = await documents.DeleteFolderAsync(id, cancellationToken);

        return outcome == DocumentOutcome.Succeeded
            ? Ok<object?>(null, "Folder deleted.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    // ── documents ───────────────────────────────────────────────────────────

    // The HR document register. An employee's own documents and drawer are reached
    // through the /mine routes, so Self scope has no business here.
    [HttpGet]
    [ProducesResponseType<PagedResult<DocumentDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View, MinimumScope = DataScopeKind.DirectReports)]
    public async Task<IActionResult> List(
        [FromQuery] DocumentQuery query,
        CancellationToken cancellationToken) =>
        Ok(await documents.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        DocumentDto? document = await documents.GetAsync(id, Caller(), cancellationToken);

        return document is null
            ? Failure<DocumentDto>(MoteeStatusCodes.NotFound, "Document not found.")
            : Ok(document);
    }

    [HttpPost]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        CreateDocumentRequest request,
        CancellationToken cancellationToken)
    {
        DocumentResult result = await documents.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Document!, "Document filed.")
            : Failure<DocumentDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        UpdateDocumentRequest request,
        CancellationToken cancellationToken) =>
        Respond(await documents.UpdateAsync(id, request, cancellationToken), "Document updated.");

    // A new upload of the same document. The old version stays — "what did the
    // policy say in March" is what this exists to answer.
    [HttpPost("{id:guid}/versions")]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> AddVersion(
        Guid id,
        AddVersionRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await documents.AddVersionAsync(id, request, cancellationToken), "New version filed.");

    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken) =>
        Respond(await documents.ArchiveAsync(id, cancellationToken), "Document archived.");

    // Recoverable. Deleting would remove the bytes; this hides the row.
    [HttpPost("{id:guid}/trash")]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Trash(Guid id, CancellationToken cancellationToken) =>
        Respond(await documents.TrashAsync(id, cancellationToken), "Moved to trash.");

    [HttpPost("{id:guid}/restore")]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken) =>
        Respond(await documents.RestoreAsync(id, cancellationToken), "Document restored.");

    // ── sharing and acknowledgement reporting ───────────────────────────────

    [HttpPost("{id:guid}/shares")]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Share(
        Guid id,
        ShareDocumentRequest request,
        CancellationToken cancellationToken) =>
        Respond(await documents.ShareAsync(id, request, cancellationToken), "Document shared.");

    [HttpDelete("{id:guid}/shares/{employeeId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Unshare(
        Guid id,
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        DocumentOutcome outcome = await documents.UnshareAsync(id, employeeId, cancellationToken);

        return outcome == DocumentOutcome.Succeeded
            ? Ok<object?>(null, "Share removed.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    // Who has agreed and who has not. The outstanding list is named rather than
    // counted, because a number is not something anybody can chase.
    [HttpGet("{id:guid}/acknowledgements")]
    [ProducesResponseType<AcknowledgementReportDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Acknowledgements(
        Guid id,
        CancellationToken cancellationToken)
    {
        AcknowledgementReportDto? report =
            await documents.AcknowledgementsAsync(id, Caller(), cancellationToken);

        return report is null
            ? Failure<AcknowledgementReportDto>(
                MoteeStatusCodes.NotFound, "Document not found.")
            : Ok(report);
    }

    private IActionResult Respond(DocumentResult result, string message) =>
        result.Succeeded
            ? Ok(result.Document!, message)
            : Failure<DocumentDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));

    private static string StatusFor(DocumentOutcome outcome) => outcome switch
    {
        DocumentOutcome.NotFound or DocumentOutcome.EmployeeNotFound
            or DocumentOutcome.FolderNotFound or DocumentOutcome.FileNotFound =>
            MoteeStatusCodes.NotFound,
        DocumentOutcome.DuplicateFolderName or DocumentOutcome.FolderNotEmpty
            or DocumentOutcome.AlreadyAcknowledged or DocumentOutcome.AlreadyShared
            or DocumentOutcome.FolderNotDeletable
            or DocumentOutcome.AlreadyReviewed
            or DocumentOutcome.NotAwaitingReview => MoteeStatusCodes.Conflict,

        // Not found rather than forbidden, for a document or folder belonging to
        // somebody else. Telling them an id exists but is none of their business is
        // itself a fact about the filing.
        DocumentOutcome.NotYours => MoteeStatusCodes.NotFound,
        DocumentOutcome.NoEmployeeRecord => MoteeStatusCodes.Forbidden,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(DocumentOutcome outcome) => outcome switch
    {
        DocumentOutcome.NotFound => "Document not found.",
        DocumentOutcome.EmployeeNotFound => "That person does not work here.",
        DocumentOutcome.FolderNotFound => "Folder not found.",
        DocumentOutcome.FileNotFound =>
            "That upload could not be found. Upload the file first, then file it.",
        DocumentOutcome.NameRequired => "Give it a name.",
        DocumentOutcome.DuplicateFolderName =>
            "There is already a folder with that name here.",
        DocumentOutcome.FolderNotDeletable =>
            "This folder is part of the filing structure and cannot be deleted. "
            + "Rename it instead.",
        DocumentOutcome.FolderNotEmpty =>
            "Empty the folder first. Deleting one with documents in it is not "
            + "something trash can undo.",
        DocumentOutcome.AcknowledgementNotRequired =>
            "This document does not ask to be acknowledged.",
        DocumentOutcome.AlreadyAcknowledged =>
            "You have already acknowledged this version.",
        DocumentOutcome.AlreadyShared =>
            "They already have this document. Change their permission instead.",
        DocumentOutcome.NoEmployeeRecord =>
            "This account has no employee record, so it has no documents of its own.",

        // Says it is not theirs without saying whose. A document HR assigned is theirs
        // to read and not to bin, and a folder belonging to a colleague is none of
        // their business — including the fact that it exists.
        DocumentOutcome.NotYours => "That is not yours to change.",

        // Names the step they have missed rather than just refusing, because the fix is
        // one action away and nothing about the request was malformed.
        DocumentOutcome.NotTrashed =>
            "Move it to the bin before deleting it for good.",
        DocumentOutcome.AlreadyReviewed => "That upload has already been decided.",
        DocumentOutcome.NotAwaitingReview =>
            "That document was filed by HR, so there is nothing to review.",
        DocumentOutcome.ReasonRequired =>
            "Say why it was refused — a rejection with no reason sends somebody to HR "
            + "to ask.",
        _ => "Could not save the document.",
    };
}

public sealed record RenameFolderRequest
{
    public required string Name { get; init; }
}

public sealed record MoveDocumentRequest
{
    // Null means out of any drawer, back to the top of their own list.
    public Guid? FolderId { get; init; }
}

public sealed record ReviewUploadRequest
{
    // Optional on an acceptance. The upload already explains itself; a note is for
    // the cases where HR wants to add something.
    public string? Note { get; init; }
}

public sealed record RejectUploadRequest
{
    public required string Reason { get; init; }
}
