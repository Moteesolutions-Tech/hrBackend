using Motee.Domain.Common;

namespace Motee.Domain.Company;

public enum VerificationKind
{
    // Incorporation: CAC in Nigeria, Companies House in the UK.
    Registration,

    // TIN in Nigeria, VAT registration in the UK.
    Tax,
}

public enum VerificationStage
{
    // Being filled in. The only stage where the company can still change the
    // number or swap the document.
    Draft,

    // Handed over for checking.
    Submitted,

    UnderReview,

    Verified,

    Rejected,
}

// A company's registration or tax record, and whether anybody has checked it.
//
// The important design decision here is who may move it. The screen gives the
// company a status dropdown containing Verified, so a tenant can mark their own
// registration verified without anybody looking at anything — which is not
// verification, it is a label. A company's own claim about its own
// incorporation is exactly the thing a verification is supposed to test.
//
// So the company drafts and submits; Motee staff review. The product already
// separates those two worlds through PlatformRole, and this is what that
// separation is for.
public class CompanyVerification : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public VerificationKind Kind { get; set; }

    // The registration or tax number, as the company gave it. Null until they
    // fill it in.
    public string? Number { get; set; }

    // The certificate. A verification with no document is somebody's word for
    // it, which is why submitting without one is refused.
    public Guid? DocumentFileId { get; set; }

    public VerificationStage Stage { get; set; } = VerificationStage.Draft;

    public DateOnly? SubmittedOn { get; set; }

    // Motee staff, not a tenant user. Null until somebody has looked.
    public Guid? ReviewedByUserId { get; set; }

    // Copied, not joined — who checked this three years ago must keep reading
    // correctly after that person leaves.
    public string? ReviewedByName { get; set; }

    public DateOnly? ReviewedOn { get; set; }

    // Why it was rejected. Required on a rejection: "Rejected" with no reason
    // is a dead end for whoever has to fix it.
    public string? DecisionNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum VerificationRefusal
{
    None,
    NumberRequired,
    DocumentRequired,
    NotEditableNow,
    StageNotReachable,
    ReasonRequired,
    NotReviewable,
}

public static class VerificationFlow
{
    // Who may move a verification where.
    //
    // Written out as two maps rather than one, because the whole point is that
    // the two parties can make different moves. A single table with a "who"
    // column reads as though the distinction were incidental.
    private static readonly Dictionary<VerificationStage, VerificationStage[]> ByCompany = new()
    {
        [VerificationStage.Draft] = [VerificationStage.Submitted],

        // Withdrawing something submitted but not yet looked at: a company that
        // spots a wrong number should not have to wait to be rejected.
        [VerificationStage.Submitted] = [VerificationStage.Draft],

        // Under review is somebody else's desk. Pulling it back mid-check would
        // waste the reviewer's work and let a company swap the document after
        // it had been seen.
        [VerificationStage.UnderReview] = [],

        [VerificationStage.Verified] = [],

        // Fix and resubmit. The only way out of a rejection, and it goes
        // through Draft so the number and document can be corrected first.
        [VerificationStage.Rejected] = [VerificationStage.Draft],
    };

    private static readonly Dictionary<VerificationStage, VerificationStage[]> ByPlatform = new()
    {
        // Nothing to review until it is handed over. Reviewing a draft would be
        // checking something the company has not finished.
        [VerificationStage.Draft] = [],

        [VerificationStage.Submitted] =
            [VerificationStage.UnderReview, VerificationStage.Verified, VerificationStage.Rejected],

        [VerificationStage.UnderReview] =
            [VerificationStage.Verified, VerificationStage.Rejected],

        // A verification can be withdrawn — a company dissolved, a certificate
        // found to be forged — and that has to be possible without deleting the
        // record of it having been granted.
        [VerificationStage.Verified] = [VerificationStage.Rejected],

        [VerificationStage.Rejected] =
            [VerificationStage.UnderReview, VerificationStage.Verified],
    };

    public static bool CompanyCanMove(VerificationStage from, VerificationStage to) =>
        ByCompany.TryGetValue(from, out VerificationStage[]? next) && next.Contains(to);

    public static bool PlatformCanMove(VerificationStage from, VerificationStage to) =>
        ByPlatform.TryGetValue(from, out VerificationStage[]? next) && next.Contains(to);

    // Whether the company can still change the number or the document.
    //
    // Only in draft. Once it is submitted the reviewer is looking at a
    // particular certificate, and letting the number change underneath them
    // means the thing verified is not the thing on file.
    public static bool IsEditableBy(VerificationStage stage) =>
        stage == VerificationStage.Draft;

    public static bool IsSettled(VerificationStage stage) =>
        stage is VerificationStage.Verified or VerificationStage.Rejected;

    // Whether somebody is waiting on us rather than on them.
    public static bool AwaitsReview(VerificationStage stage) =>
        stage is VerificationStage.Submitted or VerificationStage.UnderReview;
}

public static class VerificationRules
{
    public const int MaximumNumberLength = 60;

    public const int MaximumNoteLength = 2000;

    public static VerificationRefusal CheckDetails(
        CompanyVerification verification,
        string? number)
    {
        if (!VerificationFlow.IsEditableBy(verification.Stage))
        {
            return VerificationRefusal.NotEditableNow;
        }

        string trimmed = (number ?? string.Empty).Trim();

        return trimmed.Length > MaximumNumberLength
            ? VerificationRefusal.NumberRequired
            : VerificationRefusal.None;
    }

    // Both the number and the certificate. The screen lets a company submit
    // with neither — and a submission with nothing attached is a reviewer being
    // asked to verify a blank form.
    public static VerificationRefusal CheckSubmission(CompanyVerification verification)
    {
        if (!VerificationFlow.CompanyCanMove(verification.Stage, VerificationStage.Submitted))
        {
            return VerificationRefusal.StageNotReachable;
        }

        if (string.IsNullOrWhiteSpace(verification.Number))
        {
            return VerificationRefusal.NumberRequired;
        }

        return verification.DocumentFileId is null
            ? VerificationRefusal.DocumentRequired
            : VerificationRefusal.None;
    }

    public static VerificationRefusal CheckReview(
        CompanyVerification verification,
        VerificationStage to,
        string? note)
    {
        if (!VerificationFlow.PlatformCanMove(verification.Stage, to))
        {
            return VerificationRefusal.StageNotReachable;
        }

        // "Rejected" with no reason is a dead end for whoever has to fix it.
        return to == VerificationStage.Rejected && string.IsNullOrWhiteSpace(note)
            ? VerificationRefusal.ReasonRequired
            : VerificationRefusal.None;
    }
}
