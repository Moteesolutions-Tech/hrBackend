using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Projects;
using Motee.Domain.Authorization;
using Motee.Domain.Projects;

namespace Motee.Api.Controllers;

// Hours logged against a project.
//
// Not the weekly timesheet — that is attendance, under its own module. This is
// which project somebody's hours went to, and only the approved ones count
// towards spend.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/projects/{projectId:guid}/time")]
public class ProjectTimeController(IProjectTimeService time) : ApiControllerBase
{
    private const string Module = "workspace.projects";

    [HttpGet]
    [ProducesResponseType<PagedResult<TimeEntryDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        Guid projectId,
        [FromQuery] TimeEntryQuery query,
        CancellationToken cancellationToken) =>
        Ok(await time.ListAsync(query with { ProjectId = projectId }, cancellationToken));

    [HttpGet("tally")]
    [ProducesResponseType<TimeTally>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Tally(
        Guid projectId,
        CancellationToken cancellationToken) =>
        Ok(await time.TallyAsync(projectId, cancellationToken));

    [HttpPost]
    [ProducesResponseType<TimeEntryDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Log(
        Guid projectId,
        TimeEntryRequest request,
        CancellationToken cancellationToken)
    {
        TimeEntryResult result = await time.LogAsync(projectId, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Entry!, "Time logged.")
            : Failure<TimeEntryDto>(
                ProjectsController.StatusFor(result.Outcome, result.Refusal),
                ProjectsController.MessageFor(result.Outcome, result.Refusal));
    }

    // Approving is the moment the hours become a cost, so it needs the
    // approve action rather than plain edit.
    [HttpPatch("{entryId:guid}/status")]
    [ProducesResponseType<TimeEntryDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Approve)]
    public async Task<IActionResult> SetStatus(
        Guid projectId,
        Guid entryId,
        TimeEntryStatusRequest request,
        CancellationToken cancellationToken)
    {
        TimeEntryResult result = await time.SetStatusAsync(
            projectId, entryId, request.Status, cancellationToken);

        return result.Succeeded
            ? Ok(result.Entry!, MessageFor(request.Status))
            : Failure<TimeEntryDto>(
                ProjectsController.StatusFor(result.Outcome, result.Refusal),
                ProjectsController.MessageFor(result.Outcome, result.Refusal));
    }

    [HttpDelete("{entryId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(
        Guid projectId,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        TimeEntryResult result = await time.DeleteAsync(projectId, entryId, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Entry deleted.")
            : Failure<object?>(
                ProjectsController.StatusFor(result.Outcome, result.Refusal),
                ProjectsController.MessageFor(result.Outcome, result.Refusal));
    }

    private static string MessageFor(TimeEntryStatus status) => status switch
    {
        TimeEntryStatus.Approved => "Time approved.",
        TimeEntryStatus.Rejected => "Time sent back.",
        TimeEntryStatus.Submitted => "Time submitted.",
        _ => "Saved.",
    };
}

public sealed record TimeEntryStatusRequest
{
    public required TimeEntryStatus Status { get; init; }
}
