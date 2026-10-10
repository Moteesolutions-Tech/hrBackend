using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Platform;
using Motee.Domain.Platform;

namespace Motee.Api.Controllers;

// Motee's own console: the companies, not the people inside them.
//
// Routed under /platform rather than mixed into the tenant API so the boundary is visible
// in the URL. Every action here is guarded by RequiresPlatform, which reads the platform
// role claim — a tenant's own access levels grant nothing on this controller, however
// complete they are.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/platform")]
public class PlatformController(IPlatformService platform) : ApiControllerBase
{
    [HttpGet("tenants")]
    [ProducesResponseType<PagedResult<PlatformTenantDto>>(StatusCodes.Status200OK)]
    [RequiresPlatform(PlatformPermissions.Tenants)]
    public async Task<IActionResult> Tenants(
        [FromQuery] PagedQuery query,
        CancellationToken cancellationToken) =>
        Ok(await platform.ListTenantsAsync(query, cancellationToken));

    [HttpGet("tenants/{id:guid}")]
    [ProducesResponseType<PlatformTenantDto>(StatusCodes.Status200OK)]
    [RequiresPlatform(PlatformPermissions.Tenants)]
    public async Task<IActionResult> Tenant(Guid id, CancellationToken cancellationToken)
    {
        PlatformTenantDto? tenant = await platform.GetTenantAsync(id, cancellationToken);

        return tenant is null
            ? Failure<PlatformTenantDto>(MoteeStatusCodes.NotFound, "Tenant not found.")
            : Ok(tenant);
    }

    [HttpGet("staff")]
    [ProducesResponseType<IReadOnlyList<PlatformStaffDto>>(StatusCodes.Status200OK)]
    [RequiresPlatform(PlatformPermissions.Staff)]
    public async Task<IActionResult> Staff(CancellationToken cancellationToken) =>
        Ok(await platform.ListStaffAsync(cancellationToken));

    // Promotes an existing account. There is deliberately no endpoint that creates one:
    // an operator signs up like anyone else, verifies their address and sets their own
    // password, and is then granted a role here.
    [HttpPost("staff")]
    [ProducesResponseType<PlatformStaffDto>(StatusCodes.Status200OK)]
    [RequiresPlatform(PlatformPermissions.Staff, write: true)]
    public async Task<IActionResult> Grant(
        GrantPlatformRoleRequest request,
        CancellationToken cancellationToken)
    {
        PlatformStaffResult result = await platform.GrantAsync(
            request.Email, request.Role, cancellationToken);

        return result.Succeeded
            ? Ok(result.Staff!, "Platform role granted.")
            : Failure<PlatformStaffDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpDelete("staff/{userId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPlatform(PlatformPermissions.Staff, write: true)]
    public async Task<IActionResult> Revoke(Guid userId, CancellationToken cancellationToken)
    {
        PlatformStaffOutcome outcome = await platform.RevokeAsync(userId, cancellationToken);

        return outcome == PlatformStaffOutcome.Succeeded
            ? Ok<object?>(null, "Platform role removed.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    private static string StatusFor(PlatformStaffOutcome outcome) => outcome switch
    {
        PlatformStaffOutcome.NotFound => MoteeStatusCodes.NotFound,
        PlatformStaffOutcome.LastAdministrator => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(PlatformStaffOutcome outcome) => outcome switch
    {
        PlatformStaffOutcome.NotFound => "No such account.",
        PlatformStaffOutcome.BelongsToTenant =>
            "That account belongs to a company. Platform staff must not be a tenant user.",
        PlatformStaffOutcome.LastAdministrator =>
            "That is the last platform administrator. Grant the role to somebody else first.",
        _ => "Could not change the platform role.",
    };
}

public sealed record GrantPlatformRoleRequest
{
    public required string Email { get; init; }

    public required PlatformRole Role { get; init; }
}
