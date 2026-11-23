using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Api.Http;
using Motee.Application.Eor;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Invoices a provider has sent.
//
// The amounts are recorded as billed rather than recomputed: the provider's
// document is the record, and replacing their figures with ours would hide a
// dispute rather than raise it. What is derived is the comparison — whether
// their parts make their total, and whether the headcount they billed matches
// the workers we had engaged that month. That check is the reason to hold
// invoices at all; the screen shows them checked against nothing.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/eor/invoices")]
public class EorInvoicesController(IEorBillingService billing) : ApiControllerBase
{
    private const string Module = EorController.Module;

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<EorInvoiceDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? providerId,
        CancellationToken cancellationToken) =>
        Ok(await billing.InvoicesAsync(providerId, cancellationToken));

    [HttpPost]
    [ProducesResponseType<EorInvoiceDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Record(
        EorInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        EorResult<EorInvoiceDto> result = await billing.RecordAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Value!, "Invoice recorded.")
            : Failure<EorInvoiceDto>(
                EorController.StatusFor(result.Outcome), EorController.MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<EorInvoiceDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        EorInvoiceRequest request,
        CancellationToken cancellationToken) =>
        Respond(await billing.UpdateAsync(id, request, cancellationToken), "Invoice updated.");

    // Paid, overdue and pending all follow from the paid date and the due date,
    // so there is no status to set separately — and no way for an invoice to
    // sit at "Pending" a month past its due date.
    [HttpPost("{id:guid}/settle")]
    [ProducesResponseType<EorInvoiceDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Settle(
        Guid id,
        SettleInvoiceRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await billing.SettleAsync(id, request, cancellationToken),
            request.PaidOn is null ? "Invoice reopened." : "Invoice marked paid.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        EorOutcome outcome = await billing.RemoveAsync(id, cancellationToken);

        return outcome == EorOutcome.Succeeded
            ? Ok<object?>(null, "Invoice removed.")
            : Failure<object?>(MoteeStatusCodes.NotFound, "Invoice not found.");
    }

    private IActionResult Respond(EorResult<EorInvoiceDto> result, string message) =>
        result.Succeeded
            ? Ok(result.Value!, message)
            : Failure<EorInvoiceDto>(
                EorController.StatusFor(result.Outcome), EorController.MessageFor(result));
}
