using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Projects;
using Motee.Domain.Authorization;
using Motee.Domain.Projects;

namespace Motee.Api.Controllers;

// Projects, their work, their dates and their people.
//
// The detail route returns the project with every derived figure already worked
// out — progress, the critical path, conflicting dates, health, what is due
// next and the money — so the eight panels on screen come from one call.
//
// Logged time, risks, documents and the go-live checklist have their own
// controllers under the same prefix.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/projects")]
public class ProjectsController(IProjectService projects) : ApiControllerBase
{
    private const string Module = "workspace.projects";

    [HttpGet]
    [ProducesResponseType<PagedResult<ProjectRowDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] ProjectQuery query,
        CancellationToken cancellationToken) =>
        Ok(await projects.ListAsync(query, cancellationToken));

    [HttpGet("summary")]
    [ProducesResponseType<ProjectSummaryDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await projects.SummaryAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        ProjectDto? found = await projects.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<ProjectDto>(MoteeStatusCodes.NotFound, "Project not found.")
            : Ok(found);
    }

    [HttpPost]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        ProjectRequest request,
        CancellationToken cancellationToken)
    {
        ProjectResult result = await projects.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Project!, "Project created.")
            : Failure<ProjectDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        ProjectRequest request,
        CancellationToken cancellationToken) =>
        Respond(await projects.UpdateAsync(id, request, cancellationToken), "Project updated.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        ProjectResult result = await projects.DeleteAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Project deleted.")
            : Failure<object?>(StatusFor(result), MessageFor(result));
    }

    // ── work ────────────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/tasks")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> AddTask(
        Guid id,
        ProjectTaskRequest request,
        CancellationToken cancellationToken)
    {
        ProjectResult result = await projects.AddTaskAsync(id, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Project!, "Task added.")
            : Failure<ProjectDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}/tasks/{taskId:guid}")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> UpdateTask(
        Guid id,
        Guid taskId,
        ProjectTaskRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await projects.UpdateTaskAsync(id, taskId, request, cancellationToken),
            "Task updated.");

    // The status on its own, with progress following it. Refused while the
    // task is waiting on something else, which is what makes the dependency
    // arrows on the Gantt mean anything.
    [HttpPatch("{id:guid}/tasks/{taskId:guid}/status")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetTaskStatus(
        Guid id,
        Guid taskId,
        TaskStatusRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await projects.SetTaskStatusAsync(id, taskId, request.Status, cancellationToken),
            "Task updated.");

    // Progress on its own, with the status following it.
    [HttpPatch("{id:guid}/tasks/{taskId:guid}/progress")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetTaskProgress(
        Guid id,
        Guid taskId,
        TaskProgressRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await projects.SetTaskProgressAsync(
                id, taskId, request.PercentComplete, cancellationToken),
            "Progress saved.");

    [HttpDelete("{id:guid}/tasks/{taskId:guid}")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> DeleteTask(
        Guid id,
        Guid taskId,
        CancellationToken cancellationToken) =>
        Respond(
            await projects.DeleteTaskAsync(id, taskId, cancellationToken), "Task deleted.");

    // ── dates ───────────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/milestones")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> AddMilestone(
        Guid id,
        MilestoneRequest request,
        CancellationToken cancellationToken)
    {
        ProjectResult result = await projects.AddMilestoneAsync(id, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Project!, "Milestone added.")
            : Failure<ProjectDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}/milestones/{milestoneId:guid}")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> UpdateMilestone(
        Guid id,
        Guid milestoneId,
        MilestoneRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await projects.UpdateMilestoneAsync(id, milestoneId, request, cancellationToken),
            "Milestone updated.");

    // Reached or not reached, in one click. Reaching it records the day it was
    // actually met.
    [HttpPost("{id:guid}/milestones/{milestoneId:guid}/toggle")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> ToggleMilestone(
        Guid id,
        Guid milestoneId,
        CancellationToken cancellationToken) =>
        Respond(
            await projects.ToggleMilestoneAsync(id, milestoneId, cancellationToken),
            "Milestone updated.");

    [HttpPut("{id:guid}/milestones/{milestoneId:guid}/approval")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Approve)]
    public async Task<IActionResult> SetMilestoneApproval(
        Guid id,
        Guid milestoneId,
        MilestoneApprovalRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await projects.SetMilestoneApprovalAsync(
                id, milestoneId, request.Approved, cancellationToken),
            request.Approved ? "Milestone signed off." : "Sign-off withdrawn.");

    [HttpDelete("{id:guid}/milestones/{milestoneId:guid}")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> DeleteMilestone(
        Guid id,
        Guid milestoneId,
        CancellationToken cancellationToken) =>
        Respond(
            await projects.DeleteMilestoneAsync(id, milestoneId, cancellationToken),
            "Milestone deleted.");

    // ── people ──────────────────────────────────────────────────────────────

    // One row per person per project: setting it twice moves the figure rather
    // than booking somebody twice.
    [HttpPut("{id:guid}/allocations")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetAllocation(
        Guid id,
        AllocationRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await projects.SetAllocationAsync(id, request, cancellationToken),
            "Allocation saved.");

    [HttpDelete("{id:guid}/allocations/{employeeId:guid}")]
    [ProducesResponseType<ProjectDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> RemoveAllocation(
        Guid id,
        Guid employeeId,
        CancellationToken cancellationToken) =>
        Respond(
            await projects.RemoveAllocationAsync(id, employeeId, cancellationToken),
            "Removed from the project.");

    private IActionResult Respond(ProjectResult result, string message) =>
        result.Succeeded
            ? Ok(result.Project!, message)
            : Failure<ProjectDto>(StatusFor(result), MessageFor(result));

    internal static string StatusFor(ProjectOutcome outcome, ProjectRefusal refusal) =>
        outcome switch
        {
            ProjectOutcome.NotFound or ProjectOutcome.EmployeeNotFound =>
                MoteeStatusCodes.NotFound,

            ProjectOutcome.CodeTaken => MoteeStatusCodes.Conflict,

            // Starting work that is waiting on something else is a state
            // problem, not a malformed request.
            ProjectOutcome.Refused when refusal == ProjectRefusal.Blocked =>
                MoteeStatusCodes.Conflict,

            _ => MoteeStatusCodes.InvalidRequest,
        };

    private static string StatusFor(ProjectResult result) =>
        StatusFor(result.Outcome, result.Refusal);

    private static string MessageFor(ProjectResult result) =>
        MessageFor(result.Outcome, result.Refusal);

    internal static string MessageFor(ProjectOutcome outcome, ProjectRefusal refusal) =>
        outcome switch
        {
            ProjectOutcome.NotFound => "Project not found.",
            ProjectOutcome.EmployeeNotFound => "That person does not work here.",
            ProjectOutcome.CodeTaken => "Another project already uses that code.",
            _ => ReasonFor(refusal),
        };

    internal static string ReasonFor(ProjectRefusal refusal) => refusal switch
    {
        ProjectRefusal.NameTooShort =>
            $"Give the project a name of at least {ProjectRules.ShortestName} characters.",
        ProjectRefusal.CodeRequired => "Give the project a code — it is how it is referenced.",
        ProjectRefusal.EndBeforeStart => "The end date is before the start date.",

        ProjectRefusal.TaskNameTooShort => "Give the task a name.",
        ProjectRefusal.TaskEndBeforeStart => "The task ends before it starts.",
        ProjectRefusal.PercentOutOfRange => "Progress has to be between 0 and 100 per cent.",
        ProjectRefusal.CompletedMustBeWhole =>
            "Something marked complete is at a hundred per cent. Set the progress instead, "
            + "and it will complete itself.",
        ProjectRefusal.WholeMustBeCompleted =>
            "A hundred per cent means complete. Use the status, and the progress will "
            + "follow.",
        ProjectRefusal.Blocked => "This is waiting on work that has not finished.",

        ProjectRefusal.MilestoneNameTooShort => "Give the milestone a name.",

        ProjectRefusal.AllocationOutOfRange =>
            "An allocation has to be between 1 and 100 per cent of somebody's time.",

        ProjectRefusal.NotAllocated => "Only somebody on the project can log time to it.",
        ProjectRefusal.HoursNotPositive => "Enter the hours worked.",
        ProjectRefusal.HoursBeyondADay =>
            $"That is more than {ProjectRules.MostHoursInADay:N0} hours in a day.",
        ProjectRefusal.DayBeyondADay =>
            "That would take this person past a full day once their other projects are "
            + "counted.",

        ProjectRefusal.RiskTitleTooShort => "Give it a title.",

        ProjectRefusal.UnknownTask => "That task is not on this project.",
        ProjectRefusal.UnknownCheck => "That check is not on the go-live list.",

        _ => "Could not save the project.",
    };
}

public sealed record TaskStatusRequest
{
    public required ProjectTaskStatus Status { get; init; }
}

public sealed record TaskProgressRequest
{
    public required int PercentComplete { get; init; }
}

public sealed record MilestoneApprovalRequest
{
    public required bool Approved { get; init; }
}
