using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Projects;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Go-live readiness: a fixed set of checks across five areas, rolling up into
// one score.
//
// The checks are a template in the domain and only the ticks are stored, so a
// project nobody has opened still returns all twenty-five checks rather than an
// empty list.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/projects/{projectId:guid}/go-live")]
public class ProjectGoLiveController(IProjectGoLiveService goLive) : ApiControllerBase
{
    private const string Module = "workspace.projects";

    [HttpGet]
    [ProducesResponseType<GoLiveChecklistDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        GoLiveChecklistDto? checklist = await goLive.GetAsync(projectId, cancellationToken);

        return checklist is null
            ? Failure<GoLiveChecklistDto>(MoteeStatusCodes.NotFound, "Project not found.")
            : Ok(checklist);
    }

    [HttpPut("{itemKey}")]
    [ProducesResponseType<GoLiveChecklistDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Set(
        Guid projectId,
        string itemKey,
        GoLiveTickRequest request,
        CancellationToken cancellationToken)
    {
        GoLiveResult result = await goLive.SetAsync(
            projectId, itemKey, request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Checklist!, "Saved.")
            : Failure<GoLiveChecklistDto>(
                ProjectsController.StatusFor(result.Outcome, result.Refusal),
                ProjectsController.MessageFor(result.Outcome, result.Refusal));
    }
}
