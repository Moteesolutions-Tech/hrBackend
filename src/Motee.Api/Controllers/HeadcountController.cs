using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Headcount;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Headcount planning.
//
// Reads are the bulk of it, and one write: the target a department commits to
// for a period. Everything else on the screen — actual headcount, the gap,
// open vacancies, where recruitment has got to, the demographics, the
// attrition scores — is derived per request from records that already exist.
//
// The attrition board is the one to be careful with. It is a prompt for a
// conversation and never a decision, so every row carries the weighted
// factors behind its score: somebody shown a number can see what built it and
// say it is wrong.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/headcount")]
public class HeadcountController(IHeadcountService headcount) : ApiControllerBase
{
    private const string Module = "organization.headcount";

    // The plan table and the gap report. With no period, this quarter.
    [HttpGet("plans")]
    [ProducesResponseType<HeadcountPlanBoardDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Board(
        [FromQuery] string? period,
        CancellationToken cancellationToken) =>
        Ok(await headcount.BoardAsync(period, cancellationToken));

    [HttpGet("trend")]
    [ProducesResponseType<HeadcountTrendDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Trend(
        [FromQuery] string period,
        [FromQuery] TrendComparison comparison,
        CancellationToken cancellationToken) =>
        Ok(await headcount.TrendAsync(period, comparison, cancellationToken));

    [HttpGet("series")]
    [ProducesResponseType<IReadOnlyList<HeadcountSeriesPointDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Series(
        [FromQuery] int? year,
        CancellationToken cancellationToken) =>
        Ok(await headcount.SeriesAsync(year, cancellationToken));

    // Workforce composition. Permissioned as headcount rather than as
    // employees because it is counts, not records — but the counts are built
    // from bands and never from dates of birth, so nothing identifying leaves
    // through here.
    [HttpGet("demographics")]
    [ProducesResponseType<DemographicsDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Demographics(CancellationToken cancellationToken) =>
        Ok(await headcount.DemographicsAsync(cancellationToken));

    [HttpGet("attrition")]
    [ProducesResponseType<IReadOnlyList<AttritionRiskDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Attrition(
        [FromQuery] AttritionQuery query,
        CancellationToken cancellationToken) =>
        Ok(await headcount.AttritionAsync(query, cancellationToken));

    // Set or change a department's target. One row per department per period,
    // so this is a single route rather than a create and an update: asking
    // the caller to know which one it is would be asking them to track
    // something the server already knows.
    [HttpPut("plans")]
    [ProducesResponseType<HeadcountPlanRowDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetTarget(
        HeadcountPlanRequest request,
        CancellationToken cancellationToken)
    {
        HeadcountPlanResult result = await headcount.SetTargetAsync(request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Plan!, "Target saved.")
            : Failure<HeadcountPlanRowDto>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpDelete("plans/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await headcount.DeleteAsync(id, cancellationToken) == HeadcountPlanOutcome.Succeeded
            ? Ok<object?>(null, "Target removed.")
            : Failure<object?>(MoteeStatusCodes.NotFound, "Target not found.");

    private static string StatusFor(HeadcountPlanOutcome outcome) => outcome switch
    {
        HeadcountPlanOutcome.NotFound or HeadcountPlanOutcome.DepartmentNotFound =>
            MoteeStatusCodes.NotFound,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(HeadcountPlanOutcome outcome) => outcome switch
    {
        HeadcountPlanOutcome.NotFound => "Target not found.",
        HeadcountPlanOutcome.DepartmentNotFound => "That department does not exist.",
        HeadcountPlanOutcome.PeriodNotRecognised =>
            "A period reads like \"Q3 2026\" or \"FY 2026\".",
        HeadcountPlanOutcome.TargetNegative => "A target cannot be below zero.",
        HeadcountPlanOutcome.BudgetNegative => "A budget cannot be below zero.",
        _ => "Could not save the target.",
    };
}
