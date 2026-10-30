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
    [HttpPost("mine/{id:guid}/acknowledge")]
    [RequiresEmployee]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken cancellationToken) =>
        Respond(await documents.AcknowledgeAsync(id, cancellationToken), "Acknowledged.");

    // ── folders ─────────────────────────────────────────────────────────────

    [HttpGet("folders")]
    [ProducesResponseType<IReadOnlyList<FolderDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
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

    [HttpGet]
    [ProducesResponseType<PagedResult<DocumentDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] DocumentQuery query,
        CancellationToken cancellationToken) =>
        Ok(await documents.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<DocumentDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        DocumentDto? document = await documents.GetAsync(id, cancellationToken);

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
            await documents.AcknowledgementsAsync(id, cancellationToken);

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
            or DocumentOutcome.FolderNotDeletable => MoteeStatusCodes.Conflict,
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
        _ => "Could not save the document.",
    };
}

public sealed record RenameFolderRequest
{
    public required string Name { get; init; }
}
