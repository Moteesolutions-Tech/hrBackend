using Motee.Application.Common;
using Motee.Domain.Contracts;

namespace Motee.Application.Contracts;

// Contracts, issued and signed.
//
// Signing here means a recorded act with a timestamp and an attributed party, not a
// cryptographic signature. That is an honest description of what a drawn signature
// in a browser is worth, and the audit trail — who, when, as what, against which
// version of the terms — is the part that would actually be relied on.
public interface IContractService
{
    Task<PagedResult<ContractDto>> ListAsync(
        ContractQuery query,
        CancellationToken cancellationToken = default);

    Task<ContractDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ContractResult> CreateAsync(
        ContractRequest request,
        CancellationToken cancellationToken = default);

    Task<ContractResult> UpdateAsync(
        Guid id,
        ContractRequest request,
        CancellationToken cancellationToken = default);

    Task<ContractResult> SendForSignatureAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<ContractResult> ActivateAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ContractResult> TerminateAsync(
        Guid id,
        string reason,
        CancellationToken cancellationToken = default);

    Task<ContractOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ContractResult> AddNoteAsync(
        Guid id,
        string content,
        CancellationToken cancellationToken = default);

    // Signing on behalf of the company. The employee's own signature goes through
    // the self-service route, which resolves the contract against them.
    Task<ContractResult> SignAsync(
        Guid id,
        SignContractRequest request,
        CancellationToken cancellationToken = default);

    // Files the signed copy into the documents module and shares it with the
    // person it is about.
    //
    // The two modules hold different things: a contract is the terms and their
    // lifecycle, a document is the artefact somebody can open. Linking them means
    // an employee finds their contract where they look for every other document,
    // without the contracts screen having to reimplement sharing and versions.
    //
    // The PDF is uploaded through the files endpoint first, like every other
    // document — the signed copy is produced by the signing flow, not here.
    Task<ContractResult> FileToDocumentsAsync(
        Guid id,
        Guid storedFileId,
        CancellationToken cancellationToken = default);

    // ── the employee's side ─────────────────────────────────────────────────

    Task<IReadOnlyList<ContractDto>> MineAsync(CancellationToken cancellationToken = default);

    Task<ContractResult> SignMineAsync(
        Guid id,
        SignContractRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ContractSignatureDto
{
    public Guid? EmployeeId { get; init; }

    public required string SignedAs { get; init; }

    public required string SignedByName { get; init; }

    public Guid? SignatureFileId { get; init; }

    public required DateTimeOffset SignedAt { get; init; }
}

public sealed record ContractNoteDto
{
    public required Guid Id { get; init; }

    public required string Content { get; init; }

    public string? CreatedByName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record ContractDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? DepartmentName { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required ContractType ContractType { get; init; }

    public required ContractStatus Status { get; init; }

    // Both derived from the end date, so they become true at midnight and stop the
    // day a contract is renewed or terminated.
    public required bool IsExpired { get; init; }

    public required bool IsExpiringSoon { get; init; }

    public required DateOnly StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public required bool AutoRenew { get; init; }

    public required int NoticePeriodDays { get; init; }

    public decimal? Salary { get; init; }

    public required string Currency { get; init; }

    public required SignatureStatus SignatureStatus { get; init; }

    public required IReadOnlyList<ContractSignatureDto> Signatures { get; init; }

    public required IReadOnlyList<ContractNoteDto> Notes { get; init; }

    public required bool IsArchived { get; init; }

    // The filed copy in the documents module, once there is one.
    public Guid? DocumentId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record ContractRequest
{
    public required Guid EmployeeId { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required ContractType ContractType { get; init; }

    public required DateOnly StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public bool AutoRenew { get; init; }

    public int NoticePeriodDays { get; init; }

    public decimal? Salary { get; init; }

    // Defaults to the company's own currency, like an expense claim.
    public string? Currency { get; init; }
}

public sealed record SignContractRequest
{
    // "Employee", "HR Director". What they signed as, which is not always what
    // their job title says today.
    public required string SignedAs { get; init; }

    // The drawn signature, uploaded through the files endpoint first. Optional: a
    // recorded click with a timestamp is still a signature, and a company that
    // does not collect drawn ones should not be forced to.
    public Guid? SignatureFileId { get; init; }
}

public sealed record ContractQuery : PagedQuery
{
    public Guid? EmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }

    public ContractStatus? Status { get; init; }

    public ContractType? ContractType { get; init; }

    public bool IncludeArchived { get; init; }

    // Inside their notice window and not auto-renewing. The list somebody has to
    // act on, which is not the same as everything with an end date.
    public bool? ExpiringSoonOnly { get; init; }
}

public enum ContractOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,

    // The lifecycle will not make that move. The message says which.
    NotAllowed,

    TitleRequired,
    ReasonRequired,
    ContentRequired,

    // Ends before it starts.
    EndBeforeStart,

    // A negative salary, or a negative notice period.
    AmountNotPositive,

    CurrencyInvalid,

    // Already signed as that party. Twice would move the date and make a late
    // signature look prompt.
    AlreadySigned,

    // Editing the terms of something people have signed. The signature was against
    // what it said then, so changing it afterwards is forgery by another name.
    SignedContractIsFixed,

    // Already in the documents module. Filing twice would leave the employee with
    // two copies and nothing to say which is current.
    AlreadyFiled,

    // Filing a draft files nothing — there is no agreed copy yet.
    NotReadyToFile,

    // The uploaded PDF does not exist, or belongs to another company.
    FileNotFound,
}

public sealed record ContractResult
{
    public required ContractOutcome Outcome { get; init; }

    public ContractDto? Contract { get; init; }

    public string? Reason { get; init; }

    public bool Succeeded => Outcome == ContractOutcome.Succeeded;

    public static ContractResult Failed(ContractOutcome outcome, string? reason = null) =>
        new() { Outcome = outcome, Reason = reason };

    public static ContractResult Ok(ContractDto contract) =>
        new() { Outcome = ContractOutcome.Succeeded, Contract = contract };
}
