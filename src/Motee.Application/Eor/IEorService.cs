using Motee.Application.Common;
using Motee.Domain.Eor;

namespace Motee.Application.Eor;

public enum EorOutcome
{
    Succeeded,
    NotFound,
    Refused,
}

// Workers whose legal employer is a third-party provider in their own country.
//
// An engagement against an existing employee rather than a worker register of
// its own. The screen collects the whole employee profile again — legal names,
// date of birth, nationality, contact, role, department — and employees already
// holds every one of those columns, so a second table would be a second people
// register with nothing keeping the two in step.
public interface IEorService
{
    Task<IReadOnlyList<EorProviderDto>> ProvidersAsync(
        CancellationToken cancellationToken = default);

    Task<EorProviderDto?> ProviderAsync(Guid id, CancellationToken cancellationToken = default);

    Task<EorResult<EorProviderDto>> AddProviderAsync(
        EorProviderRequest request,
        CancellationToken cancellationToken = default);

    Task<EorResult<EorProviderDto>> UpdateProviderAsync(
        Guid id,
        EorProviderRequest request,
        CancellationToken cancellationToken = default);

    // Refused while anybody is still engaged through them, rather than leaving
    // workers attached to a partner the company has stopped using.
    Task<EorResult<EorProviderDto>> RetireProviderAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<PagedResult<EorWorkerDto>> WorkersAsync(
        EorWorkerQuery query,
        CancellationToken cancellationToken = default);

    Task<EorWorkerDto?> WorkerAsync(Guid id, CancellationToken cancellationToken = default);

    Task<EorResult<EorWorkerDto>> EngageAsync(
        EorEngagementRequest request,
        CancellationToken cancellationToken = default);

    Task<EorResult<EorWorkerDto>> UpdateEngagementAsync(
        Guid id,
        EorEngagementRequest request,
        CancellationToken cancellationToken = default);

    // Its own route rather than an end date on the update. Ending an engagement
    // is a decision with a date, and the employee's own record follows.
    Task<EorResult<EorWorkerDto>> EndAsync(
        Guid id,
        EndEngagementRequest request,
        CancellationToken cancellationToken = default);

    Task<EorOutcome> RemoveAsync(Guid id, CancellationToken cancellationToken = default);

    Task<EorSummary> SummaryAsync(CancellationToken cancellationToken = default);
}

// The compliance register: whether each worker has the paperwork that makes
// employing them lawful.
public interface IEorComplianceService
{
    Task<IReadOnlyList<ComplianceRowDto>> RegisterAsync(
        CancellationToken cancellationToken = default);

    Task<EorResult<EorWorkerDto>> RecordAsync(
        Guid engagementId,
        ComplianceKey key,
        ComplianceRequest request,
        CancellationToken cancellationToken = default);
}

// Invoices a provider has sent, and whether they match what we engaged.
public interface IEorBillingService
{
    Task<IReadOnlyList<EorInvoiceDto>> InvoicesAsync(
        Guid? providerId = null,
        CancellationToken cancellationToken = default);

    Task<EorResult<EorInvoiceDto>> RecordAsync(
        EorInvoiceRequest request,
        CancellationToken cancellationToken = default);

    Task<EorResult<EorInvoiceDto>> UpdateAsync(
        Guid id,
        EorInvoiceRequest request,
        CancellationToken cancellationToken = default);

    Task<EorResult<EorInvoiceDto>> SettleAsync(
        Guid id,
        SettleInvoiceRequest request,
        CancellationToken cancellationToken = default);

    Task<EorOutcome> RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record EorResult<T>
{
    public required EorOutcome Outcome { get; init; }

    public T? Value { get; init; }

    public EorRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == EorOutcome.Succeeded;

    public static EorResult<T> Ok(T value) =>
        new() { Outcome = EorOutcome.Succeeded, Value = value };

    public static EorResult<T> Failed(EorOutcome outcome) => new() { Outcome = outcome };

    public static EorResult<T> Refused(EorRefusal refusal) =>
        new() { Outcome = EorOutcome.Refused, Refusal = refusal };
}

// ── providers ──────────────────────────────────────────────────────────────

public sealed record EorProviderDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required EorProviderStatus Status { get; init; }

    public required IReadOnlyList<string> CountriesCovered { get; init; }

    public required decimal ManagementFeePercent { get; init; }

    public DateOnly? PartneredOn { get; init; }

    public string? Website { get; init; }

    // Counted from the engagements. The screen stores this on the provider and
    // the figure sits on the card beside the list it disagrees with.
    public required int WorkerCount { get; init; }
}

public sealed record EorProviderRequest
{
    public required string Name { get; init; }

    public EorProviderStatus Status { get; init; } = EorProviderStatus.Active;

    public IReadOnlyList<string> CountriesCovered { get; init; } = [];

    public decimal ManagementFeePercent { get; init; }

    public DateOnly? PartneredOn { get; init; }

    public string? Website { get; init; }
}

// ── workers ────────────────────────────────────────────────────────────────

public sealed record EorWorkerDto
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    // From the employee record, not stored again here.
    public required string Name { get; init; }

    public string? Initials { get; init; }

    public string? Email { get; init; }

    public string? JobTitle { get; init; }

    public string? Department { get; init; }

    public required Guid ProviderId { get; init; }

    public required string ProviderName { get; init; }

    public required string CountryOfEngagement { get; init; }

    public required EngagementStage Stage { get; init; }

    public required DateOnly StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public required PayFrequency PayFrequency { get; init; }

    public required string Currency { get; init; }

    public required decimal GrossMonthly { get; init; }

    public required decimal EmployerCostMonthly { get; init; }

    // From the provider's percentage, so raising their rate moves every
    // worker's cost. The screen types this per worker.
    public required decimal ManagementFee { get; init; }

    public required decimal TotalMonthly { get; init; }

    public decimal? UsdRate { get; init; }

    public DateOnly? RateOn { get; init; }

    // Null where no rate has been recorded, rather than zero: a worker
    // contributing nothing to the spend total is a worker whose cost has been
    // forgotten.
    public decimal? TotalUsd { get; init; }

    public required bool RateIsStale { get; init; }

    public required IReadOnlyList<ComplianceItemDto> Compliance { get; init; }

    // What stands between this worker and being lawfully in post, as sentences.
    public required IReadOnlyList<string> Blockers { get; init; }

    public required bool FullyCompliant { get; init; }
}

