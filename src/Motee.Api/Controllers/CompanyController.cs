using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Api.Http;
using Motee.Application.Company;
using Motee.Domain.Authorization;
using Motee.Domain.Company;

namespace Motee.Api.Controllers;

// The company's own record: who it is, the paperwork proving it, and the shape
// of the organisation.
//
// Most of what the screen shows is read from whatever already owns it — the
// address from the branch marked Headquarters, the headcount from the
// employees, the pinned notice from the announcements module, the org chart
// from reporting lines. Only the verification needed a table.
//
// The permission key for this has existed since the matrix shipped with nothing
// behind it: there were no company endpoints at all.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/company")]
public class CompanyController(
    ICompanyService company,
    ICompanyVerificationService verification) : ApiControllerBase
{
    internal const string Module = "organization.company";

    [HttpGet]
    [ProducesResponseType<CompanyProfileDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Profile(CancellationToken cancellationToken) =>
        Ok(await company.ProfileAsync(cancellationToken));

    [HttpPut]
    [ProducesResponseType<CompanyProfileDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SaveProfile(
        CompanyProfileRequest request,
        CancellationToken cancellationToken) =>
        Respond(await company.SaveProfileAsync(request, cancellationToken), "Profile saved.");

    // Derived from reporting lines, with every top-of-chain person returned
    // rather than one. The screen renders from a single root, so a company with
    // two people at the top loses half its chart — and a reporting line that
    // loops recurses until the browser gives up.
    [HttpGet("org-chart")]
    [ProducesResponseType<OrgChartView>(StatusCodes.Status200OK)]
    [RequiresPermission("organization.structure", PermissionAction.View)]
    public async Task<IActionResult> OrgChart(
        [FromQuery] int? maxLevel,
        CancellationToken cancellationToken) =>
        Ok(await company.OrgChartAsync(maxLevel, cancellationToken));

    // The notice pinned to the profile. Read from the announcements module,
    // which already has pinning — a second place to store one company notice
    // would be a second notice.
    [HttpGet("notice")]
    [ProducesResponseType<PinnedNoticeDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Notice(CancellationToken cancellationToken)
    {
        PinnedNoticeDto? notice = await company.PinnedNoticeAsync(cancellationToken);

        // Null is an answer, not a failure: a company with nothing pinned is
        // the ordinary case, and a 404 would read as a broken endpoint.
        return Ok(notice);
    }

    // ── verification ───────────────────────────────────────────────────────

    [HttpGet("verification")]
    [ProducesResponseType<IReadOnlyList<VerificationDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Verification(CancellationToken cancellationToken) =>
        Ok(await verification.AllAsync(cancellationToken));

    [HttpPut("verification/{kind}")]
    [ProducesResponseType<VerificationDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SaveVerification(
        VerificationKind kind,
        VerificationDetailsRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await verification.SaveDetailsAsync(kind, request, cancellationToken),
            "Details saved.");

    // The company hands it over; it cannot mark its own paperwork verified. The
    // review route is on the platform controller, where Motee staff reach it.
    [HttpPost("verification/{kind}/submit")]
    [ProducesResponseType<VerificationDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Submit(
        VerificationKind kind,
        CancellationToken cancellationToken) =>
        Respond(await verification.SubmitAsync(kind, cancellationToken), "Submitted for review.");

    // Back to draft, so the number or the certificate can be corrected. The way
    // out of a rejection, and the way to pull back a submission nobody has
    // picked up yet.
    [HttpPost("verification/{kind}/withdraw")]
    [ProducesResponseType<VerificationDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Withdraw(
        VerificationKind kind,
        CancellationToken cancellationToken) =>
        Respond(
            await verification.WithdrawAsync(kind, cancellationToken),
            "Reopened for correction.");

    private IActionResult Respond<T>(CompanyResult<T> result, string message) =>
        result.Succeeded
            ? Ok(result.Value!, message)
            : Failure<T>(StatusFor(result.Outcome), MessageFor(result));

    internal static string StatusFor(CompanyOutcome outcome) => outcome switch
    {
        CompanyOutcome.NotFound => MoteeStatusCodes.NotFound,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    internal static string MessageFor<T>(CompanyResult<T> result) => result.Outcome switch
    {
        CompanyOutcome.NotFound => "Not found.",
        _ => result.Refusal switch
        {
            VerificationRefusal.NumberRequired =>
                "Enter the registration number before submitting.",
            VerificationRefusal.DocumentRequired =>
                "Attach the certificate before submitting — a submission with nothing "
                + "attached is a reviewer being asked to verify a blank form.",
            VerificationRefusal.NotEditableNow =>
                "This has already been submitted. Withdraw it first to change the details.",
            VerificationRefusal.StageNotReachable => "That is not a move this record can make.",
            VerificationRefusal.ReasonRequired => "Say why it was rejected.",
            VerificationRefusal.NotReviewable => "Only Motee staff can review a verification.",
            _ => "That change was not accepted.",
        },
    };
}
