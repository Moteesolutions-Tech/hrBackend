using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Work;
using Motee.Domain.Work;

namespace Motee.Api.Controllers;

// Tasks — the assigned and personal list.
//
// Authorize rather than RequiresPermission, and deliberately: the frontend
// declares no permission module for tasks anywhere in permissions/modules.ts,
// and a task list is somebody's own work rather than an HR record. Every route
// resolves the signed-in person from the session, so there is no employee id
// to change and no "list everybody's tasks" to reach for.
//
// Who may put work on whose list is decided by the org chart — yourself, or
// somebody who reports to you.
[ApiController]
[ApiVersion("1.0")]
[Authorize]
[Route("api/v{version:apiVersion}/tasks")]
public class TasksController(ITaskService tasks) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<TaskItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(
        [FromQuery] TaskQuery query,
        CancellationToken cancellationToken) =>
        Ok(await tasks.MineAsync(query, cancellationToken));

    // What the caller has given out. A manager needs it to chase work, and the
    // client has no screen for it yet.
    [HttpGet("assigned-by-me")]
    [ProducesResponseType<PagedResult<TaskItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AssignedByMe(
        [FromQuery] TaskQuery query,
        CancellationToken cancellationToken) =>
        Ok(await tasks.AssignedByMeAsync(query, cancellationToken));

    [HttpGet("summary")]
    [ProducesResponseType<TaskTally>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await tasks.SummaryAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<TaskItemDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        TaskItemDto? found = await tasks.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<TaskItemDto>(MoteeStatusCodes.NotFound, "Task not found.")
            : Ok(found);
    }

    [HttpPost]
    [ProducesResponseType<TaskItemDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Add(
        TaskItemRequest request,
        CancellationToken cancellationToken)
    {
        TaskResult result = await tasks.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Task!, "Task added.")
            : Failure<TaskItemDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<TaskItemDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        Guid id,
        TaskItemRequest request,
        CancellationToken cancellationToken) =>
        Respond(await tasks.UpdateAsync(id, request, cancellationToken), "Task saved.");

    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType<TaskItemDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Complete(
        Guid id,
        CompleteTaskRequest? request,
        CancellationToken cancellationToken) =>
        Respond(
            await tasks.CompleteAsync(id, request?.CompletedOn, cancellationToken),
            "Task done.");

    [HttpDelete("{id:guid}/complete")]
    [ProducesResponseType<TaskItemDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reopen(Guid id, CancellationToken cancellationToken) =>
        Respond(await tasks.ReopenAsync(id, cancellationToken), "Task reopened.");

    // The whole order the list was dragged into. Ids that are not the caller's
    // own are ignored rather than refused, so one stale id does not throw the
    // reorder away.
    [HttpPost("reorder")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reorder(
        ReorderTasksRequest request,
        CancellationToken cancellationToken)
    {
        TaskResult result = await tasks.ReorderAsync(
            request.OrderedIds ?? [], cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "List reordered.")
            : Failure<object?>(StatusFor(result), MessageFor(result));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        TaskResult result = await tasks.DeleteAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Task deleted.")
            : Failure<object?>(StatusFor(result), MessageFor(result));
    }

    private IActionResult Respond(TaskResult result, string message) =>
        result.Succeeded
            ? Ok(result.Task!, message)
            : Failure<TaskItemDto>(StatusFor(result), MessageFor(result));

    private static string StatusFor(TaskResult result) => result.Outcome switch
    {
        TaskOutcome.NotFound or TaskOutcome.EmployeeNotFound => MoteeStatusCodes.NotFound,
        TaskOutcome.Forbidden => MoteeStatusCodes.Forbidden,
        TaskOutcome.NoEmployeeRecord => MoteeStatusCodes.Forbidden,

        TaskOutcome.Refused when result.Refusal == TaskRefusal.NotYoursToAssign =>
            MoteeStatusCodes.Forbidden,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(TaskResult result) => result.Outcome switch
    {
        TaskOutcome.NotFound => "Task not found.",
        TaskOutcome.EmployeeNotFound => "That person does not work here.",
        TaskOutcome.Forbidden => "This task is not yours.",
        TaskOutcome.NoEmployeeRecord =>
            "Your account has no employee record, so it has no task list.",
        _ => ReasonFor(result.Refusal),
    };

    private static string ReasonFor(TaskRefusal refusal) => refusal switch
    {
        TaskRefusal.TitleRequired => "Say what needs doing.",
        TaskRefusal.TitleTooLong =>
            $"Keep the task to {TaskItems.LongestTitle} characters or fewer.",
        TaskRefusal.NotesTooLong =>
            $"Keep the notes to {TaskItems.LongestNotes} characters or fewer.",
        TaskRefusal.AssigneeRequired => "Say whose task this is.",
        TaskRefusal.NotYoursToAssign =>
            "You can add tasks for yourself and for people who report to you.",
        _ => "Could not save the task.",
    };
}

public sealed record CompleteTaskRequest
{
    // Omitted means today. Set when ticking off something that was finished
    // earlier.
    public DateOnly? CompletedOn { get; init; }
}

public sealed record ReorderTasksRequest
{
    public IReadOnlyList<Guid>? OrderedIds { get; init; }
}
