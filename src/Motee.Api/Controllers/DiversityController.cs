using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Diversity;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Diversity and inclusion.
//
// Two audiences, deliberately split across two permission models.
//
// The declaration routes are Authorize-only and resolve the caller. There is
// no route here that writes somebody else's answers, and none that reads
// them: an employee's ethnicity is not an administrative field, so no amount
// of permission grants access to one person's declaration. Granting HR the
// module buys the aggregate report, not the rows behind it.
//
// The report route is permissioned, and returns counts with every group below
// the suppression threshold folded away. A group of two on an ethnicity chart
// names those two people to anybody who knows the team, so the server never
// sends one.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/diversity")]
public class DiversityController(IDiversityService diversity) : ApiControllerBase
{
    // ── the employee's own ──────────────────────────────────────────────────

    // The questions for this company's country, with the caller's own answers
    // against them. Authorize rather than a permission: answering questions
    // about yourself is not a grant anybody issues.
    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType<DiversityFormDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await diversity.MineAsync(cancellationToken));

    [Authorize]
    [HttpPut("me")]
    [ProducesResponseType<DiversityFormDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Declare(
        DiversityRequest request,
        CancellationToken cancellationToken)
    {
        DiversityResult result = await diversity.DeclareAsync(request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Form!, "Thank you — your answers are saved.")
            : Failure<DiversityFormDto>(StatusFor(result.Outcome), MessageFor(result));
    }

    // ── reporting ───────────────────────────────────────────────────────────

    // Counts only, suppressed. Permissioned as headcount: this is workforce
    // composition, and whoever may see the headcount breakdowns may see
    // these — at the aggregate level, which is the only level there is.
    [HttpGet("report")]
    [ProducesResponseType<DiversityReportDto>(StatusCodes.Status200OK)]
    [RequiresPermission("organization.headcount", PermissionAction.View)]
    public async Task<IActionResult> Report(
        [FromQuery] DiversityReportQuery query,
        CancellationToken cancellationToken) =>
        Ok(await diversity.ReportAsync(query, cancellationToken));

    private static string StatusFor(DiversityOutcome outcome) => outcome switch
    {
        DiversityOutcome.NoEmployeeRecord => MoteeStatusCodes.NotFound,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(DiversityResult result) => result.Outcome switch
    {
        DiversityOutcome.NoEmployeeRecord =>
            "There is no employee record against this account to declare against.",

        // Names the question rather than the value, because echoing a
        // rejected special-category answer back into a log or an error
        // message is the thing to avoid.
        DiversityOutcome.AnswerNotOffered =>
            $"That is not one of the options offered for \"{result.Field}\" here.",

        _ => "Could not save your answers.",
    };
}
