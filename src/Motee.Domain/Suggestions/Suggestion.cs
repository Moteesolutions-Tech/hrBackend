using Motee.Domain.Common;

namespace Motee.Domain.Suggestions;

public enum SuggestionStatus
{
    Submitted,
    UnderReview,
    Accepted,
    InProgress,
    Implemented,
    Declined,
}

public enum SuggestionCategory
{
    Culture,
    Process,
    Benefits,
    Tools,
    Wellbeing,
    Management,
    Other,
}

public enum SuggestionPriority
{
    Low,
    Medium,
    High,
}

// One suggestion in the box.
//
// The submitter is always recorded and is not always disclosed. An anonymous
// suggestion still has to belong to somebody — so the person can find their
// own submissions, and so the same person cannot be counted twice — but the
// name never leaves the service. The client's own type carries `isAnonymous`
// next to submitterName, submitterInitials and submitterDept, which means the
// identity reaches the browser and the screen is trusted not to draw it.
//
// Upvotes are rows, not a counter: the client keeps both an `upvotes` number
// and an `upvotedBy` list, which is two places for one fact and the second was
// always empty.
public class Suggestion : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Title { get; set; }

    public required string Description { get; set; }

    public SuggestionCategory Category { get; set; }

    public SuggestionStatus Status { get; set; } = SuggestionStatus.Submitted;

    // HR's triage, not the submitter's claim about their own idea.
    public SuggestionPriority Priority { get; set; } = SuggestionPriority.Medium;

    // Recorded whether or not it is disclosed. See the class comment.
    public Guid SubmitterEmployeeId { get; set; }

    public bool IsAnonymous { get; set; }

    // HR's published reply, which the submitter and everybody else can read.
    public string? AdminResponse { get; set; }

    public string? ImplementationNotes { get; set; }

    // Held at the top of the board by HR.
    public bool IsFeatured { get; set; }

    // Set by the move into a terminal status and cleared by the move out of
    // one, so the date and the status can never disagree. The client has a
    // free status dropdown and a separate resolvedAt that nothing keeps in
    // step.
    public DateOnly? ResolvedOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One person's upvote. A row rather than a counter, so "how many" and "did I
