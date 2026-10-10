using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Approvals;
using Motee.Domain.Approvals;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/approval-templates")]
public class ApprovalTemplatesController(IApprovalTemplateService templates) : ApiControllerBase
{
    // Configuring who signs off what is an administrative act, not part of running the
    // approvals themselves — so it sits behind the workflows module rather than the
    // queue one.
    private const string Module = "submissions.workflows";

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ApprovalTemplateDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] string? documentType,
        CancellationToken cancellationToken) =>
        Ok(await templates.ListAsync(documentType, cancellationToken));

    // The categories a chain can be built for, and the approver rules available. Served
    // rather than hard-coded in the client: the resolver list grows a phase at a time,
    // and an option offered before its resolver exists is one the backend cannot honour.
    [HttpGet("catalogue")]
    [ProducesResponseType<IReadOnlyList<ApprovalRoleDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Catalogue(CancellationToken cancellationToken) =>
        Ok<object>(new
        {
            documentTypes = ApprovalDocumentTypes.BuiltIn,
            approvers = Enum.GetNames<ApproverResolver>(),

            // The access levels a Role step can name. Served alongside the resolvers
            // because picking "Role" is only half a choice — the client would otherwise
            // have to fetch the access levels screen's data to complete it.
            roles = await templates.RolesAsync(cancellationToken),
        });

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ApprovalTemplateDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        ApprovalTemplateDto? template = await templates.GetAsync(id, cancellationToken);

        return template is null
            ? Failure<ApprovalTemplateDto>(MoteeStatusCodes.NotFound, "Approval template not found.")
            : Ok(template);
    }

    [HttpPost]
    [ProducesResponseType<ApprovalTemplateDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        ApprovalTemplateRequest request,
        CancellationToken cancellationToken) =>
        Respond(await templates.CreateAsync(request, cancellationToken), created: true);

    [HttpPut("{id:guid}")]
    [ProducesResponseType<ApprovalTemplateDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        ApprovalTemplateRequest request,
        CancellationToken cancellationToken) =>
        Respond(await templates.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        ApprovalTemplateOutcome outcome = await templates.DeleteAsync(id, cancellationToken);

        return outcome == ApprovalTemplateOutcome.Succeeded
            ? Ok<object?>(null, "Approval template removed.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    private IActionResult Respond(ApprovalTemplateResult result, bool created = false)
    {
        if (!result.Succeeded)
        {
            return Failure<ApprovalTemplateDto>(
                StatusFor(result.Outcome), MessageFor(result.Outcome));
        }

        return created
            ? CreatedEnvelope(result.Template!, "Approval template created.")
            : Ok(result.Template!, "Approval template updated.");
    }

    private static string StatusFor(ApprovalTemplateOutcome outcome) => outcome switch
    {
        ApprovalTemplateOutcome.NotFound => MoteeStatusCodes.NotFound,
        ApprovalTemplateOutcome.DuplicateName or ApprovalTemplateOutcome.InUse =>
            MoteeStatusCodes.Conflict,
        ApprovalTemplateOutcome.SystemTemplate => MoteeStatusCodes.Forbidden,
        ApprovalTemplateOutcome.NoSteps or ApprovalTemplateOutcome.RoleMissing =>
            MoteeStatusCodes.InvalidRequest,
        _ => MoteeStatusCodes.InternalServerError,
    };

    private static string MessageFor(ApprovalTemplateOutcome outcome) => outcome switch
    {
        ApprovalTemplateOutcome.NotFound => "Approval template not found.",
        ApprovalTemplateOutcome.DuplicateName =>
            "A template with that name already exists for this document type.",

        // Says what to do instead, because the alternative is not obvious.
        ApprovalTemplateOutcome.SystemTemplate =>
            "Built-in templates cannot be changed. Copy it and edit the copy.",
        ApprovalTemplateOutcome.InUse =>
            "Approvals have run against this template, so it cannot be deleted. "
            + "Deactivate it instead.",
        ApprovalTemplateOutcome.NoSteps => "An approval chain needs at least one step.",
        ApprovalTemplateOutcome.RoleMissing =>
            "A step set to Role must name an access level that exists.",
        _ => "Could not save the approval template.",
    };
}
