using Motee.Application.Common;
using Motee.Domain.Suggestions;

namespace Motee.Application.Suggestions;

// The suggestion box.
//
// An anonymous suggestion's submitter never leaves this service. The DTO has
// nowhere to put the name, rather than a flag saying not to draw it — the
// client's own type carries `isAnonymous` beside submitterName,
// submitterInitials and submitterDept, so today the identity travels to the
// browser and the screen is trusted.
//
// Upvotes are rows, so the count and "have I voted" are the same fact asked
// two ways and a double-click cannot inflate anything.
public interface ISuggestionService
{
    Task<PagedResult<SuggestionDto>> ListAsync(
        SuggestionQuery query,
        CancellationToken cancellationToken = default);

    Task<SuggestionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // The four KPI cards.
    Task<SuggestionTally> SummaryAsync(CancellationToken cancellationToken = default);

    // The signed-in person's own submissions, anonymous ones included — they
    // wrote them, so they can see them.
    Task<IReadOnlyList<SuggestionDto>> MineAsync(CancellationToken cancellationToken = default);

    Task<SuggestionResult> SubmitAsync(
        SuggestionRequest request,
        CancellationToken cancellationToken = default);

    // HR's triage. Moving into or out of a terminal status stamps and clears
    // the resolved date, so the two can never disagree.
    Task<SuggestionResult> SetStatusAsync(
        Guid id,
        SuggestionStatus status,
        CancellationToken cancellationToken = default);

    Task<SuggestionResult> SetPriorityAsync(
        Guid id,
        SuggestionPriority priority,
        CancellationToken cancellationToken = default);

    // The published reply and the implementation notes, saved together because
    // the detail panel edits them on one screen.
    Task<SuggestionResult> RespondAsync(
        Guid id,
        SuggestionResponseRequest request,
        CancellationToken cancellationToken = default);

    Task<SuggestionResult> SetFeaturedAsync(
        Guid id,
        bool featured,
        CancellationToken cancellationToken = default);

    // A toggle: voting twice takes the vote back, which is what the board's
    // single handler does.
    // Takes it off the board for good, with its comments and votes.
    //
    // Not a status: every other action here moves a suggestion through triage, and a
    // duplicate or something posted to the wrong board has no honest status to sit in.
    // Removed outright rather than hidden, because a hidden suggestion still counts
    // towards the board's totals and nothing can be clicked through to it.
    Task<SuggestionOutcome> RemoveAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SuggestionResult> ToggleUpvoteAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // `official` marks it as HR's answer rather than a colleague's. Decided by
    // the caller's permission and then frozen onto the row, so somebody who
    // later leaves HR does not un-badge the answers they gave there.
    Task<SuggestionResult> CommentAsync(
        Guid id,
        string message,
        bool official,
        CancellationToken cancellationToken = default);

    Task<SuggestionResult> RemoveCommentAsync(
        Guid id,
        Guid commentId,
        bool canRemoveAnybodys,
        CancellationToken cancellationToken = default);
}

public sealed record SuggestionDto
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required SuggestionCategory Category { get; init; }

    public required SuggestionStatus Status { get; init; }

    public required SuggestionPriority Priority { get; init; }

    public required bool IsAnonymous { get; init; }

    // All three are null on an anonymous suggestion, for everybody except its
    // author. There is no flag here saying "do not show the name" because
    // there is no name here to show.
    public Guid? SubmitterEmployeeId { get; init; }

    public string? SubmitterName { get; init; }

    public string? SubmitterDepartmentName { get; init; }

    // Whether the caller wrote it. Lets somebody pick their own anonymous
    // suggestion out of the list without it naming them to anyone else.
    public required bool IsMine { get; init; }

    public string? AdminResponse { get; init; }

    public string? ImplementationNotes { get; init; }

    public required bool IsFeatured { get; init; }

    // Derived: enough upvotes inside the fortnight. The client calls anything
    // over ten votes trending for ever.
    public required bool IsTrending { get; init; }

    // Counted from the vote rows.
    public required int Upvotes { get; init; }

    // Whether the caller is one of them — the same fact from the other side,
    // which is why neither is a column.
    public required bool HasMyVote { get; init; }

    public required bool IsResolved { get; init; }

    public DateOnly? ResolvedOn { get; init; }

    public required IReadOnlyList<SuggestionCommentDto> Comments { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SuggestionCommentDto
{
    public required Guid Id { get; init; }

    public required Guid AuthorEmployeeId { get; init; }

    public string? AuthorName { get; init; }

    public string? AuthorDepartmentName { get; init; }

    public required string Message { get; init; }

    // HR's answer rather than a colleague's.
    public required bool IsOfficial { get; init; }

    public required bool CanRemove { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public enum SuggestionSort
{
    MostUpvoted,
    MostRecent,
    Oldest,
}

public sealed record SuggestionQuery : PagedQuery
{
    public SuggestionStatus? Status { get; init; }

    public SuggestionCategory? Category { get; init; }

    public SuggestionPriority? Priority { get; init; }

    public bool? FeaturedOnly { get; init; }

    public bool? TrendingOnly { get; init; }

    // The card that counts "under review" includes accepted ones, which is one
    // filter rather than two statuses for the caller to remember.
    public bool? UnderReviewOnly { get; init; }

    public SuggestionSort Sort { get; init; } = SuggestionSort.MostRecent;

    public string? Search { get; init; }
}

public sealed record SuggestionRequest
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    public SuggestionCategory Category { get; init; } = SuggestionCategory.Other;

    // Honoured as given. The submitter is always recorded; this decides only
    // whether anybody is ever told.
    public bool IsAnonymous { get; init; }
}

public sealed record SuggestionResponseRequest
{
    public string? AdminResponse { get; init; }

    public string? ImplementationNotes { get; init; }
}

public enum SuggestionOutcome
{
    Succeeded,
    NotFound,
    Refused,
    Forbidden,

    // The signed-in account has no employee record, so it cannot submit or
    // vote. The account owner is permanently in this state.
    NoEmployeeRecord,
}

public sealed record SuggestionResult
{
    public required SuggestionOutcome Outcome { get; init; }

    public SuggestionDto? Suggestion { get; init; }

    public SuggestionRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == SuggestionOutcome.Succeeded;

    public static SuggestionResult Failed(SuggestionOutcome outcome) =>
        new() { Outcome = outcome };

    public static SuggestionResult Refused(SuggestionRefusal refusal) =>
        new() { Outcome = SuggestionOutcome.Refused, Refusal = refusal };

    public static SuggestionResult Ok(SuggestionDto suggestion) =>
        new() { Outcome = SuggestionOutcome.Succeeded, Suggestion = suggestion };

    public static SuggestionResult Done() => new() { Outcome = SuggestionOutcome.Succeeded };
}
