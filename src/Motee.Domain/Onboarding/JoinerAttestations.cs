namespace Motee.Domain.Onboarding;

// Which privacy notice is currently in force.
//
// Owned by the backend, not taken from the caller. A client that supplied its own version
// could record agreement to a notice that never existed, which is precisely the claim
// consent is supposed to evidence.
//
// Bumped by hand when the notice text changes, and deliberately so: the text ships with
// the application, and a version that moved on its own would be a version nobody could
// map back to words. Consent already given stays stamped with the version it was given
// under — that is the whole point of recording it.
public static class PrivacyNotice
{
    public const string CurrentVersion = "2026-01";
}

// Accepting the privacy notice before entering anything.
//
// Recorded rather than assumed, and with the version, because the question an auditor or
// a subject-access request asks is not "did they agree" but "what did they agree to". A
// bare boolean cannot answer that once the notice has been reworded, and it will be.
public sealed record PrivacyConsent
{
    public required DateTimeOffset AcceptedAt { get; init; }

    public required string NoticeVersion { get; init; }

    // Where from. Kept because consent is the one thing somebody may later deny giving,
    // and nullable because a deployment behind a proxy that strips it should record
    // nothing rather than record something wrong.
    public string? IpAddress { get; init; }
}

// Signing off the completed pack.
//
// The joiner typing their own name is the attestation: everything above it is declared
// true. Not a signature image — that is the Docu-Sign module's job, and a typed name with
// a timestamp is what the form actually collects.
public sealed record JoinerDeclaration
{
    public required string SignedName { get; init; }

    public required DateTimeOffset SignedAt { get; init; }

    public string? IpAddress { get; init; }
}

// What a new starter is asked to upload.
//
// A fixed list because each slot has its own meaning to somebody checking it: "right to
// work" is a legal check, "proof of address" is a different one, and a single untyped
// pile of files makes both unanswerable without opening every attachment.
public enum JoinerDocumentKind
{
    Passport,
    DrivingLicence,

    // The UK's statutory pre-employment check. Its own slot rather than being inferred
    // from a passport, because a passport is one of several acceptable proofs and the
    // check is the thing being evidenced.
    RightToWork,

    Visa,
    ProofOfAddress,
    Qualifications,

    // The previous employer's leaving certificate. Uploaded here as evidence; the figures
    // transcribed from it live on the starter tax record.
    P45,

    // Nigeria asks joiners for two guarantors and identification for each. UK tenants
    // never see these slots.
    Guarantor1Id,
    Guarantor2Id,
}
