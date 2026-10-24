using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Performance;
using Motee.Domain.Authorization;
using Motee.Domain.Performance;

namespace Motee.Api.Controllers;

// Goals, set by the person or for them.
//
// Both halves are real — somebody writes their own development goals, and a manager
// sets objectives on them — so the split here is the usual one: self-service takes no
// employee id, administrative routes name whose goal it is.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/performance/goals")]
public class PerformanceGoalsController(IPerformanceGoalService goals) : ApiControllerBase
{
    private const string Module = "talent.performance";

    [HttpGet("mine")]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<PerformanceGoalDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(
        [FromQuery] GoalQuery query,
        CancellationToken cancellationToken) =>
        Ok(await goals.MineAsync(query, cancellationToken));

    [HttpPost("mine")]
    [RequiresEmployee]
    [ProducesResponseType<PerformanceGoalDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateMine(
        GoalRequest request,
        CancellationToken cancellationToken)
    {
        GoalResult result = await goals.CreateMineAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Goal!, "Goal created.")
            : Failure<PerformanceGoalDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    // A check-in: where it has got to, and why. Self-service because it is their
    // goal — a manager moving somebody's number without saying so is how a goal
    // stops being theirs.
    [HttpPost("{id:guid}/progress")]
    [RequiresEmployee]
    [ProducesResponseType<PerformanceGoalDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RecordProgress(
        Guid id,
        ProgressUpdateRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await goals.RecordProgressAsync(id, request, cancellationToken), "Progress recorded.");

    // ── managing ────────────────────────────────────────────────────────────

    [HttpGet]
    [ProducesResponseType<PagedResult<PerformanceGoalDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] GoalQuery query,
        CancellationToken cancellationToken) =>
        Ok(await goals.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<PerformanceGoalDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        PerformanceGoalDto? goal = await goals.GetAsync(id, cancellationToken);

        return goal is null
            ? Failure<PerformanceGoalDto>(MoteeStatusCodes.NotFound, "Goal not found.")
            : Ok(goal);
    }

    [HttpPost("employees/{employeeId:guid}")]
    [ProducesResponseType<PerformanceGoalDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        Guid employeeId,
        GoalRequest request,
        CancellationToken cancellationToken)
    {
        GoalResult result = await goals.CreateAsync(employeeId, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Goal!, "Goal set.")
            : Failure<PerformanceGoalDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<PerformanceGoalDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        GoalRequest request,
        CancellationToken cancellationToken) =>
        Respond(await goals.UpdateAsync(id, request, cancellationToken), "Goal updated.");

    [HttpPost("{id:guid}/status")]
    [ProducesResponseType<PerformanceGoalDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> ChangeStatus(
        Guid id,
        ChangeGoalStatusRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await goals.ChangeStatusAsync(id, request.Status, cancellationToken),
            "Goal updated.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        GoalOutcome outcome = await goals.DeleteAsync(id, cancellationToken);

        return outcome == GoalOutcome.Succeeded
            ? Ok<object?>(null, "Goal deleted.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    private IActionResult Respond(GoalResult result, string message) =>
        result.Succeeded
            ? Ok(result.Goal!, message)
            : Failure<PerformanceGoalDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));

    private static string StatusFor(GoalOutcome outcome) => outcome switch
    {
        GoalOutcome.NotFound or GoalOutcome.EmployeeNotFound => MoteeStatusCodes.NotFound,
        GoalOutcome.AlreadyFinal => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(GoalOutcome outcome) => outcome switch
    {
        GoalOutcome.NotFound => "Goal not found.",
        GoalOutcome.EmployeeNotFound => "Employee not found.",
        GoalOutcome.TitleRequired => "A goal needs a title.",
        GoalOutcome.ProgressOutOfRange => "Progress runs from 0 to 100.",
        GoalOutcome.AlreadyFinal =>
            "This goal has been closed. Set a new one rather than reopening it.",
        _ => "Could not save the goal.",
    };
}

public sealed record ChangeGoalStatusRequest
{
    public required GoalStatus Status { get; init; }
}
