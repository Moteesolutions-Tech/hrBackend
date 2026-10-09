using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Audit;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/audit-trail")]
public class AuditTrailController(IAuditTrail audit) : ApiControllerBase
{
    private const string Module = "admin.audit-trail";

    // Read-only by design. There is no endpoint to edit or delete an entry, and there
    // should not be: a trail somebody can rewrite answers no question worth asking.
    // Removal happens by retention policy, applied to the whole table.
    [HttpGet]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] AuditQuery query,
        CancellationToken cancellationToken) =>
        Ok(await audit.ListAsync(query, cancellationToken));

    // What the action, module and status filters should offer. Served rather than
    // hard-coded in the client: the module list grows with every module that ships, and
    // one maintained in the frontend goes stale without anyone noticing — the symptom
    // being a filter that cannot find entries plainly visible in the table.
    [HttpGet("catalogue")]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Catalogue(CancellationToken cancellationToken) =>
        Ok(await audit.CatalogueAsync(cancellationToken));
}
