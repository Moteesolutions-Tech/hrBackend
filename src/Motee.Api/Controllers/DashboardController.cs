using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Dashboard;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// The HR dashboard.
//
// Two reads, one per tab this can fill honestly. The screen has seven tabs; the
// other five want sickness, recruitment, surveys, occupational health and
// training, and none of those modules exists. Serving them as empty would look
// like a company with no data rather than a product without the feature.
//
// Each read is permissioned as the module it aggregates rather than against a
// dashboard permission of its own. A headcount split by gender is employee data
// however it is totalled, and somebody who cannot open the employee list should
// not read it off the front page instead.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/dashboard")]
public class DashboardController(IDashboardService dashboard) : ApiControllerBase
{
    [HttpGet("people")]
    [ProducesResponseType<PeopleDashboardDto>(StatusCodes.Status200OK)]
    [RequiresPermission("organization.employees", PermissionAction.View)]
    public async Task<IActionResult> People(CancellationToken cancellationToken) =>
        Ok(await dashboard.PeopleAsync(cancellationToken));

    [HttpGet("attendance")]
    [ProducesResponseType<AttendanceDashboardDto>(StatusCodes.Status200OK)]
    [RequiresPermission("time-payroll.attendance", PermissionAction.View)]
    public async Task<IActionResult> Attendance(CancellationToken cancellationToken) =>
        Ok(await dashboard.AttendanceAsync(cancellationToken));
}
