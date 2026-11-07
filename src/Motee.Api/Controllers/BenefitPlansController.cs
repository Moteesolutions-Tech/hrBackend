using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Benefits;
using Motee.Domain.Authorization;
using Motee.Domain.Benefits;

namespace Motee.Api.Controllers;

// The benefits catalogue, and the coverage levels it draws on.
//
// Nothing here writes an entitlement. Who gets what is derived from a plan's
// scope and an employee's employment type on every read, so there is no
// enrol-this-person endpoint and no table of entitlements to fall out of step
// with the catalogue above it.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/benefit-plans")]
public class BenefitPlansController(IBenefitPlanService benefits) : ApiControllerBase
{
    private const string Module = "organization.benefit-plans";

    // The employee profile's Benefits section sits inside a record somebody
    // already had to be allowed to open, so it is gated on the employee
    // directory rather than on this module — a line manager can see what
    // their report is entitled to without being given the catalogue to edit.
    private const string EmployeeModule = "organization.employees";

    [HttpGet]
    [ProducesResponseType<BenefitPlanListDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] BenefitPlanQuery query,
        CancellationToken cancellationToken) =>
        Ok(await benefits.ListAsync(query, cancellationToken));

    // How much of the catalogue is live, how many people it lands on, and how
    // many it lands on nothing automatic for.
    [HttpGet("summary")]
    [ProducesResponseType<BenefitSummaryDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await benefits.SummaryAsync(cancellationToken));

    [HttpGet("coverage-levels")]
    [ProducesResponseType<IReadOnlyList<BenefitCoverageLevelDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> CoverageLevels(CancellationToken cancellationToken) =>
        Ok(await benefits.CoverageLevelsAsync(cancellationToken));

    [HttpPost("coverage-levels")]
    [ProducesResponseType<BenefitCoverageLevelDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> AddCoverageLevel(
        CoverageLevelRequest request,
        CancellationToken cancellationToken)
    {
        BenefitCoverageLevelResult result =
            await benefits.AddCoverageLevelAsync(request.Name, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Level!, "Coverage level added.")
            : Failure<BenefitCoverageLevelDto>(
                StatusFor(result.Outcome, result.Refusal), MessageFor(result.Outcome, result.Refusal));
    }

    // Every plan on the level moves with it: the plan holds the level's id
    // rather than a copy of its label, so there is nothing to carry across.
    [HttpPut("coverage-levels/{id:guid}")]
    [ProducesResponseType<BenefitCoverageLevelDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> RenameCoverageLevel(
        Guid id,
        CoverageLevelRequest request,
        CancellationToken cancellationToken)
    {
        BenefitCoverageLevelResult result =
            await benefits.RenameCoverageLevelAsync(id, request.Name, cancellationToken);

        return result.Succeeded
            ? Ok(result.Level!, "Coverage level renamed.")
            : Failure<BenefitCoverageLevelDto>(
                StatusFor(result.Outcome, result.Refusal), MessageFor(result.Outcome, result.Refusal));
    }

    [HttpDelete("coverage-levels/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> RemoveCoverageLevel(
        Guid id,
        CancellationToken cancellationToken) =>
        await benefits.RemoveCoverageLevelAsync(id, cancellationToken) switch
        {
            BenefitPlanOutcome.Succeeded => Ok<object?>(
                null, "Coverage level removed. Plans on it now have no coverage level."),
            _ => Failure<object?>(MoteeStatusCodes.NotFound, "Coverage level not found."),
        };

    // What one person is entitled to, split into what lands automatically and
    // what is theirs to take up, with any waiting period resolved against
    // their start date.
    [HttpGet("employees/{employeeId:guid}")]
    [ProducesResponseType<EmployeeBenefitsDto>(StatusCodes.Status200OK)]
    [RequiresPermission(EmployeeModule, PermissionAction.View)]
    public async Task<IActionResult> ForEmployee(
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        EmployeeBenefitsDto? found = await benefits.ForEmployeeAsync(employeeId, cancellationToken);

        return found is null
            ? Failure<EmployeeBenefitsDto>(MoteeStatusCodes.NotFound, "Employee not found.")
            : Ok(found);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<BenefitPlanDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        BenefitPlanDto? found = await benefits.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<BenefitPlanDto>(MoteeStatusCodes.NotFound, "Benefit plan not found.")
            : Ok(found);
    }

    [HttpPost]
    [ProducesResponseType<BenefitPlanDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        BenefitPlanRequest request,
        CancellationToken cancellationToken)
    {
        BenefitPlanResult result = await benefits.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Plan!, "Benefit plan created.")
            : Failure<BenefitPlanDto>(
                StatusFor(result.Outcome, result.Refusal), MessageFor(result.Outcome, result.Refusal));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<BenefitPlanDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        BenefitPlanRequest request,
        CancellationToken cancellationToken) =>
        Respond(await benefits.UpdateAsync(id, request, cancellationToken), "Benefit plan saved.");

    // Always a draft, whatever it was copied from.
    [HttpPost("{id:guid}/duplicate")]
    [ProducesResponseType<BenefitPlanDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Duplicate(Guid id, CancellationToken cancellationToken)
    {
        BenefitPlanResult result = await benefits.DuplicateAsync(id, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Plan!, "Benefit plan duplicated as a draft.")
            : Failure<BenefitPlanDto>(
                StatusFor(result.Outcome, result.Refusal), MessageFor(result.Outcome, result.Refusal));
    }

    [HttpPost("{id:guid}/status")]
    [ProducesResponseType<BenefitPlanDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetStatus(
        Guid id,
        BenefitPlanStatusRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await benefits.SetStatusAsync(id, request.Status, cancellationToken),
            request.Status switch
            {
                BenefitPlanStatus.Active => "Benefit plan activated.",
                BenefitPlanStatus.Archived => "Benefit plan archived.",
                _ => "Benefit plan moved back to draft.",
            });

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        BenefitPlanResult result = await benefits.DeleteAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Benefit plan deleted.")
            : Failure<object?>(
                StatusFor(result.Outcome, result.Refusal), MessageFor(result.Outcome, result.Refusal));
    }

    private IActionResult Respond(BenefitPlanResult result, string message) =>
        result.Succeeded
            ? Ok(result.Plan!, message)
            : Failure<BenefitPlanDto>(
                StatusFor(result.Outcome, result.Refusal), MessageFor(result.Outcome, result.Refusal));

    private static string StatusFor(BenefitPlanOutcome outcome, BenefitPlanRefusal refusal) =>
        outcome switch
        {
            BenefitPlanOutcome.NotFound => MoteeStatusCodes.NotFound,

            BenefitPlanOutcome.Refused when refusal
                is BenefitPlanRefusal.CoverageLevelNameTaken
                or BenefitPlanRefusal.CataloguePlanCannotBeDeleted => MoteeStatusCodes.Conflict,

            _ => MoteeStatusCodes.InvalidRequest,
        };

    private static string MessageFor(BenefitPlanOutcome outcome, BenefitPlanRefusal refusal) =>
        outcome == BenefitPlanOutcome.NotFound ? "Not found." : ReasonFor(refusal);

    private static string ReasonFor(BenefitPlanRefusal refusal) => refusal switch
    {
        BenefitPlanRefusal.NameRequired =>
            $"Give the plan a name of at least {BenefitPlans.ShortestName} characters.",
        BenefitPlanRefusal.NameTooLong =>
            $"Keep the name to {BenefitPlans.LongestName} characters or fewer.",
        BenefitPlanRefusal.DescriptionRequired =>
            "Describe what the benefit is — employees read this on their profile.",
        BenefitPlanRefusal.DescriptionTooLong =>
            $"Keep the description to {BenefitPlans.LongestDescription} characters or fewer.",
        BenefitPlanRefusal.ContributionOutOfRange =>
            "A contribution has to be a percentage between 0 and 100.",
        BenefitPlanRefusal.WaitingPeriodNegative =>
            "A waiting period cannot be negative. Leave it blank for cover from day one.",
        BenefitPlanRefusal.WaitingPeriodTooLong =>
            $"That waiting period is over {BenefitPlans.LongestWaitingPeriodDays} days — "
            + "check whether the figure was meant to be months.",
        BenefitPlanRefusal.ScopeHasNoEmploymentTypes =>
            "Pick at least one employment type, or set the plan to cover every employee. "
            + "Leaving it empty would give the benefit to everybody.",
        BenefitPlanRefusal.CoverageLevelUnknown =>
            "That coverage level is not on your list. Add it under Manage first.",
        BenefitPlanRefusal.CountryNotThisTenants =>
            "A benefit plan belongs to the country your company operates in.",
        BenefitPlanRefusal.CataloguePlanCannotBeDeleted =>
            "This plan came with the catalogue. Archive it instead.",
        BenefitPlanRefusal.CoverageLevelNameRequired => "Give the coverage level a name.",
        BenefitPlanRefusal.CoverageLevelNameTaken =>
            "That coverage level is already on your list.",
        _ => "Could not save the benefit plan.",
    };
}

public sealed record CoverageLevelRequest
{
    public required string Name { get; init; }
}

public sealed record BenefitPlanStatusRequest
{
    public required BenefitPlanStatus Status { get; init; }
}
