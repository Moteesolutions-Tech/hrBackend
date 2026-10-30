using Motee.Domain.Common;

namespace Motee.Domain.Recruitment;

public enum OfferStatus
{
    Sent,
    Accepted,
    Rejected,

    // Pulled before an answer came back. Distinct from rejected, which is the
    // candidate's decision — a withdrawn offer is the company's.
    Withdrawn,
}

// An offer and what came back.
//
// The negotiation happens over email; the system does not pretend to own the
// conversation. What it owns is the outcome, because that is what gates the
// move to Hired and what anybody reviewing the hire afterwards needs to see.
public class CandidateOffer : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CandidateId { get; set; }

    public OfferStatus Status { get; set; } = OfferStatus.Sent;

    public decimal? Salary { get; set; }

    public string? Currency { get; set; }

    public DateOnly? StartDate { get; set; }

    public string? Notes { get; set; }

    // The offer letter, in stored_files with every other upload.
    public Guid? AttachmentFileId { get; set; }

    public DateTimeOffset SentAt { get; set; }

    public DateTimeOffset? RespondedAt { get; set; }

    public Guid? RespondedByUserId { get; set; }

    // What the candidate said, in their words rather than as a status label.
    public string? ResponseNote { get; set; }
}

public static class Offers
{
    // The one that counts: the most recent. An earlier offer that was declined
    // and then improved should not keep the candidate out of Hired.
    public static CandidateOffer? Latest(IReadOnlyCollection<CandidateOffer> offers) =>
        offers.OrderByDescending(offer => offer.SentAt).FirstOrDefault();

    // Any accepted offer at all, not just the latest. Somebody who accepted and
    // then had a correction sent has still accepted.
    public static bool Accepted(IReadOnlyCollection<CandidateOffer> offers) =>
        offers.Any(offer => offer.Status == OfferStatus.Accepted);

    public static bool Declined(IReadOnlyCollection<CandidateOffer> offers) =>
        Latest(offers)?.Status == OfferStatus.Rejected;

    // Only an offer still awaiting an answer can be answered. Responding twice
    // would overwrite the first answer and its timestamp.
    public static bool CanRespond(OfferStatus status) => status == OfferStatus.Sent;
}
