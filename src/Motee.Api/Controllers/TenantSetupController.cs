using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Tenancy;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tenant/setup")]
public class TenantSetupController(
    ITenantSetupService setup,
    ICurrentTenant currentTenant,
    IValidator<TenantSetupRequest> validator) : ApiControllerBase
{
    // The wizard renders its dropdowns from here. Static reference data — identical
    // for every tenant — so it is cacheable and needs no tenant context.
    [HttpGet("options")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
    public IActionResult Options() => Ok(TenantSetupOptions.Current);

    // Opens the wizard as review-and-adjust: company name and country come from
    // registration, and everything else arrives as its documented default rather
    // than blank.
    [HttpGet]
    [RequiresPermission("admin.settings", PermissionAction.View)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return Failure<TenantSetupDto>(MoteeStatusCodes.Forbidden, "No tenant on this token.");
        }

        TenantSetupDto? result = await setup.GetAsync(tenantId, cancellationToken);

        return result is null
            ? Failure<TenantSetupDto>(MoteeStatusCodes.NotFound, "Tenant not found.")
            : Ok(result);
    }

    [HttpPut]
    [RequiresPermission("admin.settings", PermissionAction.Edit)]
    public async Task<IActionResult> Save(
        TenantSetupRequest request,
        CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return Failure<object?>(MoteeStatusCodes.Forbidden, "No tenant on this token.");
        }

        ValidationResult validation = await validator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return Failure<object?>(
                MoteeStatusCodes.InvalidRequest,
                string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        return await setup.SaveAsync(tenantId, request, cancellationToken)
            ? Ok<object?>(null, "Setup saved.")
            : Failure<object?>(MoteeStatusCodes.NotFound, "Tenant not found.");
    }

    [HttpPost("complete")]
    [RequiresPermission("admin.settings", PermissionAction.Edit)]
    public async Task<IActionResult> Complete(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return Failure<object?>(MoteeStatusCodes.Forbidden, "No tenant on this token.");
        }

        return await setup.CompleteAsync(tenantId, cancellationToken)
            ? Ok<object?>(null, "Setup complete.")
            : Failure<object?>(MoteeStatusCodes.NotFound, "Tenant not found.");
    }
}
