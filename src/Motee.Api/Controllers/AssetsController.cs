using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Api.Contracts.Assets;
using Motee.Application.Assets;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Domain.Assets;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/assets")]
public class AssetsController(
    IAssetService assets,
    IValidator<AssetRequest> validator) : ApiControllerBase
{
    private const string Module = "operations.assets";

    [HttpGet]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] AssetFilters filters,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = PagedQuery.DefaultPageSize,
        CancellationToken cancellationToken = default) =>
        Ok(await assets.ListAsync(
            Query(filters) with { Page = page, PageSize = pageSize },
            cancellationToken));

    [HttpGet("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        AssetDto? found = await assets.GetAsync(id, cancellationToken);

        // The tenant filter already excludes other companies. This is the narrower
        // case: self-service reaches only what the caller is holding, so a colleague's
        // laptop cannot be read by guessing an id.
        bool reachable = found is not null
            && (GrantedScope().Kind != DataScopeKind.Self
                || found.AssignedToEmployeeId == ViewerEmployeeId());

        return reachable
            ? Ok(found!)
            : Failure<AssetDto>(MoteeStatusCodes.NotFound, "Asset not found.");
    }

    [HttpPost]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        AssetRequest request,
        CancellationToken cancellationToken)
    {
        ValidationResult validation = await validator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return Failure<AssetDto>(
                MoteeStatusCodes.InvalidRequest,
                string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        AssetResult result = await assets.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Asset!, "Asset created.")
            : Failure<AssetDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPut("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        AssetRequest request,
        CancellationToken cancellationToken)
    {
        ValidationResult validation = await validator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return Failure<AssetDto>(
                MoteeStatusCodes.InvalidRequest,
                string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        AssetResult result = await assets.UpdateAsync(id, request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Asset!, "Asset updated.")
            : Failure<AssetDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // Handing kit over is an act, not a side effect of correcting a serial number.
    [HttpPost("{id:guid}/assign")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Assign(
        Guid id,
        AssignAssetRequest request,
        CancellationToken cancellationToken)
    {
        AssetResult result = await assets.AssignAsync(id, request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Asset!, "Asset assigned.")
            : Failure<AssetDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPost("{id:guid}/return")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Return(Guid id, CancellationToken cancellationToken)
    {
        AssetResult result = await assets.ReturnAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok(result.Asset!, "Asset returned.")
            : Failure<AssetDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // Lost and Retired. There is no delete: an asset that existed stays on the books,
    // because "where did that laptop go" is exactly what an audit asks.
    [HttpPost("{id:guid}/status")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> ChangeStatus(
        Guid id,
        ChangeAssetStatusRequest request,
        CancellationToken cancellationToken)
    {
        AssetResult result = await assets.ChangeStatusAsync(id, request.Status, cancellationToken);

        return result.Succeeded
            ? Ok(result.Asset!, $"Asset marked {request.Status}.")
            : Failure<AssetDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    private AssetQuery Query(AssetFilters filters) => new()
    {
        Scope = GrantedScope(),
        ViewerEmployeeId = ViewerEmployeeId(),
        Search = filters.Search,
        Category = filters.Category,
        Status = filters.Status,
        AssignedToEmployeeId = filters.AssignedToEmployeeId,
    };

    private DataScope GrantedScope() =>
        HttpContext.Items[PermissionAuthorizationHandler.DataScopeItemKey] is DataScope scope
            ? scope
            : DataScope.Nothing;

    private Guid? ViewerEmployeeId() =>
        Guid.TryParse(User.FindFirst(MoteeClaimTypes.EmployeeId)?.Value, out Guid id) ? id : null;

    private static string StatusFor(AssetOutcome outcome) => outcome switch
    {
        AssetOutcome.NotFound => MoteeStatusCodes.NotFound,
        AssetOutcome.DuplicateTag or AssetOutcome.AlreadyAssigned
            or AssetOutcome.InvalidStatusChange => MoteeStatusCodes.Conflict,
        AssetOutcome.UnknownEmployee => MoteeStatusCodes.InvalidRequest,
        _ => MoteeStatusCodes.InternalServerError,
    };

    private static string MessageFor(AssetOutcome outcome) => outcome switch
    {
        AssetOutcome.NotFound => "Asset not found.",
        AssetOutcome.DuplicateTag => "That asset tag is already in use.",
        AssetOutcome.AlreadyAssigned =>
            "That asset is assigned to someone else. Return it before reassigning.",
        AssetOutcome.UnknownEmployee => "That employee does not exist.",
        AssetOutcome.InvalidStatusChange => "That is not a valid change for this asset.",
        _ => "Could not complete the request.",
    };
}
