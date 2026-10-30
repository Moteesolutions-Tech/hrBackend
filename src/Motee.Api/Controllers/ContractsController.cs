using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Contracts;
using Motee.Domain.Authorization;

namespace Motee.Api.Controllers;

// Contracts, issued and signed.
//
// Signing is split by who is doing it: an employee signs their own through the
// self-service route, which resolves the contract against them, and the company
// signs through the administrative one. A valid id is not permission to sign
// somebody else's contract as them.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/contracts")]
public class ContractsController(IContractService contracts) : ApiControllerBase
{
    private const string Module = "operations.contracts";

    // ── the employee's side ─────────────────────────────────────────────────

    [HttpGet("mine")]
    [RequiresEmployee]
    [ProducesResponseType<IReadOnlyList<ContractDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await contracts.MineAsync(cancellationToken));

    [HttpPost("mine/{id:guid}/sign")]
    [RequiresEmployee]
    [ProducesResponseType<ContractDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SignMine(
        Guid id,
        SignContractRequest request,
        CancellationToken cancellationToken) =>
        Respond(await contracts.SignMineAsync(id, request, cancellationToken), "Signed.");

    // ── issuing and managing ────────────────────────────────────────────────

    [HttpGet]
    [ProducesResponseType<PagedResult<ContractDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] ContractQuery query,
        CancellationToken cancellationToken) =>
        Ok(await contracts.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ContractDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        ContractDto? contract = await contracts.GetAsync(id, cancellationToken);

        return contract is null
            ? Failure<ContractDto>(MoteeStatusCodes.NotFound, "Contract not found.")
            : Ok(contract);
    }

    [HttpPost]
    [ProducesResponseType<ContractDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        ContractRequest request,
        CancellationToken cancellationToken)
    {
        ContractResult result = await contracts.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Contract!, "Contract drafted.")
            : Failure<ContractDto>(
                StatusFor(result.Outcome), result.Reason ?? MessageFor(result.Outcome));
    }

    // Refused once anybody has signed. A signature is against what the contract
    // said at that moment, so changing the terms afterwards is forgery by another
    // name — the service refuses rather than trusting no screen offers it.
    [HttpPut("{id:guid}")]
    [ProducesResponseType<ContractDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        ContractRequest request,
        CancellationToken cancellationToken) =>
        Respond(await contracts.UpdateAsync(id, request, cancellationToken), "Contract updated.");

    [HttpPost("{id:guid}/send")]
    [ProducesResponseType<ContractDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SendForSignature(
        Guid id,
        CancellationToken cancellationToken) =>
        Respond(
            await contracts.SendForSignatureAsync(id, cancellationToken),
            "Sent for signature.");

    [HttpPost("{id:guid}/activate")]
    [ProducesResponseType<ContractDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken) =>
        Respond(await contracts.ActivateAsync(id, cancellationToken), "Contract in force.");

    [HttpPost("{id:guid}/terminate")]
    [ProducesResponseType<ContractDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Terminate(
        Guid id,
        TerminateContractRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await contracts.TerminateAsync(id, request.Reason, cancellationToken),
            "Contract terminated.");

    // The company's side of the signature. The employee's own goes through
    // /mine/{id}/sign.
    [HttpPost("{id:guid}/sign")]
    [ProducesResponseType<ContractDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Sign(
        Guid id,
        SignContractRequest request,
        CancellationToken cancellationToken) =>
        Respond(await contracts.SignAsync(id, request, cancellationToken), "Signed.");

    // Files the signed copy into documents and shares it with the person it is
    // about. The PDF goes through /files first, like every other document.
    [HttpPost("{id:guid}/file-to-documents")]
    [ProducesResponseType<ContractDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> FileToDocuments(
        Guid id,
        FileContractRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await contracts.FileToDocumentsAsync(id, request.StoredFileId, cancellationToken),
            "Filed in documents.");

    [HttpPost("{id:guid}/notes")]
    [ProducesResponseType<ContractDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> AddNote(
        Guid id,
        ContractNoteRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await contracts.AddNoteAsync(id, request.Content, cancellationToken), "Note added.");

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        ContractOutcome outcome = await contracts.DeleteAsync(id, cancellationToken);

        return outcome == ContractOutcome.Succeeded
            ? Ok<object?>(null, "Contract deleted.")
            : Failure<object?>(StatusFor(outcome), MessageFor(outcome));
    }

    private IActionResult Respond(ContractResult result, string message) =>
        result.Succeeded
            ? Ok(result.Contract!, message)
            : Failure<ContractDto>(
                StatusFor(result.Outcome), result.Reason ?? MessageFor(result.Outcome));

    private static string StatusFor(ContractOutcome outcome) => outcome switch
    {
        ContractOutcome.NotFound or ContractOutcome.EmployeeNotFound =>
            MoteeStatusCodes.NotFound,
        ContractOutcome.NotAllowed or ContractOutcome.AlreadySigned
            or ContractOutcome.SignedContractIsFixed => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(ContractOutcome outcome) => outcome switch
    {
        ContractOutcome.NotFound => "Contract not found.",
        ContractOutcome.EmployeeNotFound => "That person does not work here.",
        ContractOutcome.TitleRequired => "Give the contract a title.",
        ContractOutcome.ReasonRequired => "Say why it is being terminated.",
        ContractOutcome.ContentRequired => "Write something before adding the note.",
        ContractOutcome.EndBeforeStart => "The end date is before the start date.",
        ContractOutcome.AmountNotPositive =>
            "Salary and notice period cannot be negative.",
        ContractOutcome.CurrencyInvalid =>
            "Use a three-letter currency code, such as NGN or GBP.",
        ContractOutcome.AlreadySigned => "You have already signed this contract.",
        ContractOutcome.SignedContractIsFixed =>
            "This contract has been signed, so its terms cannot change. Issue an "
            + "amendment instead.",
        ContractOutcome.AlreadyFiled => "This contract is already in Documents.",
        ContractOutcome.NotReadyToFile =>
            "Send the contract out before filing it — there is no agreed copy yet.",
        ContractOutcome.FileNotFound =>
            "That upload could not be found. Upload the signed copy first.",
        _ => "Could not save the contract.",
    };
}

public sealed record FileContractRequest
{
    // The signed PDF, uploaded through /files first.
    public required Guid StoredFileId { get; init; }
}

public sealed record TerminateContractRequest
{
    public required string Reason { get; init; }
}

public sealed record ContractNoteRequest
{
    public required string Content { get; init; }
}
