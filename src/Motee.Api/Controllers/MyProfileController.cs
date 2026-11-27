using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Employees;

namespace Motee.Api.Controllers;

// The employee's own side of a profile change request.
//
// RequiresEmployee rather than a module permission, like the rest of self-service:
// needing the employee module to ask for your own phone number to be corrected would
// mean granting every member of staff the screen listing the whole company.
//
// There is no id in any of these routes. The subject is always the caller, and leaving
// it out of the signature is what makes raising one against somebody else impossible
// rather than merely checked.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/employees/me/change-requests")]
public class MyProfileController(IChangeRequestService requests) : ApiControllerBase
{
    // The fields that may be asked about, with their labels and the caller's current
    // values. Served rather than hardcoded in the client, because a field the form
    // offers that the backend will refuse is a request somebody cannot finish.
    [HttpGet("fields")]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<RequestableFieldDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Fields(CancellationToken cancellationToken) =>
        Ok(await requests.FieldsAsync(cancellationToken));

    [HttpGet]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<ChangeRequestDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await requests.MineAsync(cancellationToken));

    [HttpPost]
    [RequiresEmployee]
    [ProducesResponseType<ChangeRequestDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Raise(
        RaiseChangeRequest request,
        CancellationToken cancellationToken)
    {
        ChangeRequestResult result = await requests.RaiseAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Request!, "Change requested.")
            : Failure<ChangeRequestDto>(
                ChangeRequestResponses.StatusFor(result.Outcome),
                result.Reason ?? ChangeRequestResponses.MessageFor(result.Outcome));
    }

    // Taken back before anybody decided. Theirs to withdraw, and the service checks
    // they raised it — a withdrawal is not a rejection, and the log should not read as
    // though HR refused something nobody ever ruled on.
    [HttpPost("{id:guid}/withdraw")]
    [RequiresEmployee]
    [ProducesResponseType<ChangeRequestDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken cancellationToken)
    {
        ChangeRequestResult result = await requests.WithdrawAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok(result.Request!, "Change request withdrawn.")
            : Failure<ChangeRequestDto>(
                ChangeRequestResponses.StatusFor(result.Outcome),
                result.Reason ?? ChangeRequestResponses.MessageFor(result.Outcome));
    }
}
