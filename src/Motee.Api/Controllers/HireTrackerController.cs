using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Workforce;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// The hire tracker: every hiring effort on one line.
//
// One read, and it owns no data. It joins the headcount request, the vacancy,
// the candidates, their offers and the onboarding, and answers the question
// none of those modules can answer alone: how far along is this, and has it
// moved lately.
//
// Rows come back stalled-first. A vacancy module shows vacancies and an
// onboarding module shows joiners; neither shows the role approved in August,
// advertised in September, that nobody has looked at since.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/hire-tracker")]
public class HireTrackerController(IHireTrackerService tracker) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<HireTrackerRowDto>>(StatusCodes.Status200OK)]
    [RequiresPermission("talent.hire-tracker", PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] HireTrackerQuery query,
        CancellationToken cancellationToken) =>
        Ok(await tracker.ListAsync(query, cancellationToken));
}
