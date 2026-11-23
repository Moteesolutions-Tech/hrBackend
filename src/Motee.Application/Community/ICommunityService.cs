using Motee.Application.Common;
using Motee.Domain.Community;

namespace Motee.Application.Community;

// The community feed.
//
// Only the feed. The celebrations rail is birthdays and anniversaries inside a
// window, which the calendar module already derives — it should read
// GET /calendar and filter to the birthday and anniversary categories. The
// directory is the employee list, which the employees module already serves,
// and the client's `skills` array on it is hardcoded empty because nothing in
// the product records skills yet.
public interface ICommunityService
{
    Task<PagedResult<PostDto>> FeedAsync(
        FeedQuery query,
        CancellationToken cancellationToken = default);

    Task<PostDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // Posts, reactions, comments, poll votes and the headcount the feed
    // reaches.
    Task<FeedTally> SummaryAsync(CancellationToken cancellationToken = default);

    Task<PostResult> PostAsync(
        PostRequest request,
        CancellationToken cancellationToken = default);

    // The author's own to edit. A poll's options cannot be changed once
    // somebody has voted — the votes point at them.
    Task<PostResult> UpdateAsync(
        Guid id,
        PostRequest request,
        bool canModerate,
        CancellationToken cancellationToken = default);

    Task<PostResult> DeleteAsync(
        Guid id,
        bool canModerate,
        CancellationToken cancellationToken = default);

    Task<PostResult> SetPinnedAsync(
        Guid id,
        bool pinned,
        CancellationToken cancellationToken = default);

    // A toggle: liking twice takes it back.
    Task<PostResult> ToggleLikeAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PostResult> CommentAsync(
        Guid id,
        string message,
        CancellationToken cancellationToken = default);

    Task<PostResult> RemoveCommentAsync(
        Guid id,
        Guid commentId,
        bool canModerate,
        CancellationToken cancellationToken = default);

    // Single choice: voting again moves the vote rather than adding one.
    Task<PostResult> VoteAsync(
        Guid id,
        Guid optionId,
        CancellationToken cancellationToken = default);
}

public sealed record PostDto
{
    public required Guid Id { get; init; }

    public required PostType PostType { get; init; }

    public required Guid AuthorEmployeeId { get; init; }

    public string? AuthorName { get; init; }

    public string? AuthorDepartmentName { get; init; }

    public required string Content { get; init; }

    public required bool IsPinned { get; init; }

    public DateOnly? EventOn { get; init; }

    public string? EventLocation { get; init; }

    public string? PollQuestion { get; init; }

    // Empty unless this is a poll. Shares are null until somebody votes.
    public required IReadOnlyList<PollOptionDto> PollOptions { get; init; }

    public required int PollVotes { get; init; }

    // Which option the caller chose, if any.
    public Guid? MyVoteOptionId { get; init; }

    public CelebrationKind? CelebrationKind { get; init; }

    public Guid? CelebrationPersonId { get; init; }

    public string? CelebrationPersonName { get; init; }

    public string? CelebrationDetail { get; init; }

    // Counted from the reaction rows.
    public required int Likes { get; init; }

    public required bool HasMyLike { get; init; }

    public required IReadOnlyList<PostCommentDto> Comments { get; init; }

    // Whether the caller may edit or remove it, so the screen can withhold
    // the button rather than offering one that fails.
    public required bool CanManage { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record PollOptionDto
{
    public required Guid Id { get; init; }

    public required string Label { get; init; }

    public required int Votes { get; init; }

    // Null until somebody votes.
    public decimal? Share { get; init; }

    public required int Sequence { get; init; }
}

public sealed record PostCommentDto
{
    public required Guid Id { get; init; }

    public required Guid AuthorEmployeeId { get; init; }

    public string? AuthorName { get; init; }

    public string? AuthorDepartmentName { get; init; }

    public required string Message { get; init; }

    public required bool CanRemove { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record FeedQuery : PagedQuery
{
    public PostType? PostType { get; init; }

    public Guid? AuthorEmployeeId { get; init; }

    public bool? PinnedOnly { get; init; }

    // Events still to come, for the rail that lists them.
    public bool? UpcomingEventsOnly { get; init; }

    public string? Search { get; init; }
}

public sealed record PostRequest
{
    public PostType PostType { get; init; }

    public required string Content { get; init; }

    public DateOnly? EventOn { get; init; }

    public string? EventLocation { get; init; }

    public string? PollQuestion { get; init; }

    // Trimmed, lower-cased and de-duplicated on the way in.
    public IReadOnlyList<string>? PollOptions { get; init; }

    public CelebrationKind? CelebrationKind { get; init; }

    public Guid? CelebrationPersonId { get; init; }

    public string? CelebrationDetail { get; init; }
}

public enum PostOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,
    Refused,
    Forbidden,
    NoEmployeeRecord,

    // Changing a poll's options after somebody has voted on them.
    PollHasVotes,
}

public sealed record PostResult
{
    public required PostOutcome Outcome { get; init; }

    public PostDto? Post { get; init; }

    public PostRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == PostOutcome.Succeeded;

    public static PostResult Failed(PostOutcome outcome) => new() { Outcome = outcome };

    public static PostResult Refused(PostRefusal refusal) =>
        new() { Outcome = PostOutcome.Refused, Refusal = refusal };

    public static PostResult Ok(PostDto post) =>
        new() { Outcome = PostOutcome.Succeeded, Post = post };

    public static PostResult Done() => new() { Outcome = PostOutcome.Succeeded };
}
