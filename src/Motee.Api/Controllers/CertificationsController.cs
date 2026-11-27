using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Learning;
using Motee.Domain.Authorization;
using Motee.Domain.Learning;

namespace Motee.Api.Controllers;

// The certification register: completion certificates the product issued, and
// external credentials recorded against somebody.
//
// Whether a certificate is active, expiring or expired is worked out from its
// expiry against today, so a register left open overnight reads correctly in
// the morning. A completion certificate's expiry follows the course that
// issued it and is not editable here.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/certifications")]
public class CertificationsController(ICertificationService certifications)
    : ApiControllerBase
{
    private const string Module = "talent.training";

    [HttpGet]
    [ProducesResponseType<PagedResult<CertificationDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View, MinimumScope = DataScopeKind.DirectReports)]
    public async Task<IActionResult> List(
        [FromQuery] CertificationQuery query,
        CancellationToken cancellationToken) =>
        Ok(await certifications.ListAsync(query, Caller(), cancellationToken));

    [HttpGet("summary")]
    [ProducesResponseType<CertTally>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(
        [FromQuery] CertificationQuery query,
        CancellationToken cancellationToken) =>
        Ok(await certifications.SummaryAsync(query, cancellationToken));

    // Somebody's own, for their profile. No module grant: a person's own
    // credentials are theirs to see.
    [HttpGet("mine")]
    [Authorize]
    [ProducesResponseType<IReadOnlyList<CertificationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await certifications.MineAsync(cancellationToken));

    [HttpPost]
    [ProducesResponseType<CertificationDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Record(
        CertificationRequest request,
        CancellationToken cancellationToken)
    {
        CertificationResult result = await certifications.RecordAsync(
            request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Certification!, "Certificate recorded.")
            : Failure<CertificationDto>(
                LearningController.StatusFor(result.Outcome), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<CertificationDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        CertificationRequest request,
        CancellationToken cancellationToken)
    {
        CertificationResult result = await certifications.UpdateAsync(
            id, request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Certification!, "Certificate updated.")
            : Failure<CertificationDto>(
                LearningController.StatusFor(result.Outcome), MessageFor(result));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        CertificationResult result = await certifications.DeleteAsync(
            id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Certificate removed.")
            : Failure<object?>(
                LearningController.StatusFor(result.Outcome), MessageFor(result));
    }

    private static string MessageFor(CertificationResult result) => result.Outcome switch
    {
        LearningOutcome.NotFound => "Certificate not found.",
        LearningOutcome.EmployeeNotFound => "That person does not work here.",
        _ => LearningController.ReasonFor(result.Refusal),
    };
}