// already vote" are the same fact asked two ways, and a double-click cannot
// inflate the number.
public class SuggestionVote : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SuggestionId { get; set; }

    public Guid EmployeeId { get; set; }

    public DateOnly VotedOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public class SuggestionComment : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SuggestionId { get; set; }

    public Guid AuthorEmployeeId { get; set; }

    public required string Message { get; set; }

    // Whether this was posted as an official reply. Stored rather than derived
    // from the author's current permissions: somebody who leaves HR should not
    // retroactively un-badge the answers they gave while they were there.
    public bool IsOfficial { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum SuggestionRefusal
{
    None,
    TitleRequired,
    TitleTooLong,
    DescriptionRequired,
    DescriptionTooLong,
    AdminResponseTooLong,
    ImplementationNotesTooLong,
    CommentRequired,
    CommentTooLong,

    // Voting for, or commenting on, something the caller cannot see.
    NotVisible,
}

public static class SuggestionStatuses
{
    // The client's mapper accepts "new", "under-review" and "rejected"
    // alongside the current spellings, so a row written by an earlier version
    // keeps reading.
    public static bool TryParse(string? value, out SuggestionStatus status)
    {
        status = SuggestionStatus.Submitted;

        switch (value?.Trim().ToLowerInvariant().Replace('-', '_'))
        {
            case "new":
            case "submitted":
                return true;
            case "under_review":
                status = SuggestionStatus.UnderReview;
                return true;
            case "accepted":
                status = SuggestionStatus.Accepted;
                return true;
            case "in_progress":
                status = SuggestionStatus.InProgress;
                return true;
            case "implemented":
                status = SuggestionStatus.Implemented;
                return true;
            case "rejected":
            case "declined":
                status = SuggestionStatus.Declined;
                return true;
            default:
                return false;
        }
    }
}

public static class SuggestionRules
{
    // The form's own limits.
    public const int ShortestTitle = 5;

    public const int LongestTitle = 100;

    public const int ShortestDescription = 20;

    public const int LongestDescription = 1000;

    public const int LongestResponse = 2000;

    public const int LongestComment = 1000;

    // What "trending" means here: this many upvotes inside the window. The
    // client calls anything over ten votes trending for ever, which leaves a
    // two-year-old suggestion with eleven votes sitting in the trending rail.
    public const int TrendingVotes = 10;

    public const int TrendingWindowDays = 14;

    public static SuggestionRefusal Refusal(Suggestion suggestion)
    {
        string title = suggestion.Title?.Trim() ?? string.Empty;
        string description = suggestion.Description?.Trim() ?? string.Empty;

        if (title.Length < ShortestTitle)
        {
            return SuggestionRefusal.TitleRequired;
        }

        if (title.Length > LongestTitle)
        {
            return SuggestionRefusal.TitleTooLong;
        }

        if (description.Length < ShortestDescription)
        {
            return SuggestionRefusal.DescriptionRequired;
        }

        if (description.Length > LongestDescription)
        {
            return SuggestionRefusal.DescriptionTooLong;
        }

        if ((suggestion.AdminResponse?.Trim().Length ?? 0) > LongestResponse)
        {
            return SuggestionRefusal.AdminResponseTooLong;
        }

        return (suggestion.ImplementationNotes?.Trim().Length ?? 0) > LongestResponse
            ? SuggestionRefusal.ImplementationNotesTooLong
            : SuggestionRefusal.None;
    }

    public static SuggestionRefusal CommentRefusal(string? message)
    {
        string trimmed = message?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return SuggestionRefusal.CommentRequired;
        }

        return trimmed.Length > LongestComment
            ? SuggestionRefusal.CommentTooLong
            : SuggestionRefusal.None;
    }

    // Whether this caller may be told who wrote it.
    //
    // Nobody is exempt — not HR, not the account owner. A box that can be
    // unmasked by whoever is senior enough is not an anonymous box, it is a box
    // with a queue for unmasking. The one exception is the author, who already
    // knows and needs to find their own submissions.
    public static bool NamesSubmitter(Suggestion suggestion, Guid viewerEmployeeId) =>
        !suggestion.IsAnonymous || suggestion.SubmitterEmployeeId == viewerEmployeeId;

    public static bool IsResolved(SuggestionStatus status) =>
        status is SuggestionStatus.Implemented or SuggestionStatus.Declined;

    // The resolved date, given where it is going and where it already was.
    // Moving between the two terminal states keeps the day it was first
    // closed, because that is the date "when was this decided" means.
    public static DateOnly? ResolvedOn(SuggestionStatus status, DateOnly? was, DateOnly asAt)
    {
        if (!IsResolved(status))
        {
            return null;
        }

        return was ?? asAt;
    }

    public static bool IsTrending(IReadOnlyList<DateOnly> voteDates, DateOnly asAt)
    {
        DateOnly from = asAt.AddDays(-TrendingWindowDays);

        return voteDates.Count(date => date > from) >= TrendingVotes;
    }
}

// One suggestion, reduced to what the board's figures need.
public sealed record SuggestionFacts
{
    public required SuggestionStatus Status { get; init; }

    public required int Upvotes { get; init; }
}

public sealed record SuggestionTally
{
    public required int Total { get; init; }

    public required int Implemented { get; init; }

    // Under review and accepted together, which is the client's own rule for
    // this card: both mean "somebody is looking at it".
    public required int UnderReview { get; init; }

    public required int Declined { get; init; }

    public required decimal AverageUpvotes { get; init; }
}

public static class SuggestionBoard
{
    public static SuggestionTally Of(IReadOnlyList<SuggestionFacts> suggestions) => new()
    {
        Total = suggestions.Count,
        Implemented = suggestions.Count(row => row.Status == SuggestionStatus.Implemented),
        UnderReview = suggestions.Count(row =>
            row.Status is SuggestionStatus.UnderReview or SuggestionStatus.Accepted),
        Declined = suggestions.Count(row => row.Status == SuggestionStatus.Declined),

        // Zero rather than a division by nothing. An empty box has no average
        // to report.
        AverageUpvotes = suggestions.Count == 0
            ? 0m
            : Math.Round(
                suggestions.Sum(row => row.Upvotes) / (decimal)suggestions.Count,
                1,
                MidpointRounding.AwayFromZero),
    };
}
