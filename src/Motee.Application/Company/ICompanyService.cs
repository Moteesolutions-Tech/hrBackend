using Motee.Domain.Common;
using Motee.Domain.Company;
using Motee.Domain.Countries;

namespace Motee.Application.Company;

public enum CompanyOutcome
{
    Succeeded,
    NotFound,
    Refused,
}

// The company's own record: who it is, the paperwork proving it, and the shape
// of the organisation.
//
// The permission key for this has existed since the matrix shipped with nothing
// behind it — there were no company endpoints at all, only an audit-policy
// mapping for the Tenant type.
public interface ICompanyService
{
    Task<CompanyProfileDto> ProfileAsync(CancellationToken cancellationToken = default);

    Task<CompanyResult<CompanyProfileDto>> SaveProfileAsync(
        CompanyProfileRequest request,
        CancellationToken cancellationToken = default);

    Task<OrgChartView> OrgChartAsync(
        int? maxLevel = null,
        CancellationToken cancellationToken = default);

    // The notice pinned to the company profile. Read from the announcements
    // module, which already has pinning — a second place to store one company
    // notice would be a second notice, and the screen would show whichever it
    // happened to read.
    Task<PinnedNoticeDto?> PinnedNoticeAsync(CancellationToken cancellationToken = default);
}

// Registration and tax verification. Separate because its two halves are
// performed by different people: the company submits, Motee staff review.
public interface ICompanyVerificationService
{
    Task<IReadOnlyList<VerificationDto>> AllAsync(CancellationToken cancellationToken = default);

    Task<CompanyResult<VerificationDto>> SaveDetailsAsync(
        VerificationKind kind,
        VerificationDetailsRequest request,
        CancellationToken cancellationToken = default);

    Task<CompanyResult<VerificationDto>> SubmitAsync(
        VerificationKind kind,
        CancellationToken cancellationToken = default);

    // Back to draft, so the number or the certificate can be corrected.
    //
    // The way out of a rejection, and the way to pull back a submission nobody
    // has picked up yet. Without it a rejected company is stuck: the record is
    // not editable outside draft, and nothing else could put it there.
    Task<CompanyResult<VerificationDto>> WithdrawAsync(
        VerificationKind kind,
        CancellationToken cancellationToken = default);

    // Platform staff only, which is the whole point. A tenant marking its own
    // registration verified is a label, not a verification.
    Task<CompanyResult<VerificationDto>> ReviewAsync(
        Guid tenantId,
        VerificationKind kind,
        VerificationReviewRequest request,
        CancellationToken cancellationToken = default);

    // Everything waiting on us, across every company. The other half of moving
    // review to the platform: a reviewer needs a queue, or verifications sit
    // submitted until somebody thinks to look at a particular tenant.
    Task<IReadOnlyList<ReviewQueueItemDto>> QueueAsync(
        CancellationToken cancellationToken = default);
}

public sealed record ReviewQueueItemDto
{
    public required Guid TenantId { get; init; }

    public required string CompanyName { get; init; }

    public required string CountryName { get; init; }

    public required VerificationKind Kind { get; init; }

    public required string Label { get; init; }

    public required string Authority { get; init; }

    public string? Number { get; init; }

    public Guid? DocumentFileId { get; init; }

    public required VerificationStage Stage { get; init; }

    public DateOnly? SubmittedOn { get; init; }

    // How long it has been sitting with us. The figure a reviewer sorts by.
    public required int DaysWaiting { get; init; }
}

public sealed record CompanyResult<T>
{
    public required CompanyOutcome Outcome { get; init; }

    public T? Value { get; init; }

    public VerificationRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == CompanyOutcome.Succeeded;

    public static CompanyResult<T> Ok(T value) =>
        new() { Outcome = CompanyOutcome.Succeeded, Value = value };

    public static CompanyResult<T> Failed(CompanyOutcome outcome) => new() { Outcome = outcome };

    public static CompanyResult<T> Refused(VerificationRefusal refusal) =>
        new() { Outcome = CompanyOutcome.Refused, Refusal = refusal };
}

// ── profile ────────────────────────────────────────────────────────────────

public sealed record CompanyProfileDto
{
    public required string Name { get; init; }

    // The registered name where it differs from the trading one.
    public string? LegalName { get; init; }

    public string? Industry { get; init; }

    public required CountryCode CountryCode { get; init; }

    public required string CountryName { get; init; }

    // The head-office address, assembled from the branch marked Headquarters.
    //
    // Not a field of its own: branches already hold address lines, city, region
    // and country, and a company address typed separately is one that disagrees
    // with the site everybody works at.
    public string? Address { get; init; }

    // Which branch that was, so the screen can point at it rather than offering
    // an address box that saves nowhere. Null where no branch is marked as
    // headquarters.
    public Guid? HeadOfficeBranchId { get; init; }

    public string? HeadOfficeName { get; init; }

    public string? ContactEmail { get; init; }

    public string? ContactPhone { get; init; }

    public string? Website { get; init; }

    public string? LogoUrl { get; init; }

    // What the company said at sign-up, as a band.
    public string? SizeBand { get; init; }

    // What is actually on the books. The screen derives its size band from the
    // employee count while the wizard stores a self-reported one, so the two
    // can disagree — both are sent, labelled, rather than one quietly winning.
    public required int Headcount { get; init; }

    public DateTimeOffset? RegisteredAt { get; init; }
}

public sealed record CompanyProfileRequest
{
    public required string Name { get; init; }

    public string? LegalName { get; init; }

    public string? Industry { get; init; }

    public string? ContactEmail { get; init; }

    public string? ContactPhone { get; init; }

    public string? Website { get; init; }
}

// ── verification ───────────────────────────────────────────────────────────

public sealed record VerificationDto
{
    public required VerificationKind Kind { get; init; }

    // From the country profile: Nigeria asks for an RC number from the CAC, the
    // UK for a company number from Companies House.
    public required string Label { get; init; }

    public required string NumberLabel { get; init; }

    public required string Authority { get; init; }

    public required string Description { get; init; }

    public string? Number { get; init; }

    public Guid? DocumentFileId { get; init; }

    public required VerificationStage Stage { get; init; }

    public DateOnly? SubmittedOn { get; init; }

    public string? ReviewedByName { get; init; }

    public DateOnly? ReviewedOn { get; init; }

    public string? DecisionNote { get; init; }

    // Whether the company can still change the number or the document. False
    // once submitted: the reviewer is looking at a particular certificate, and
    // letting it change underneath them means the thing verified is not the
    // thing on file.
    public required bool Editable { get; init; }

    // Whether the company can hand it over now, and what is missing if not.
    public required bool CanSubmit { get; init; }

    public required IReadOnlyList<string> Outstanding { get; init; }

    // Read from the audit trail rather than a history table of its own. Every
    // stage change is already recorded there with who did it and when, and a
    // second copy can disagree with the auditor's.
    public required IReadOnlyList<VerificationEventDto> History { get; init; }
}

public sealed record VerificationEventDto
{
    public required string Event { get; init; }

    public string? By { get; init; }

    public required DateTimeOffset At { get; init; }
}

public sealed record VerificationDetailsRequest
{
    public string? Number { get; init; }

    public Guid? DocumentFileId { get; init; }
}

public sealed record VerificationReviewRequest
{
    public required VerificationStage Stage { get; init; }

    // Required on a rejection. "Rejected" with no reason is a dead end for
    // whoever has to fix it.
    public string? Note { get; init; }
}

// ── pinned notice ──────────────────────────────────────────────────────────

public sealed record PinnedNoticeDto
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }

    public string? PublishedBy { get; init; }
}
