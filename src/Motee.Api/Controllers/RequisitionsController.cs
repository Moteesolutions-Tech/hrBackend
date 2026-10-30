using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Recruitment;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Vacancies.
//
// Publishing is two calls rather than one: /warnings says everything that
// ought to be sorted out, and /publish refuses only on the things that would
// waste an applicant's time. Rolling them together would mean revealing the
// job one refusal at a time.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/requisitions")]
public class RequisitionsController(IRequisitionService requisitions) : ApiControllerBase
{
    private const string Module = "talent.recruitment";

    [HttpGet]
    [ProducesResponseType<PagedResult<RequisitionDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] RequisitionQuery query,
        CancellationToken cancellationToken) =>
        Ok(await requisitions.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<RequisitionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        RequisitionDto? requisition = await requisitions.GetAsync(id, cancellationToken);

        return requisition is null
            ? Failure<RequisitionDto>(MoteeStatusCodes.NotFound, "Vacancy not found.")
            : Ok(requisition);
    }

    // Everything worth fixing before it goes live, blocking and advisory
    // together, so the screen shows the whole job at once.
    [HttpGet("{id:guid}/warnings")]
    [ProducesResponseType<IReadOnlyList<PublishWarningDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Warnings(Guid id, CancellationToken cancellationToken) =>
        Ok(await requisitions.WarningsAsync(id, cancellationToken));

    [HttpPost]
    [ProducesResponseType<RequisitionDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        RequisitionRequest request,
        CancellationToken cancellationToken)
    {
        RequisitionResult result = await requisitions.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Requisition!, "Vacancy drafted.")
            : Failure<RequisitionDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<RequisitionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        RequisitionRequest request,
        CancellationToken cancellationToken) =>
        Respond(await requisitions.UpdateAsync(id, request, cancellationToken), "Vacancy updated.");

    [HttpPost("{id:guid}/publish")]
    [ProducesResponseType<RequisitionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Publish(Guid id, CancellationToken cancellationToken) =>
        Respond(await requisitions.PublishAsync(id, cancellationToken), "Vacancy published.");

    [HttpPost("{id:guid}/hold")]
    [ProducesResponseType<RequisitionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Hold(Guid id, CancellationToken cancellationToken) =>
        Respond(await requisitions.HoldAsync(id, cancellationToken), "Vacancy on hold.");

    [HttpPost("{id:guid}/resume")]
    [ProducesResponseType<RequisitionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Resume(Guid id, CancellationToken cancellationToken) =>
        Respond(await requisitions.ResumeAsync(id, cancellationToken), "Vacancy reopened.");

    [HttpPost("{id:guid}/close")]
    [ProducesResponseType<RequisitionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Close(
        Guid id,
        [FromBody] CloseRequisitionRequest? request,
        CancellationToken cancellationToken) =>
        Respond(
            await requisitions.CloseAsync(id, request?.Reason, cancellationToken),
            "Vacancy closed.");

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<RequisitionDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Cancel(
        Guid id,
        [FromBody] CloseRequisitionRequest? request,
        CancellationToken cancellationToken) =>
        Respond(
            await requisitions.CancelAsync(id, request?.Reason, cancellationToken),
            "Vacancy cancelled.");

    // Refused once anybody has applied. Closing keeps the funnel; deleting
    // would take the applicants with it.
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        RequisitionOutcome outcome = await requisitions.DeleteAsync(id, cancellationToken);

        return outcome == RequisitionOutcome.Succeeded
            ? Ok<object?>(null, "Vacancy deleted.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    private IActionResult Respond(RequisitionResult result, string message) =>
        result.Succeeded
            ? Ok(result.Requisition!, message)
            : Failure<RequisitionDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));

    private static string StatusFor(RequisitionOutcome outcome) => outcome switch
    {
        RequisitionOutcome.NotFound => MoteeStatusCodes.NotFound,
        RequisitionOutcome.NotInThatState or RequisitionOutcome.HasCandidates =>
            MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(RequisitionOutcome outcome) => outcome switch
    {
        RequisitionOutcome.NotFound => "Vacancy not found.",
        RequisitionOutcome.TitleRequired => "Give the vacancy a title.",
        RequisitionOutcome.HiringManagerRequired =>
            "Name a hiring manager — somebody has to own this hire.",
        RequisitionOutcome.JobDescriptionRequired =>
            "Write a job description. Applicants have nothing to read without one.",
        RequisitionOutcome.OpeningsNotPositive => "A vacancy needs at least one opening.",
        RequisitionOutcome.SalaryRangeInverted =>
            "The minimum salary is above the maximum.",
        RequisitionOutcome.ExpiryBeforeToday =>
            "The closing date has already passed. Move it before publishing.",
        RequisitionOutcome.NotInThatState => "The vacancy is not in a state for that.",
        RequisitionOutcome.HasCandidates =>
            "People have applied to this vacancy, so it cannot be deleted. Close it instead.",
        _ => "Could not save the vacancy.",
    };
}

public sealed record CloseRequisitionRequest
{
    public string? Reason { get; init; }
}
