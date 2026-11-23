using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Workflows;
using Motee.Domain.Authorization;
using Motee.Domain.Workflows;

namespace Motee.Api.Controllers;

// Workflows that are running.
//
// The inbox is the exception to the module grant: work assigned to somebody is
// theirs to pick up, hand in and sign off whatever else they can see. A
// workflow task that only an HR admin could action would sit on an IT
// engineer's list forever.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/workflow-runs")]
public class WorkflowRunsController(IWorkflowRunService runs) : ApiControllerBase
{
    private const string Module = "workspace.workflows";

    [HttpGet]
    [ProducesResponseType<PagedResult<RunRowDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] RunQuery query,
        CancellationToken cancellationToken) =>
        Ok(await runs.ListAsync(query, cancellationToken));

    // Everything one person owes: their own work, what is theirs to sign off,
    // and what the run is waiting on from somebody else.
    [HttpGet("my-work")]
    [Authorize]
    [ProducesResponseType<MyWorkDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MyWork(CancellationToken cancellationToken) =>
        Ok(await runs.MyWorkAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize]
    [ProducesResponseType<RunDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        RunDto? found = await runs.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<RunDto>(MoteeStatusCodes.NotFound, "Run not found.")
            : Ok(found);
    }

    [HttpPost]
    [ProducesResponseType<RunDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Start(
        StartRunRequest request,
        CancellationToken cancellationToken)
    {
        RunResult result = await runs.StartAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Run!, "Workflow started.")
            : Failure<RunDto>(StatusFor(result), MessageFor(result));
    }

    // ── one task at a time ──────────────────────────────────────────────────

    [HttpPost("{id:guid}/tasks/{taskId:guid}/start")]
    [Authorize]
    [ProducesResponseType<RunDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StartTask(
        Guid id,
        Guid taskId,
        CancellationToken cancellationToken) =>
        Respond(await runs.StartTaskAsync(id, taskId, cancellationToken), "Task started.");

    // Handing work in. With no reviewer it completes outright — making somebody
    // approve their own work is how a checklist becomes theatre.
    [HttpPost("{id:guid}/tasks/{taskId:guid}/submit")]
    [Authorize]
    [ProducesResponseType<RunDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SubmitTask(
        Guid id,
        Guid taskId,
        TaskNoteRequest? request,
        CancellationToken cancellationToken) =>
        Respond(
            await runs.SubmitTaskAsync(id, taskId, request?.Note, cancellationToken),
            "Task handed in.");

    [HttpPost("{id:guid}/tasks/{taskId:guid}/approve")]
    [Authorize]
    [ProducesResponseType<RunDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ApproveTask(
        Guid id,
        Guid taskId,
        TaskNoteRequest? request,
        CancellationToken cancellationToken) =>
        Respond(
            await runs.ApproveTaskAsync(id, taskId, request?.Note, cancellationToken),
            "Task signed off.");

    [HttpPost("{id:guid}/tasks/{taskId:guid}/reject")]
    [Authorize]
    [ProducesResponseType<RunDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RejectTask(
        Guid id,
        Guid taskId,
        TaskNoteRequest? request,
        CancellationToken cancellationToken) =>
        Respond(
            await runs.RejectTaskAsync(id, taskId, request?.Note, cancellationToken),
            "Task sent back.");

    // It did not apply to this run after all.
    [HttpPost("{id:guid}/tasks/{taskId:guid}/skip")]
    [ProducesResponseType<RunDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SkipTask(
        Guid id,
        Guid taskId,
        TaskNoteRequest? request,
        CancellationToken cancellationToken) =>
        Respond(
            await runs.SkipTaskAsync(id, taskId, request?.Note, cancellationToken),
            "Task marked as not applying.");

    // Moving work to a different desk is the first control anybody reaches for,
    // and it changes whose queue it sits in — so it needs the module rather
    // than just being signed in.
    [HttpPost("{id:guid}/tasks/{taskId:guid}/reassign")]
    [ProducesResponseType<RunDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> ReassignTask(
        Guid id,
        Guid taskId,
        ReassignTaskRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await runs.ReassignTaskAsync(id, taskId, request.EmployeeId, cancellationToken),
            "Task reassigned.");

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<RunDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken) =>
        Respond(await runs.CancelAsync(id, cancellationToken), "Run cancelled.");

    private IActionResult Respond(RunResult result, string message) =>
        result.Succeeded
            ? Ok(result.Run!, message)
            : Failure<RunDto>(StatusFor(result), MessageFor(result));

    private static string StatusFor(RunResult result) =>
        WorkflowsController.StatusFor(result.Outcome, result.Refusal);

    private static string MessageFor(RunResult result) =>
        WorkflowsController.MessageFor(result.Outcome, result.Refusal, result.Reason);
}

public sealed record TaskNoteRequest
{
    public string? Note { get; init; }
}

public sealed record ReassignTaskRequest
{
    public required Guid EmployeeId { get; init; }
}
