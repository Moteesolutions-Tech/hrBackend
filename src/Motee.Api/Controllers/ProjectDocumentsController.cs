using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Files;
using Motee.Application.Projects;
using Motee.Domain.Authorization;
using Motee.Domain.Files;
using Motee.Domain.Projects;

namespace Motee.Api.Controllers;

// Rollout documents hung off a project: a charter, a data-migration mapping, a
// UAT plan.
//
// Deliberately not the document library: that module's folders, sharing,
// versioning and acknowledgement tracking are for company-wide compliance
// filing. The bytes still go through the one upload path, so the size ceiling
// and the type check are the product's.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/projects/{projectId:guid}/documents")]
public class ProjectDocumentsController(IProjectDocumentService documents) : ApiControllerBase
{
    private const string Module = "workspace.projects";

    // Above the purpose's own 50 MB ceiling, so an oversized file is refused
    // by the policy with a message somebody can act on rather than dropped by
    // the server mid-upload.
    private const long MaxRequestBytes = 60L * 1024 * 1024;

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ProjectDocumentDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] ProjectDocumentCategory? category,
        CancellationToken cancellationToken) =>
        Ok(await documents.ListAsync(projectId, category, cancellationToken));

    [HttpPost]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType<ProjectDocumentDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Attach(
        Guid projectId,
        [FromForm] IFormFile file,
        [FromForm] ProjectDocumentCategory category,
        [FromForm] string? name,
        [FromForm] string? description,
        CancellationToken cancellationToken)
    {
        await using Stream content = file.OpenReadStream();

        ProjectDocumentResult result = await documents.AttachAsync(
            projectId,
            new ProjectDocumentUpload
            {
                Name = name,
                Category = category,
                Description = description,
                FileName = file.FileName,
                ContentType = file.ContentType,
                Content = content,
                SizeBytes = file.Length,
            },
            cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Document!, "Document attached.")
            : Failure<ProjectDocumentDto>(StatusFor(result), MessageFor(result));
    }

    [HttpGet("{documentId:guid}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Download(
        Guid projectId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        FileContent? content = await documents.OpenAsync(
            projectId, documentId, cancellationToken);

        if (content is null)
        {
            return Failure<object?>(MoteeStatusCodes.NotFound, "Document not found.");
        }

        // Attachment rather than inline: an uploaded document should download,
        // not render in the tab under our own origin.
        return File(content.Content, content.ContentType, content.FileName);
    }

    [HttpDelete("{documentId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(
        Guid projectId,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        ProjectDocumentResult result = await documents.DeleteAsync(
            projectId, documentId, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Document removed.")
            : Failure<object?>(StatusFor(result), MessageFor(result));
    }

    private static string StatusFor(ProjectDocumentResult result) => result.Outcome switch
    {
        ProjectOutcome.NotFound => MoteeStatusCodes.NotFound,
        ProjectOutcome.Refused when result.Rejection == FileRejection.TooLarge =>
            MoteeStatusCodes.PayloadTooLarge,

        // Nothing the caller sent was wrong: the request reached here without a
        // tenant, which is ours to fix.
        ProjectOutcome.Refused when result.Rejection == FileRejection.NoTenant =>
            MoteeStatusCodes.InternalServerError,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(ProjectDocumentResult result) => result.Outcome switch
    {
        ProjectOutcome.NotFound => "Project or document not found.",
        _ => result.Rejection switch
        {
            FileRejection.Empty => "That file is empty.",
            FileRejection.TooLarge => "That file is larger than this upload allows.",
            FileRejection.DisallowedType => "That file type is not accepted here.",
            FileRejection.ContentDoesNotMatchType =>
                "That file's contents do not match its type.",
            FileRejection.NoTenant =>
                "Could not determine which company this upload belongs to.",
            _ => "Could not attach that document.",
        },
    };
}
