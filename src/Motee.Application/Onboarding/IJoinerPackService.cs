using Motee.Domain.Common;
using Motee.Domain.Onboarding;

namespace Motee.Application.Onboarding;

// The compliance half of a joiner's pack: the privacy notice they accepted, the documents
// they uploaded, their guarantors, their tax declaration, and the draft they left behind.
//
// Separate from IOnboardingService, which owns the process — stages, submission, review.
// This owns what was collected. The two meet at submission, where the pack has to be
// complete before the process can move.
public interface IJoinerPackService
{
    // What this country asks for, and which slots cannot be skipped. Served because the
    // list differs by country, and a slot the interface offers that the backend will not
    // accept is a form somebody cannot finish.
    Task<JoinerPackRequirementsDto> RequirementsAsync(CancellationToken cancellationToken = default);

    Task<JoinerPackDto?> GetAsync(Guid onboardingRecordId, CancellationToken cancellationToken = default);

    // Accepting the privacy notice. Gates the rest of the form, so it is its own call
    // rather than a field on the submission — consent given at the end would not be
    // consent to collect what was already entered.
    //
    // The version is not a parameter: the backend stamps whichever notice is in force.
    // A caller that chose it could record agreement to a notice that never existed.
    Task<JoinerPackResult> AcceptPrivacyNoticeAsync(
        Guid onboardingRecordId,
        CancellationToken cancellationToken = default);

    // Replaces whatever is in that slot. A rejected passport is re-uploaded, not added
    // alongside, so nobody has to work out which of two is live.
    Task<JoinerPackResult> AttachDocumentAsync(
        Guid onboardingRecordId,
        JoinerDocumentKind kind,
        Guid fileId,
        CancellationToken cancellationToken = default);

    Task<JoinerPackOutcome> RemoveDocumentAsync(
        Guid onboardingRecordId,
        JoinerDocumentKind kind,
        CancellationToken cancellationToken = default);

    // Both guarantors at once. They are collected as a pair on one step, and saving one
    // at a time would let a joiner submit with only the first.
    Task<JoinerPackResult> SaveGuarantorsAsync(
        Guid onboardingRecordId,
        IReadOnlyList<GuarantorRequest> guarantors,
        CancellationToken cancellationToken = default);

    // The tax declaration. The code is derived here, never accepted from the caller: a
    // joiner does not know their tax code and an HR admin should not be guessing it.
    Task<JoinerPackResult> SaveStarterTaxAsync(
        Guid onboardingRecordId,
        StarterTaxRequest request,
        CancellationToken cancellationToken = default);

    // "Save & finish later". Stored verbatim and never validated, because a draft that
    // had to be valid would not be a draft.
    Task<JoinerPackOutcome> SaveDraftAsync(
        Guid onboardingRecordId,
        string draftJson,
        int? step,
        CancellationToken cancellationToken = default);

    // Signs the pack off and records the attestation. Refuses while anything required is
    // outstanding, naming what — "incomplete" alone sends somebody hunting.
    Task<JoinerPackResult> DeclareAsync(
        Guid onboardingRecordId,
        string signedName,
        CancellationToken cancellationToken = default);
}

public enum JoinerPackOutcome
{
    Succeeded,
    NotFound,

    // The privacy notice has not been accepted, so there is no basis for collecting any
    // of this yet.
    ConsentMissing,

    // A document slot this country does not use, or a guarantor for a UK tenant.
    NotApplicable,

    // The file is not an uploaded joiner document belonging to this tenant.
    UnknownFile,

    // Something required is still outstanding. The result names it.
    Incomplete,

    // A P45 and a checklist together, or a branch whose details were not supplied.
    ContradictorySource,

    // Fewer than the two guarantors Nigeria asks for, or a position outside 1–2.
    InvalidGuarantors,
}

public sealed record JoinerPackResult
{
    public required JoinerPackOutcome Outcome { get; init; }

    public JoinerPackDto? Pack { get; init; }

    // What is still missing, when the outcome is Incomplete. In words, because a screen
    // showing "Guarantor 2 — ID Document" is actionable and "incomplete" is not.
    public IReadOnlyList<string> Outstanding { get; init; } = [];

    public bool Succeeded => Outcome == JoinerPackOutcome.Succeeded;

    public static JoinerPackResult Failed(
        JoinerPackOutcome outcome,
        IReadOnlyList<string>? outstanding = null) =>
        new() { Outcome = outcome, Outstanding = outstanding ?? [] };

    public static JoinerPackResult Ok(JoinerPackDto pack) =>
        new() { Outcome = JoinerPackOutcome.Succeeded, Pack = pack };
}

public sealed record GuarantorRequest
{
    public required int Position { get; init; }

    public required string Name { get; init; }

    public required string Relationship { get; init; }

    public string? Occupation { get; init; }

    public string? Address { get; init; }

    public string? Phone { get; init; }
}

public sealed record StarterTaxRequest
{
    public required StarterTaxSource Source { get; init; }

    public P45Details? P45 { get; init; }

    // The answers, not the declaration. The declaration is resolved from them — somebody
    // asked to pick A, B or C directly will pick the wrong one.
    public EmployeeStatementAnswers? EmployeeStatement { get; init; }

    public StudentLoanDetails? StudentLoan { get; init; }
}

public sealed record JoinerPackRequirementsDto
{
    public required string CountryCode { get; init; }

    public required IReadOnlyList<JoinerDocumentSpec> Documents { get; init; }

    // Nigeria asks for two; UK tenants are not shown the step at all.
    public required int GuarantorsRequired { get; init; }

    // UK PAYE capture. Nigerian tenants declare a TIN and a state tax office instead,
    // which is a different model rather than this one left blank.
    public required bool CollectsStarterTax { get; init; }

    public required string PrivacyNoticeVersion { get; init; }
}

public sealed record JoinerPackDto
{
    public required Guid OnboardingRecordId { get; init; }

    public PrivacyConsent? PrivacyConsent { get; init; }

    public JoinerDeclaration? Declaration { get; init; }

    public required IReadOnlyList<JoinerDocumentDto> Documents { get; init; }

    public required IReadOnlyList<GuarantorDto> Guarantors { get; init; }

    public StarterTaxDto? StarterTax { get; init; }

    public string? DraftJson { get; init; }

    public int? DraftStep { get; init; }

    public DateTimeOffset? DraftSavedAt { get; init; }

    // Everything still outstanding, so the wizard can show progress rather than only
    // finding out at the end.
    public required IReadOnlyList<string> Outstanding { get; init; }

    public bool IsComplete => Outstanding.Count == 0 && PrivacyConsent is not null;
}

public sealed record JoinerDocumentDto
{
    public required JoinerDocumentKind Kind { get; init; }

    public required Guid FileId { get; init; }

    public required string FileName { get; init; }

    // Signed on read, like every other file link here.
    public string? Url { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }
}

public sealed record GuarantorDto
{
    public required Guid Id { get; init; }

    public required int Position { get; init; }

    public required string Name { get; init; }

    public required string Relationship { get; init; }

    public string? Occupation { get; init; }

    public string? Address { get; init; }

    public string? Phone { get; init; }
}

public sealed record StarterTaxDto
{
    public required StarterTaxSource Source { get; init; }

    public required DateOnly EmploymentStartDate { get; init; }

    public P45Details? P45 { get; init; }

    public StarterChecklistDetails? StarterChecklist { get; init; }

    public DerivedTax? Derived { get; init; }

    public required DateOnly RetainUntil { get; init; }
}
