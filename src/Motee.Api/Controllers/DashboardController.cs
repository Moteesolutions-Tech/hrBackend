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

    // The lifecycle overview. Spans workforce planning, recruitment,
    // onboarding, the employee roster and offboarding.
    //
    // One permission rather than one per stage, which breaks the rule above and
    // is worth saying why: every figure here is a count and nothing else. No
    // names, no records, no rows — an integer saying how many requisitions are
    // open is not recruitment data in the way a candidate list is. Behind the
    // employee module because the page is the employee lifecycle, and anybody
    // who cannot open the employee list has no business reading the funnel.
    //
    // If a card ever grows a list of people, this needs splitting.
    [HttpGet("lifecycle")]
    [ProducesResponseType<LifecycleDashboardDto>(StatusCodes.Status200OK)]
    [RequiresPermission("organization.employees", PermissionAction.View)]
    public async Task<IActionResult> Lifecycle(CancellationToken cancellationToken) =>
        Ok(await dashboard.LifecycleAsync(cancellationToken));

    // Who is about to lose leave. Named rows, so behind the leave module rather
    // than the employee one: this is a list of people and their balances, which
    // is leave data however few columns it has.
    //
    // There is no /dashboard/events to go with these. The Events tab wants the
    // agenda, and GET /calendar/upcoming?days=30 already is the agenda — one
    // that applies each event's own audience rules. A dashboard copy would
    // either repeat that or leak a one-to-one somebody is not party to.
    [HttpGet("untaken-leave")]
    [ProducesResponseType<UntakenLeaveDashboardDto>(StatusCodes.Status200OK)]
    [RequiresPermission("time-payroll.leave", PermissionAction.View)]
    public async Task<IActionResult> UntakenLeave(CancellationToken cancellationToken) =>
        Ok(await dashboard.UntakenLeaveAsync(cancellationToken));

    // The employee's own front page. RequiresEmployee rather than a module
    // permission, like the rest of self-service: needing the attendance module
    // to see your own clock would mean granting every member of staff the
    // screen that shows where the whole company is.
    //
    // Recent activity is not part of this. GET /notifications already answers
    // "what has happened to me", per user, with a link and a read flag the card
    // wants anyway — and a notification is a different thing from an
    // obligation, which is what Pending holds.
    [HttpGet("mine")]
    [RequiresEmployee]
    [ProducesResponseType<MyDashboardDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await dashboard.MineAsync(cancellationToken));

    // The Action Centre: everything outstanding across eleven categories.
    //
    // Behind the employee module, which is the narrowest gate that still makes
    // sense for something spanning documents, recruitment, employee relations,
    // performance, leave, learning, assets and occupational health. It names
    // people, so unlike the lifecycle counts it is not safe to open wider —
    // and anybody who cannot read the employee list has no business reading a
    // list of employees with things wrong on their records.
    //
    // Nothing clinical travels: the occupational-health rows carry a reference
    // and a name, never the reason.
    [HttpGet("hr-actions")]
    [ProducesResponseType<HrActionsDashboardDto>(StatusCodes.Status200OK)]
    [RequiresPermission("organization.employees", PermissionAction.View)]
    public async Task<IActionResult> HrActions(CancellationToken cancellationToken) =>
        Ok(await dashboard.HrActionsAsync(cancellationToken));
}
