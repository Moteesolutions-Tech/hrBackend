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
[Route("api/v{version:apiVersion}/departments")]
public class DepartmentsController(
    IDepartmentService departments,
    IValidator<DepartmentRequest> validator) : ApiControllerBase
{
    [HttpGet]
    [RequiresPermission("organization.departments", PermissionAction.View)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await departments.ListAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [RequiresPermission("organization.departments", PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        DepartmentDto? department = await departments.GetAsync(id, cancellationToken);

        return department is null
            ? Failure<DepartmentDto>(MoteeStatusCodes.NotFound, "Department not found.")
            : Ok(department);
    }

    [HttpPost]
    [RequiresPermission("organization.departments", PermissionAction.Create)]
    public async Task<IActionResult> Create(
        DepartmentRequest request,
        CancellationToken cancellationToken)
    {
        ValidationResult validation = await validator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return Failure<DepartmentDto>(
                MoteeStatusCodes.InvalidRequest,
                string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        DepartmentResult result = await departments.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Department!, "Department created.")
            : Failure<DepartmentDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPut("{id:guid}")]
    [RequiresPermission("organization.departments", PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        DepartmentRequest request,
        CancellationToken cancellationToken)
    {
        ValidationResult validation = await validator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return Failure<DepartmentDto>(
                MoteeStatusCodes.InvalidRequest,
                string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        DepartmentResult result = await departments.UpdateAsync(id, request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Department!, "Department updated.")
            : Failure<DepartmentDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpDelete("{id:guid}")]
    [RequiresPermission("organization.departments", PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        DepartmentOutcome outcome = await departments.DeleteAsync(id, cancellationToken);

        return outcome == DepartmentOutcome.Succeeded
            ? Ok<object?>(null, "Department deleted.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    private static string StatusFor(DepartmentOutcome outcome) => outcome switch
    {
        DepartmentOutcome.NotFound => MoteeStatusCodes.NotFound,
        DepartmentOutcome.DuplicateName or DepartmentOutcome.DuplicateCode => MoteeStatusCodes.Conflict,
        DepartmentOutcome.HasEmployees => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InternalServerError,
    };

    private static string MessageFor(DepartmentOutcome outcome) => outcome switch
    {
        DepartmentOutcome.NotFound => "Department not found.",
        DepartmentOutcome.DuplicateName => "A department with that name already exists.",
        DepartmentOutcome.DuplicateCode => "A department with that code already exists.",
        DepartmentOutcome.HasEmployees =>
            "This department still has employees. Move them first, or set it to inactive.",
        _ => "Could not save the department.",
    };
}
