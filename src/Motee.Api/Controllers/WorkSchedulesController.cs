using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Attendance;
using Motee.Application.Common;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// The contracted hours a company runs, and who is on which.
//
// Behind the attendance module rather than employees, because this is what attendance
// judges against — "late", "overtime" and "absent" have no meaning without it.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/attendance/schedules")]
public class WorkSchedulesController(
    IWorkScheduleService schedules,
    ICurrentEmployee currentEmployee) : ApiControllerBase
{
    private const string Module = "time-payroll.attendance";

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<WorkScheduleDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await schedules.ListAsync(cancellationToken));

    // Your own hours, without the attendance module.
    //
    // No permission attribute, for the same reason as the rest of self-service:
    // needing the module to see what time you start would mean granting every member
    // of staff the screen that configures the whole company's shifts.
    [HttpGet("mine")]
    [RequiresEmployee]
    [ProducesResponseType<WorkScheduleDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken)
    {
        // Non-null: the filter above turned the alternative away.
        Guid employeeId = (await currentEmployee.IdAsync(cancellationToken))!.Value;

        WorkScheduleDto? schedule = await schedules.EffectiveForAsync(employeeId, cancellationToken);

        return schedule is null
            ? Failure<WorkScheduleDto>(
                MoteeStatusCodes.NotFound, "No working hours are configured.")
            : Ok(schedule);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<WorkScheduleDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        WorkScheduleDto? schedule = await schedules.GetAsync(id, cancellationToken);

        return schedule is null
            ? Failure<WorkScheduleDto>(MoteeStatusCodes.NotFound, "Work schedule not found.")
            : Ok(schedule);
    }

    // The hours one person is held to, resolved — their own, or the company default.
    // The one question attendance asks for every judgement it makes.
    [HttpGet("for/{employeeId:guid}")]
    [ProducesResponseType<WorkScheduleDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> For(Guid employeeId, CancellationToken cancellationToken)
    {
        WorkScheduleDto? schedule = await schedules.EffectiveForAsync(employeeId, cancellationToken);

        return schedule is null
            ? Failure<WorkScheduleDto>(
                MoteeStatusCodes.NotFound,
                "No working hours could be resolved for that employee.")
            : Ok(schedule);
    }

    [HttpPost]
    [ProducesResponseType<WorkScheduleDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        WorkScheduleRequest request,
        CancellationToken cancellationToken)
    {
        WorkScheduleResult result = await schedules.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Schedule!, "Work schedule created.")
            : Failure<WorkScheduleDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<WorkScheduleDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        WorkScheduleRequest request,
        CancellationToken cancellationToken)
    {
        WorkScheduleResult result = await schedules.UpdateAsync(id, request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Schedule!, "Work schedule updated.")
            : Failure<WorkScheduleDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPost("{id:guid}/default")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> MakeDefault(Guid id, CancellationToken cancellationToken)
    {
        WorkScheduleOutcome outcome = await schedules.MakeDefaultAsync(id, cancellationToken);

        return outcome == WorkScheduleOutcome.Succeeded
            ? Ok<object?>(null, "Default working hours changed.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    [HttpPut("employees/{employeeId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Assign(
        Guid employeeId,
        AssignWorkScheduleRequest request,
        CancellationToken cancellationToken)
    {
        WorkScheduleOutcome outcome =
            await schedules.AssignAsync(employeeId, request.ScheduleId, cancellationToken);

        return outcome == WorkScheduleOutcome.Succeeded
            ? Ok<object?>(null, "Working hours assigned.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        WorkScheduleOutcome outcome = await schedules.DeleteAsync(id, cancellationToken);

        return outcome == WorkScheduleOutcome.Succeeded
            ? Ok<object?>(null, "Work schedule deleted.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    private static string StatusFor(WorkScheduleOutcome outcome) => outcome switch
    {
        WorkScheduleOutcome.NotFound or WorkScheduleOutcome.EmployeeNotFound =>
            MoteeStatusCodes.NotFound,
        WorkScheduleOutcome.DuplicateName or WorkScheduleOutcome.InUse
            or WorkScheduleOutcome.IsDefault => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(WorkScheduleOutcome outcome) => outcome switch
    {
        WorkScheduleOutcome.NotFound => "Work schedule not found.",
        WorkScheduleOutcome.EmployeeNotFound => "Employee not found.",
        WorkScheduleOutcome.DuplicateName => "A work schedule with that name already exists.",
        WorkScheduleOutcome.NoWorkingDays =>
            "A work schedule needs at least one working day.",
        WorkScheduleOutcome.InvalidHours =>
            "Start and end times must differ, and the grace period cannot be negative.",
        WorkScheduleOutcome.BreakLongerThanShift =>
            "The break is longer than the shift it is taken from, which leaves no paid hours.",

        // Says what to do, because the obvious next click is the one that is refused.
        WorkScheduleOutcome.InUse =>
            "People are on this schedule. Move them to another one before deleting it — "
            + "deleting it would change what counts as late for them.",
        WorkScheduleOutcome.IsDefault =>
            "This is the default schedule and catches anybody hired without a choice. "
            + "Make another one the default first.",
        _ => "Could not save the work schedule.",
    };
}

public sealed record AssignWorkScheduleRequest
{
    // Null puts them back on the company default, which is a choice rather than an
    // omission — it is how somebody comes off a night shift.
    public Guid? ScheduleId { get; init; }
}
