using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Workflows;
using Motee.Domain.Authorization;
using Motee.Domain.Workflows;

namespace Motee.Api.Controllers;

// Workflow templates.
//
// The seeded recruitment, preboarding, onboarding and offboarding flows can be
// activated or archived but not rewritten or deleted: they are what the
// product's own screens fire.
//
// Runs have their own controller under /workflow-runs.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/workflows")]
public class WorkflowsController(IWorkflowService workflows) : ApiControllerBase
{
    private const string Module = "workspace.workflows";

    [HttpGet]
    [ProducesResponseType<PagedResult<WorkflowRowDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] WorkflowQuery query,
        CancellationToken cancellationToken) =>
        Ok(await workflows.ListAsync(query, cancellationToken));

    [HttpGet("summary")]
    [ProducesResponseType<WorkflowSummaryDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await workflows.SummaryAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<WorkflowDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        WorkflowDto? found = await workflows.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<WorkflowDto>(MoteeStatusCodes.NotFound, "Workflow not found.")
            : Ok(found);
    }

    [HttpPost]
    [ProducesResponseType<WorkflowDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        WorkflowRequest request,
        CancellationToken cancellationToken)
    {
        WorkflowResult result = await workflows.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Workflow!, "Workflow created.")
            : Failure<WorkflowDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<WorkflowDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        WorkflowRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await workflows.UpdateAsync(id, request, cancellationToken), "Workflow updated.");

    // Activate or archive without going through the whole builder. Allowed on a
    // seeded flow: a company that does not recruit should be able to switch
    // recruitment off.
    [HttpPatch("{id:guid}/status")]
    [ProducesResponseType<WorkflowDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetStatus(
        Guid id,
        WorkflowStatusRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await workflows.SetStatusAsync(id, request.Status, cancellationToken),
            MessageFor(request.Status));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        WorkflowResult result = await workflows.DeleteAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Workflow deleted.")
            : Failure<object?>(StatusFor(result), MessageFor(result));
    }

    private IActionResult Respond(WorkflowResult result, string message) =>
        result.Succeeded
            ? Ok(result.Workflow!, message)
            : Failure<WorkflowDto>(StatusFor(result), MessageFor(result));

    private static string MessageFor(WorkflowStatus status) => status switch
    {
        WorkflowStatus.Active => "Workflow activated.",
        WorkflowStatus.Archived => "Workflow archived.",
        _ => "Workflow moved back to draft.",
    };

    internal static string StatusFor(WorkflowOutcome outcome, WorkflowRefusal refusal) =>
        outcome switch
        {
            WorkflowOutcome.NotFound => MoteeStatusCodes.NotFound,

            WorkflowOutcome.EmployeeNotFound
                or WorkflowOutcome.AccessLevelNotFound
                or WorkflowOutcome.DepartmentNotFound => MoteeStatusCodes.NotFound,

            // Not a malformed request: one of the flows the product itself
            // drives.
            WorkflowOutcome.Seeded => MoteeStatusCodes.Forbidden,

            // A state problem rather than a bad payload.
            WorkflowOutcome.Refused when refusal == WorkflowRefusal.NotRunnable =>
                MoteeStatusCodes.Conflict,

            _ => MoteeStatusCodes.InvalidRequest,
        };

    private static string StatusFor(WorkflowResult result) =>
        StatusFor(result.Outcome, result.Refusal);

    private static string MessageFor(WorkflowResult result) =>
        MessageFor(result.Outcome, result.Refusal, null);

    internal static string MessageFor(
        WorkflowOutcome outcome,
        WorkflowRefusal refusal,
        string? reason) =>
        outcome switch
        {
            WorkflowOutcome.NotFound => "Workflow not found.",
            WorkflowOutcome.EmployeeNotFound => "That person does not work here.",
            WorkflowOutcome.AccessLevelNotFound => "That access level does not exist.",
            WorkflowOutcome.DepartmentNotFound => "That department does not exist.",
            WorkflowOutcome.Seeded =>
                "This is one of the workflows the product runs itself, so it cannot be "
                + "changed. Archive it instead, or copy it and edit the copy.",
            _ => reason ?? ReasonFor(refusal),
        };

    internal static string ReasonFor(WorkflowRefusal refusal) => refusal switch
    {
        WorkflowRefusal.TitleTooShort =>
            $"Give the workflow a title of at least {WorkflowRules.ShortestTitle} characters.",
        WorkflowRefusal.DepartmentRequired => "Choose a department for this workflow's scope.",
        WorkflowRefusal.NoTasks => "Add at least one task.",

        WorkflowRefusal.ScheduleRequired =>
            "Say when an automatic workflow should fire — on a date, or after an event.",
        WorkflowRefusal.ScheduleDateRequired => "Pick a date for the automatic trigger.",
        WorkflowRefusal.TriggerEventRequired => "Pick the event this workflow follows.",
        WorkflowRefusal.OffsetRequired => "Enter how long after the event it should fire.",

        WorkflowRefusal.TaskTitleRequired => "Every task needs a title.",
        WorkflowRefusal.AssigneeRequired => "Every task needs somebody to do it.",
        WorkflowRefusal.ReviewerRequired =>
            "A task set to be reviewed needs a reviewer, or it can be handed in and never "
            + "signed off.",

        WorkflowRefusal.DependencyComesLater =>
            "A task cannot wait on work that comes after it — the run would never start.",
        WorkflowRefusal.UnknownDependency => "A task is waiting on work that is not here.",

        WorkflowRefusal.NotRunnable => "Only an active workflow can be started.",
        WorkflowRefusal.UnknownWorkflow => "Workflow not found.",
        WorkflowRefusal.UnknownTask => "That task is not on this workflow.",
        WorkflowRefusal.NotYours => "That task cannot be moved.",

        _ => "Could not save the workflow.",
    };
}

public sealed record WorkflowStatusRequest
{
    public required WorkflowStatus Status { get; init; }
}
