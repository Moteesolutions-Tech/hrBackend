using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Organisation;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/business-units")]
public class BusinessUnitsController(
    IBusinessUnitService businessUnits,
    IValidator<BusinessUnitRequest> validator) : ApiControllerBase
{
    // organization.structure rather than a module of its own: a business unit is the
    // shape of the organisation above departments, and the templates already grant this
    // to the people who would maintain it.
    private const string Module = "organization.structure";

    [HttpGet]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await businessUnits.ListAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        BusinessUnitDto? unit = await businessUnits.GetAsync(id, cancellationToken);

        return unit is null
            ? Failure<BusinessUnitDto>(MoteeStatusCodes.NotFound, "Business unit not found.")
            : Ok(unit);
    }

    [HttpPost]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        BusinessUnitRequest request,
        CancellationToken cancellationToken)
    {
        ValidationResult validation = await validator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return Failure<BusinessUnitDto>(
                MoteeStatusCodes.InvalidRequest,
                string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        BusinessUnitResult result = await businessUnits.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.BusinessUnit!, "Business unit created.")
            : Failure<BusinessUnitDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPut("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        BusinessUnitRequest request,
        CancellationToken cancellationToken)
    {
        ValidationResult validation = await validator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return Failure<BusinessUnitDto>(
                MoteeStatusCodes.InvalidRequest,
                string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        BusinessUnitResult result = await businessUnits.UpdateAsync(id, request, cancellationToken);

        return result.Succeeded
            ? Ok(result.BusinessUnit!, "Business unit updated.")
            : Failure<BusinessUnitDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpDelete("{id:guid}")]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        BusinessUnitOutcome outcome = await businessUnits.DeleteAsync(id, cancellationToken);

        return outcome == BusinessUnitOutcome.Succeeded
            ? Ok<object?>(null, "Business unit deleted.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    private static string StatusFor(BusinessUnitOutcome outcome) => outcome switch
    {
        BusinessUnitOutcome.NotFound => MoteeStatusCodes.NotFound,
        BusinessUnitOutcome.DuplicateName or BusinessUnitOutcome.HasDepartments =>
            MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InternalServerError,
    };

    private static string MessageFor(BusinessUnitOutcome outcome) => outcome switch
    {
        BusinessUnitOutcome.NotFound => "Business unit not found.",
        BusinessUnitOutcome.DuplicateName => "A business unit with that name already exists.",

        // Says what to do instead, because the alternative is not obvious: deactivating
        // keeps every access level scoped to it working while stopping new use.
        BusinessUnitOutcome.HasDepartments =>
            "This business unit still has departments. Move them first, or set it to inactive.",
        _ => "Could not save the business unit.",
    };
}
