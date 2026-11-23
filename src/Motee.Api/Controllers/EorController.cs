using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Api.Http;
using Motee.Application.Common;
using Motee.Application.Eor;
using Motee.Domain.Authorization;
using Motee.Domain.Eor;

namespace Motee.Api.Controllers;

// Employer of Record: workers whose legal employer is a third-party provider in
// their own country.
//
// A worker here is an employee with an engagement, not a separate kind of
// person. The screen collects the whole employee profile again — legal names,
// date of birth, nationality, contact, role, department — and employees already
// holds all of it, so the engagement carries only what is true because of the
// EOR arrangement. That also means an EOR worker appears in the directory,
// holds documents and books leave like anybody else.
//
// The permission key has been granted to five roles since the matrix shipped
// and gated nothing, because there were no endpoints behind it.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/eor")]
public class EorController(
    IEorService eor,
    IEorComplianceService compliance) : ApiControllerBase
{
    internal const string Module = "organization.eor";

    // ── providers ──────────────────────────────────────────────────────────

    [HttpGet("providers")]
    [ProducesResponseType<IReadOnlyList<EorProviderDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Providers(CancellationToken cancellationToken) =>
        Ok(await eor.ProvidersAsync(cancellationToken));

    [HttpGet("providers/{id:guid}")]
    [ProducesResponseType<EorProviderDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Provider(Guid id, CancellationToken cancellationToken)
    {
        EorProviderDto? provider = await eor.ProviderAsync(id, cancellationToken);

        return provider is null
            ? Failure<EorProviderDto>(MoteeStatusCodes.NotFound, "Provider not found.")
            : Ok(provider);
    }

    [HttpPost("providers")]
    [ProducesResponseType<EorProviderDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> AddProvider(
        EorProviderRequest request,
        CancellationToken cancellationToken)
    {
        EorResult<EorProviderDto> result = await eor.AddProviderAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Value!, "Provider added.")
            : Failure<EorProviderDto>(StatusFor(result.Outcome), MessageFor(result));
    }

    [HttpPut("providers/{id:guid}")]
    [ProducesResponseType<EorProviderDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> UpdateProvider(
        Guid id,
        EorProviderRequest request,
        CancellationToken cancellationToken) =>
        Respond(await eor.UpdateProviderAsync(id, request, cancellationToken), "Provider updated.");

    // Made inactive rather than deleted, and refused while anybody is still
    // engaged through them: invoices and ended engagements point at the record,
    // and workers attached to a partner the company has stopped using is the
    // state this prevents.
    [HttpDelete("providers/{id:guid}")]
    [ProducesResponseType<EorProviderDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> RetireProvider(
        Guid id,
        CancellationToken cancellationToken) =>
        Respond(await eor.RetireProviderAsync(id, cancellationToken), "Provider retired.");

    // ── workers ────────────────────────────────────────────────────────────

    [HttpGet("workers")]
    [ProducesResponseType<PagedResult<EorWorkerDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Workers(
        [FromQuery] EorWorkerQuery query,
        CancellationToken cancellationToken) =>
        Ok(await eor.WorkersAsync(query, cancellationToken));

    [HttpGet("workers/{id:guid}")]
    [ProducesResponseType<EorWorkerDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Worker(Guid id, CancellationToken cancellationToken)
    {
        EorWorkerDto? worker = await eor.WorkerAsync(id, cancellationToken);

        return worker is null
            ? Failure<EorWorkerDto>(MoteeStatusCodes.NotFound, "Engagement not found.")
            : Ok(worker);
    }

    [HttpPost("workers")]
    [ProducesResponseType<EorWorkerDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Engage(
        EorEngagementRequest request,
        CancellationToken cancellationToken)
    {
        EorResult<EorWorkerDto> result = await eor.EngageAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Value!, "Worker engaged.")
            : Failure<EorWorkerDto>(StatusFor(result.Outcome), MessageFor(result));
    }

    [HttpPut("workers/{id:guid}")]
    [ProducesResponseType<EorWorkerDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> UpdateEngagement(
        Guid id,
        EorEngagementRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await eor.UpdateEngagementAsync(id, request, cancellationToken),
            "Engagement updated.");

    // Its own route rather than an end date on the update. Ending an engagement
    // is a decision with a date and a reason worth recording, and the stage the
    // register shows follows from it.
    [HttpPost("workers/{id:guid}/end")]
    [ProducesResponseType<EorWorkerDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> End(
        Guid id,
        EndEngagementRequest request,
        CancellationToken cancellationToken) =>
        Respond(await eor.EndAsync(id, request, cancellationToken), "Engagement ended.");

    // The engagement goes; the person stays. Removing somebody from this
    // register means they are no longer engaged through a provider, not that
    // they never worked here.
    [HttpDelete("workers/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        EorOutcome outcome = await eor.RemoveAsync(id, cancellationToken);

        return outcome == EorOutcome.Succeeded
            ? Ok<object?>(null, "Engagement removed.")
            : Failure<object?>(MoteeStatusCodes.NotFound, "Engagement not found.");
    }

    [HttpGet("summary")]
    [ProducesResponseType<EorSummary>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await eor.SummaryAsync(cancellationToken));

    // ── compliance ─────────────────────────────────────────────────────────

    [HttpGet("compliance")]
    [ProducesResponseType<IReadOnlyList<ComplianceRowDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Compliance(CancellationToken cancellationToken) =>
        Ok(await compliance.RegisterAsync(cancellationToken));

    [HttpPut("workers/{id:guid}/compliance/{key}")]
    [ProducesResponseType<EorWorkerDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> RecordCompliance(
        Guid id,
        ComplianceKey key,
        ComplianceRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await compliance.RecordAsync(id, key, request, cancellationToken),
            "Compliance updated.");

    private IActionResult Respond<T>(EorResult<T> result, string message) =>
        result.Succeeded
            ? Ok(result.Value!, message)
            : Failure<T>(StatusFor(result.Outcome), MessageFor(result));

    internal static string StatusFor(EorOutcome outcome) => outcome switch
    {
        EorOutcome.NotFound => MoteeStatusCodes.NotFound,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    internal static string MessageFor<T>(EorResult<T> result) => result.Outcome switch
    {
        EorOutcome.NotFound => "Not found.",
        _ => ReasonFor(result.Refusal),
    };

    internal static string ReasonFor(EorRefusal refusal) => refusal switch
    {
        EorRefusal.NameRequired => "Give the provider a name.",
        EorRefusal.FeePercentOutOfRange =>
            $"A management fee has to be between 0 and {EorProviders.MaximumFeePercent} per cent.",
        EorRefusal.NoCountriesCovered =>
            "Name at least one country this provider can employ in — a provider covering "
            + "nowhere can employ nobody.",
        EorRefusal.ProviderNotFound => "That provider does not exist.",
        EorRefusal.ProviderInactive => "That provider is no longer active.",
        EorRefusal.ProviderDoesNotCoverCountry =>
            "That provider cannot legally employ in that country.",
        EorRefusal.ProviderHasLiveEngagements =>
            "Workers are still engaged through this provider. End their engagements first.",
        EorRefusal.EmployeeNotFound => "That person does not work here.",
        EorRefusal.AlreadyEngaged =>
            "That person already has a live engagement. End it before starting another.",
        EorRefusal.EndsBeforeStart => "An engagement cannot end before it starts.",
        EorRefusal.GrossNotPositive => "Enter the worker's gross monthly pay.",
        EorRefusal.CostNegative => "Costs cannot be negative.",
        EorRefusal.CurrencyUnknown => "Choose the currency the worker is paid in.",
        EorRefusal.RateNotPositive => "An exchange rate has to be greater than zero.",
        EorRefusal.RateDateMissing =>
            "Say when the exchange rate was taken — a rate nobody can date is one nobody "
            + "can judge.",
        EorRefusal.EndDateRequired => "Give the engagement an end date.",
        EorRefusal.PeriodRequired => "Say which month the invoice is for.",
        EorRefusal.AmountNegative => "An invoiced amount cannot be negative.",
        EorRefusal.InvoiceNotFound => "That invoice does not exist.",
        _ => "That change was not accepted.",
    };
}
