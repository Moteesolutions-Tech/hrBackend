using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Organisation;
using Motee.Domain.Authorization;
using Motee.Domain.Organisation;

namespace Motee.Api.Controllers;

// Employment types and their configuration.
//
// Deactivate rather than delete is the rule, and the two are separate routes
// because they refuse for different reasons. Retiring a type is refused while
// people are employed under it; deleting one is refused outright for the
// eleven kinds the product recognises, because recruitment, onboarding and
// the reports all map onto them.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/employment-types")]
public class EmploymentTypesController(IEmploymentTypeService types) : ApiControllerBase
{
    private const string Module = "organization.employment-types";

    // Inactive ones included by default: the screen that reactivates a
    // retired type has to be able to see it.
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<EmploymentTypeDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] bool activeOnly,
        CancellationToken cancellationToken) =>
        Ok(await types.ListAsync(!activeOnly, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<EmploymentTypeDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        EmploymentTypeDto? found = await types.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<EmploymentTypeDto>(MoteeStatusCodes.NotFound, "Employment type not found.")
            : Ok(found);
    }

    [HttpPost]
    [ProducesResponseType<EmploymentTypeDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        EmploymentTypeRequest request,
        CancellationToken cancellationToken)
    {
        EmploymentTypeResult result = await types.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Type!, "Employment type added.")
            : Failure<EmploymentTypeDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<EmploymentTypeDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        EmploymentTypeRequest request,
        CancellationToken cancellationToken) =>
        Respond(await types.UpdateAsync(id, request, cancellationToken), "Employment type saved.");

    [HttpPost("{id:guid}/deactivate")]
    [ProducesResponseType<EmploymentTypeDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken) =>
        Respond(await types.DeactivateAsync(id, cancellationToken), "Employment type retired.");

    [HttpPost("{id:guid}/reactivate")]
    [ProducesResponseType<EmploymentTypeDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken cancellationToken) =>
        Respond(await types.ReactivateAsync(id, cancellationToken), "Employment type restored.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await types.DeleteAsync(id, cancellationToken) switch
        {
            EmploymentTypeOutcome.Succeeded => Ok<object?>(null, "Employment type deleted."),
            EmploymentTypeOutcome.NotFound =>
                Failure<object?>(MoteeStatusCodes.NotFound, "Employment type not found."),
            _ => Failure<object?>(
                MoteeStatusCodes.Conflict,
                "This is one of the built-in types, or people are employed under it. "
                + "Retire it instead."),
        };

    private IActionResult Respond(EmploymentTypeResult result, string message) =>
        result.Succeeded
            ? Ok(result.Type!, message)
            : Failure<EmploymentTypeDto>(StatusFor(result), MessageFor(result));

    private static string StatusFor(EmploymentTypeResult result) => result.Outcome switch
    {
        EmploymentTypeOutcome.NotFound => MoteeStatusCodes.NotFound,

        EmploymentTypeOutcome.Refused when result.Refusal
            is EmploymentTypeRefusal.NameTaken
            or EmploymentTypeRefusal.InUse
            or EmploymentTypeRefusal.BuiltInCannotBeDeleted => MoteeStatusCodes.Conflict,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(EmploymentTypeResult result) => result.Outcome switch
    {
        EmploymentTypeOutcome.NotFound => "Employment type not found.",
        _ => ReasonFor(result.Refusal),
    };

    // Sentences, because they are shown as written.
    private static string ReasonFor(EmploymentTypeRefusal refusal) => refusal switch
    {
        EmploymentTypeRefusal.NameRequired => "Give the employment type a name.",
        EmploymentTypeRefusal.NameTaken =>
            "There is already a type with that name.",
        EmploymentTypeRefusal.LeaveDaysNegative =>
            "Leave entitlement cannot be negative. Leave it blank if nobody has set one.",
        EmploymentTypeRefusal.ProbationNegative => "Probation cannot be negative.",
        EmploymentTypeRefusal.ProbationTooLong =>
            "That probation runs over two years — check whether months were meant.",
        EmploymentTypeRefusal.HoursOutOfRange =>
            "Hours a week has to fall inside a week. Leave it blank for an "
            + "arrangement with no fixed hours.",
        EmploymentTypeRefusal.PensionOutOfRange =>
            "A pension contribution is a percentage, so it runs from 0 to 100.",
        EmploymentTypeRefusal.InUse =>
            "People are employed under this type. Move them first, then retire it.",
        EmploymentTypeRefusal.BuiltInCannotBeDeleted =>
            "This is one of the built-in types. Retire it instead of deleting it.",
        _ => "Could not save the employment type.",
    };
}
