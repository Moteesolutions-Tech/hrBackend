using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Authorization;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/users")]
public class UsersController(ITenantUserService users) : ApiControllerBase
{
    // The same module as the levels themselves. Seeing who holds what and changing who
    // holds what are one job done on one screen, and splitting the permission would let
    // someone assign a level to a person they are not allowed to see.
    private const string Module = "admin.access-levels";

    [HttpGet]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await users.ListAsync(cancellationToken));
}
