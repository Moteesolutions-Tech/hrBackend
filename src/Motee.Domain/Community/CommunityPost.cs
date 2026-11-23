using Motee.Domain.Common;

namespace Motee.Domain.Community;

public enum PostType
{
    Update,
    Shoutout,
    Event,
    Poll,
    Milestone,
}

public enum CelebrationKind
{
    Birthday,
    Anniversary,
    NewHire,
    Promotion,
}

// One post on the community feed.
//
// Only the feed is modelled here. The celebrations rail is birthdays and work
// anniversaries inside a window, which the calendar already derives from
// AnnualDates, and the directory is the employee list the employees module
// already serves — so neither gets a store of its own. A celebration *post* is
// somebody choosing to write about one, which is a different thing from the
// calendar knowing the date.
public class CommunityPost : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public PostType PostType { get; set; }

    public Guid AuthorEmployeeId { get; set; }

    public required string Content { get; set; }

    public bool IsPinned { get; set; }

    // Only for an event post, and required there: an event with no date is an
    // announcement.
    public DateOnly? EventOn { get; set; }

    public string? EventLocation { get; set; }

    // Only for a poll, and required there. The options are their own rows, so
    // a vote can point at one.
    public string? PollQuestion { get; set; }

    // Who the post is about, where it is about somebody.
    public CelebrationKind? CelebrationKind { get; set; }

    public Guid? CelebrationPersonId { get; set; }

    public string? CelebrationDetail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One poll answer. A row rather than an entry in a jsonb list, so a vote has
// something to reference and a renamed option does not orphan the votes cast
// for it.
public class PostPollOption : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid PostId { get; set; }

    public required string Label { get; set; }

    public int Sequence { get; set; }
}

// One person's vote. Single choice: the client keeps a list of voters per
// option, which lets the same person appear under two answers and makes the
// shares add up to more than a hundred.
public class PostPollVote : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid PostId { get; set; }

    public Guid OptionId { get; set; }

    public Guid EmployeeId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

// One person's reaction. The feed has a single kind — a like — so unlike kudos
// there is no type on the row.
public class PostReaction : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid PostId { get; set; }

    public Guid EmployeeId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public class PostComment : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid PostId { get; set; }

    public Guid AuthorEmployeeId { get; set; }

    public required string Message { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum PostRefusal
{
    None,
    ContentRequired,
    ContentTooLong,
    EventDateRequired,
    PollQuestionRequired,
    PollNeedsTwoOptions,
    TooManyPollOptions,
    CelebrationPersonRequired,
    CommentRequired,
    CommentTooLong,

    // Voting on a post that is not a poll, or on an option from another poll.
    NotAPoll,
    UnknownOption,

    // Editing or deleting somebody else's post without the moderation grant.
    NotYours,
}

public static class CelebrationKinds
{
    public static bool TryParse(string? value, out CelebrationKind kind)
    {
        kind = CelebrationKind.Birthday;

        string normalised = (value ?? string.Empty)
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal);

        return Enum.TryParse(normalised, ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }
}

public sealed record PollTally
{
    public required string Label { get; init; }

    public required int Votes { get; init; }
}

public sealed record PollResult
{
    public required string Label { get; init; }

    public required int Votes { get; init; }

    // Null until somebody votes. Nought per cent across every option draws
    // empty bars that read as "everybody abstained" rather than "no votes
    // yet".
    public decimal? Share { get; init; }
}

public sealed record PostFacts
{
    public required PostType PostType { get; init; }

    public required int Likes { get; init; }

    public required int Comments { get; init; }

    public required int PollVotes { get; init; }
}

public sealed record FeedTally
{
    public required int Posts { get; init; }

    public required int Reactions { get; init; }

    public required int Comments { get; init; }

    public required int PollVotes { get; init; }

    public required int Members { get; init; }
}

public static class CommunityPosts
{
    public const int LongestContent = 4000;

    public const int LongestComment = 2000;

    public const int MostPollOptions = 10;

    public const int FewestPollOptions = 2;

    public static PostRefusal Refusal(CommunityPost post, IReadOnlyList<string> options)
    {
        string content = post.Content?.Trim() ?? string.Empty;

        if (content.Length == 0)
        {
            return PostRefusal.ContentRequired;
        }

        if (content.Length > LongestContent)
        {
            return PostRefusal.ContentTooLong;
        }

        // An event with no date is an announcement, and there would be nothing
        // to put on a calendar.
        if (post.PostType == PostType.Event && post.EventOn is null)
        {
            return PostRefusal.EventDateRequired;
        }

        if (post.PostType == PostType.Poll)
        {
            if (string.IsNullOrWhiteSpace(post.PollQuestion))
            {
                return PostRefusal.PollQuestionRequired;
            }

            IReadOnlyList<string> normalised = NormaliseOptions(options);

            if (normalised.Count < FewestPollOptions)
            {
                return PostRefusal.PollNeedsTwoOptions;
            }

            if (normalised.Count > MostPollOptions)
            {
                return PostRefusal.TooManyPollOptions;
            }
        }

        // Naming a kind of celebration without naming who it is about leaves
        // nothing to celebrate.
        return post.CelebrationKind is not null && post.CelebrationPersonId is null
            ? PostRefusal.CelebrationPersonRequired
            : PostRefusal.None;
    }

    public static PostRefusal CommentRefusal(string? message)
    {
        string trimmed = message?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return PostRefusal.CommentRequired;
        }

        return trimmed.Length > LongestComment
            ? PostRefusal.CommentTooLong
            : PostRefusal.None;
    }

    // Trimmed, lower-cased and de-duplicated, in the order given. Two options
    // worded the same make a result nobody can read.
    public static IReadOnlyList<string> NormaliseOptions(IEnumerable<string?> options)
    {
        List<string> kept = [];

        foreach (string? option in options)
        {
            string trimmed = option?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
            {
                continue;
            }

            string folded = trimmed.ToLowerInvariant();

            if (!kept.Contains(folded, StringComparer.Ordinal))
            {
                kept.Add(folded);
            }
        }

        return kept;
    }

    public static IReadOnlyList<PollResult> PollResults(IReadOnlyList<PollTally> tallies)
    {
        int total = tallies.Sum(row => row.Votes);

        return
        [
            .. tallies.Select(row => new PollResult
            {
                Label = row.Label,
                Votes = row.Votes,
                Share = total == 0
                    ? null
                    : Math.Round(row.Votes / (decimal)total * 100m, 1, MidpointRounding.AwayFromZero),
            }),
        ];
    }
}

public static class CommunityFeed
{
    public static FeedTally Of(IReadOnlyList<PostFacts> posts, int members) => new()
    {
        Posts = posts.Count,
        Reactions = posts.Sum(post => post.Likes),
        Comments = posts.Sum(post => post.Comments),
        PollVotes = posts.Sum(post => post.PollVotes),
        Members = members,
    };
}
