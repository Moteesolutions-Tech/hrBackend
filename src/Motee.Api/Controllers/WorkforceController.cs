using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Workforce;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Workforce planning: turnover and hiring.
//
// Three reads, all derived. The screen they feed held three hardcoded arrays,
// and the point of replacing them is that these move when the records do — a
// resignation logged this morning is in this afternoon's rate.
//
// There is no skills-gap route. It needs a skill per employee and a category
// per skill, and neither exists in the product; the frontend's version is
// invented rows. An endpoint here would return fiction with the authority of
// an API, which is worse than the screen admitting it has no data.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/workforce")]
public class WorkforceController(IWorkforceAnalyticsService workforce) : ApiControllerBase
{
    private const string Module = "operations.workforce";

    [HttpGet("overview")]
    [ProducesResponseType<WorkforceOverviewDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Overview(CancellationToken cancellationToken) =>
        Ok(await workforce.OverviewAsync(cancellationToken));

    // One row per quarter per department, with a company row per quarter. The
    // voluntary split is the point: a single turnover figure hides whether a
    // company is losing people or letting them go.
    [HttpGet("turnover")]
    [ProducesResponseType<IReadOnlyList<TurnoverRowDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Turnover(
        [FromQuery] TurnoverQuery query,
        CancellationToken cancellationToken) =>
        Ok(await workforce.TurnoverAsync(query, cancellationToken));

    [HttpGet("hiring")]
    [ProducesResponseType<IReadOnlyList<HiringRowDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Hiring(
        [FromQuery] HiringQuery query,
        CancellationToken cancellationToken) =>
        Ok(await workforce.HiringAsync(query, cancellationToken));
}
