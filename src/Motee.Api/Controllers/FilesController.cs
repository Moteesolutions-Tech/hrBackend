using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Contracts;
using Motee.Application.Files;
using Motee.Domain.Files;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/files")]
[Authorize]
public class FilesController(IFileUploadService files) : ApiControllerBase
{
    [HttpPost]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> Upload(
        [FromForm] IFormFile file,
        [FromForm] FilePurpose purpose,
        [FromForm] Guid? ownerId,
        CancellationToken cancellationToken)
    {
        // Exports are written by the job, not uploaded. Accepting them here would let
        // anyone put a file where a finished export is expected.
        if (purpose == FilePurpose.Export)
        {
            return Failure<StoredFileDto>(
                MoteeStatusCodes.InvalidRequest, "Exports cannot be uploaded.");
        }

        await using Stream content = file.OpenReadStream();

        FileUploadResult result = await files.UploadAsync(
            new FileUpload
            {
                Purpose = purpose,
                OwnerId = ownerId,
                FileName = file.FileName,
                ContentType = file.ContentType,
                Content = content,
                SizeBytes = file.Length,
            },
            cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.File!, "File uploaded.")
            : Failure<StoredFileDto>(StatusFor(result.Rejection), MessageFor(result.Rejection));
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] FilePurpose purpose,
        [FromQuery] Guid? ownerId,
        CancellationToken cancellationToken) =>
        Ok(await files.ListAsync(purpose, ownerId, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        StoredFileDto? file = await files.GetAsync(id, cancellationToken);

        return file is null
            ? Failure<StoredFileDto>(MoteeStatusCodes.NotFound, "File not found.")
            : Ok(file);
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        FileContent? content = await files.OpenAsync(id, cancellationToken);

        if (content is null)
        {
            return Failure<object?>(MoteeStatusCodes.NotFound, "File not found.");
        }

        // Attachment rather than inline: an uploaded document should download, not
        // render in the tab under our own origin.
        return File(content.Content, content.ContentType, content.FileName);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await files.DeleteAsync(id, cancellationToken)
            ? Ok<object?>(null, "File deleted.")
            : Failure<object?>(MoteeStatusCodes.NotFound, "File not found.");

    // Above the largest purpose limit, so an oversized upload is refused by the
    // policy with a useful message rather than by the server dropping the request.
    private const int MaxRequestBytes = 25 * 1024 * 1024;

    private static string StatusFor(FileRejection rejection) => rejection switch
    {
        FileRejection.TooLarge => MoteeStatusCodes.PayloadTooLarge,

        // Nothing the caller sent was wrong — the request reached here without a
        // tenant, which is ours to fix, not theirs.
        FileRejection.NoTenant => MoteeStatusCodes.InternalServerError,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(FileRejection rejection) => rejection switch
    {
        FileRejection.Empty => "That file is empty.",
        FileRejection.TooLarge => "That file is larger than this upload allows.",
        FileRejection.DisallowedType => "That file type is not accepted here.",
        FileRejection.ContentDoesNotMatchType =>
            "That file's contents do not match its type.",
        FileRejection.UnknownPurpose => "Unknown upload purpose.",
        FileRejection.NoTenant => "Could not determine which company this upload belongs to.",
        _ => "Could not upload that file.",
    };
}
