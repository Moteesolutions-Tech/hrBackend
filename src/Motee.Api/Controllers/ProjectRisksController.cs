using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Projects;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// The risk and issue register.
//
// An HRIS rollout touches employee data, payroll, access and documents, so this
// is the register the client named as the biggest gap in the module.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/projects/{projectId:guid}/risks")]
public class ProjectRisksController(IProjectRiskService risks) : ApiControllerBase
{
    private const string Module = "workspace.projects";

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ProjectRiskDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        Guid projectId,
        CancellationToken cancellationToken) =>
        Ok(await risks.ListAsync(projectId, cancellationToken));

    [HttpGet("tally")]
    [ProducesResponseType<RiskTally>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Tally(
        Guid projectId,
        CancellationToken cancellationToken) =>
        Ok(await risks.TallyAsync(projectId, cancellationToken));

    [HttpPost]
    [ProducesResponseType<ProjectRiskDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Add(
        Guid projectId,
        RiskRequest request,
        CancellationToken cancellationToken)
    {
        ProjectRiskResult result = await risks.AddAsync(projectId, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Risk!, "Added to the register.")
            : Failure<ProjectRiskDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{riskId:guid}")]
    [ProducesResponseType<ProjectRiskDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid projectId,
        Guid riskId,
        RiskRequest request,
        CancellationToken cancellationToken)
    {
        ProjectRiskResult result = await risks.UpdateAsync(
            projectId, riskId, request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Risk!, "Register updated.")
            : Failure<ProjectRiskDto>(StatusFor(result), MessageFor(result));
    }

    [HttpDelete("{riskId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(
        Guid projectId,
        Guid riskId,
        CancellationToken cancellationToken)
    {
        ProjectRiskResult result = await risks.DeleteAsync(
            projectId, riskId, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Removed from the register.")
            : Failure<object?>(StatusFor(result), MessageFor(result));
    }

    private static string StatusFor(ProjectRiskResult result) =>
        ProjectsController.StatusFor(result.Outcome, result.Refusal);

    private static string MessageFor(ProjectRiskResult result) =>
        ProjectsController.MessageFor(result.Outcome, result.Refusal);
}