public sealed record EorWorkerQuery : PagedQuery
{
    public Guid? ProviderId { get; init; }

    public string? Country { get; init; }

    public EngagementStage? Stage { get; init; }

    // Only the workers with something outstanding that stops them working.
    public bool? WithBlockers { get; init; }

    public string? Search { get; init; }
}

public sealed record EorEngagementRequest
{
    public required Guid EmployeeId { get; init; }

    public required Guid ProviderId { get; init; }

    public required string CountryOfEngagement { get; init; }

    public required string Currency { get; init; }

    public decimal GrossMonthly { get; init; }

    public decimal EmployerCostMonthly { get; init; }

    public PayFrequency PayFrequency { get; init; } = PayFrequency.Monthly;

    public required DateOnly StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public decimal? UsdRate { get; init; }

    public DateOnly? RateOn { get; init; }
}

public sealed record EndEngagementRequest
{
    public required DateOnly EndDate { get; init; }

    public string? Reason { get; init; }
}

// ── compliance ─────────────────────────────────────────────────────────────

public sealed record ComplianceItemDto
{
    public required ComplianceKey Key { get; init; }

    public required string Label { get; init; }

    // What was recorded.
    public required ComplianceState State { get; init; }

    // What it amounts to today, which is not the same thing: a permit ticked
    // complete with an expiry in the past is expired.
    public required ComplianceStanding Standing { get; init; }

    public string? Note { get; init; }

    public DateOnly? ExpiresOn { get; init; }

    public DateOnly? VerifiedOn { get; init; }

    public Guid? EvidenceFileId { get; init; }

    // Whether this requirement is one that lapses, so the form offers an expiry
    // only where there is one to give.
    public required bool Expires { get; init; }

    public required bool BlocksWork { get; init; }

    // Marked complete with nothing attached. Reported rather than refused — a
    // provider confirming something by email is still a record — so the
    // register can show what rests on somebody's word.
    public required bool Unevidenced { get; init; }
}

public sealed record ComplianceRowDto
{
    public required Guid EngagementId { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string Name { get; init; }

    public string? Initials { get; init; }

    public required string CountryOfEngagement { get; init; }

    public required string ProviderName { get; init; }

    public required EngagementStage Stage { get; init; }

    public required IReadOnlyList<ComplianceItemDto> Items { get; init; }

    public required IReadOnlyList<string> Blockers { get; init; }
}

public sealed record ComplianceRequest
{
    public required ComplianceState State { get; init; }

    public string? Note { get; init; }

    public DateOnly? ExpiresOn { get; init; }

    public DateOnly? VerifiedOn { get; init; }

    public Guid? EvidenceFileId { get; init; }
}

// ── billing ────────────────────────────────────────────────────────────────

public sealed record EorInvoiceDto
{
    public required Guid Id { get; init; }

    public required Guid ProviderId { get; init; }

    public required string ProviderName { get; init; }

    public required DateOnly Period { get; init; }

    public required string PeriodLabel { get; init; }

    public string? Reference { get; init; }

    public required string Currency { get; init; }

    public required decimal GrossPay { get; init; }

    public required decimal EmployerCosts { get; init; }

    public required decimal ManagementFee { get; init; }

    public required decimal Total { get; init; }

    public required InvoiceStanding Standing { get; init; }

    public required int DaysOverdue { get; init; }

    public required DateOnly IssuedOn { get; init; }

    public required DateOnly DueOn { get; init; }

    public DateOnly? PaidOn { get; init; }

    public Guid? DocumentFileId { get; init; }

    // What the provider billed for.
    public required int BilledWorkerCount { get; init; }

    // What we had engaged with them that month. The comparison is the reason
    // to hold invoices at all, and the screen renders them checked against
    // nothing — so a provider billing for nine when seven are engaged reads as
    // a normal month.
    public required int EngagedWorkerCount { get; init; }

    public bool HeadcountMatches => BilledWorkerCount == EngagedWorkerCount;

    // Whether the provider's own total equals their own parts. Their figure is
    // kept either way: a credit note or a one-off charge shows up here, and
    // replacing it with our sum would hide a dispute rather than raise it.
    public required bool TotalAddsUp { get; init; }

    public required decimal SumOfParts { get; init; }
}

public sealed record EorInvoiceRequest
{
    public required Guid ProviderId { get; init; }

    // The month billed for. Any date within it is accepted and stored as the
    // first — a period is a month, not a day.
    public required DateOnly Period { get; init; }

    public string? Reference { get; init; }

    public required string Currency { get; init; }

    public int BilledWorkerCount { get; init; }

    public decimal GrossPay { get; init; }

    public decimal EmployerCosts { get; init; }

    public decimal ManagementFee { get; init; }

    public decimal Total { get; init; }

    public required DateOnly IssuedOn { get; init; }

    public required DateOnly DueOn { get; init; }

    public Guid? DocumentFileId { get; init; }
}

public sealed record SettleInvoiceRequest
{
    // Null reopens it, for an invoice marked paid by mistake.
    public DateOnly? PaidOn { get; init; }
}
