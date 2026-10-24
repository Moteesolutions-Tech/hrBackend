using Motee.Domain.Common;

namespace Motee.Domain.Performance;

// Which direction the feedback travelled.
//
// Worth keeping separately from who wrote it, because the same sentence means
// different things depending on where it came from: "could communicate more
// clearly" from a report and from a manager are not the same observation, and a
// review that flattens them loses the only context that makes upward feedback
// worth collecting.
public enum FeedbackType
{
    Peer,

    // From somebody who reports to them.
    Upward,

    // From somebody who reports to the writer.
    Downward,

    Manager,
}

// Something one person wrote about another's work.
public class PerformanceFeedback : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Who it is about.
    public Guid AboutEmployeeId { get; set; }

    // Who wrote it. Always recorded, whatever is shown: feedback nobody can be
    // held to is feedback nobody should act on, and an unattributable complaint in
    // somebody's review file is the thing HR cannot defend.
    public Guid FromEmployeeId { get; set; }

    public FeedbackType Type { get; set; }

    public required string Message { get; set; }

    // The ask this answers, when it answers one. Null for feedback offered
    // unprompted, which is the other half of how it arrives.
    public Guid? RequestId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum FeedbackRequestStatus
{
    Pending,
    Answered,

    // Said no, rather than leaving it to rot in a list. A colleague who was not in
    // a position to comment should be able to say so without the asker waiting.
    Declined,
}

// Asking a colleague to write something.
public class FeedbackRequest : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The person who wants the feedback — the one asking about themselves.
    public Guid AskedByEmployeeId { get; set; }

    // The colleague being asked.
    public Guid AskedOfEmployeeId { get; set; }

    // "We worked together on the gateway — your view on my technical communication
    // would help." Optional, and the difference between a useful answer and a
    // polite one.
    public string? Context { get; set; }

    public FeedbackRequestStatus Status { get; set; } = FeedbackRequestStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? RespondedAt { get; set; }
}

public static class FeedbackRules
{
    // Nobody reviews themselves through the back door. A self-assessment is its own
    // field on a review, written knowing it is one.
    public static bool IsSelfDirected(Guid about, Guid from) => about == from;

    // Where the feedback sits relative to the person it is about, worked out from
    // the reporting line rather than taken from the client.
    //
    // Asked of the data because the client cannot be trusted with it: a report
    // labelling their own feedback "manager" would give it weight it has not
    // earned, and the label is what a 360 is read through.
    public static FeedbackType Classify(
        Guid aboutEmployeeId,
        Guid fromEmployeeId,
        Guid? aboutManagerId,
        Guid? fromManagerId)
    {
        if (aboutManagerId == fromEmployeeId)
        {
            return FeedbackType.Manager;
        }

        if (fromManagerId == aboutEmployeeId)
        {
            return FeedbackType.Upward;
        }

        return FeedbackType.Peer;
    }
}
