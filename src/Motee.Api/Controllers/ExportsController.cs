using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Contracts;
using Motee.Application.Exports;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/exports")]
[Authorize]
public class ExportsController(IExportService exports) : ApiControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        ExportJobDto? job = await exports.GetAsync(id, cancellationToken);

        return job is null
            ? Failure<ExportJobDto>(MoteeStatusCodes.NotFound, "Export not found.")
            : Ok(job);
    }

    // The finished file is delivered by email as a signed link straight to storage.
    // This mints a fresh one for a caller who still has a session — the emailed link
    // outlives neither its own expiry nor a forwarded inbox, so an expired one should
    // not be a dead end.
    [HttpPost("{id:guid}/link")]
    public async Task<IActionResult> Link(Guid id, CancellationToken cancellationToken)
    {
        ExportLink? link = await exports.CreateLinkAsync(id, cancellationToken);

        return link is null
            ? Failure<ExportLink>(
                MoteeStatusCodes.NotFound,
                "That export is not available. It may still be running, or it may have expired.")
            : Ok(link);
    }
}
