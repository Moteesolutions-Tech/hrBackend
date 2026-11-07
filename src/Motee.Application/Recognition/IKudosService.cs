using Motee.Application.Common;
using Motee.Domain.Recognition;

namespace Motee.Application.Recognition;

// Kudos — the recognition wall, the leaderboard over it, and one person's own
// recognition.
//
// Nothing is counted into a column. Reactions and comments are rows, so a
// reaction count and "did I already react" are the same fact asked two ways —
// the client's type carries a `users` list, a `reactedBy` list and a `count`,
// which is three places for one answer and two of them were always empty.
public interface IKudosService
{
    // The wall. Private kudos are filtered out unless the caller is party to
    // them, so there is no visibility flag for a client to honour or forget.
    Task<PagedResult<KudosPostDto>> WallAsync(
        KudosQuery query,
        CancellationToken cancellationToken = default);

    Task<KudosPostDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // The four KPI cards.
    Task<KudosTally> SummaryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KudosStandingDto>> LeaderboardAsync(
        int take = 10,
        CancellationToken cancellationToken = default);

    // What the signed-in person has received and given, for their dashboard.
    Task<KudosForMeDto> MineAsync(CancellationToken cancellationToken = default);

    Task<KudosResult> SendAsync(
        KudosRequest request,
        CancellationToken cancellationToken = default);

    // The sender's to withdraw. HR can remove anybody's, which is what the
    // module's Delete permission is for.
    Task<KudosResult> RemoveAsync(
        Guid id,
        bool canRemoveAnybodys,
        CancellationToken cancellationToken = default);

    Task<KudosResult> SetPinnedAsync(
        Guid id,
        bool pinned,
        CancellationToken cancellationToken = default);

    // Toggles. Clicking the same reaction twice takes it back, which is what
    // the feed's single onReact handler does.
    Task<KudosResult> ToggleReactionAsync(
        Guid id,
        ReactionType type,
        CancellationToken cancellationToken = default);

    Task<KudosResult> CommentAsync(
        Guid id,
        string message,
        CancellationToken cancellationToken = default);

    // The author's to delete, or HR's.
    Task<KudosResult> RemoveCommentAsync(
        Guid id,
        Guid commentId,
        bool canRemoveAnybodys,
        CancellationToken cancellationToken = default);
}

public sealed record KudosPostDto
{
    public required Guid Id { get; init; }

    public required Guid SenderEmployeeId { get; init; }

    public string? SenderName { get; init; }

    public string? SenderDepartmentName { get; init; }

    public Guid? RecipientEmployeeId { get; init; }

    public string? RecipientName { get; init; }

    public Guid? RecipientDepartmentId { get; init; }

    public string? RecipientDepartmentName { get; init; }

    public required KudosRecipientKind RecipientKind { get; init; }

    public required KudosType KudosType { get; init; }

    public string? CustomTypeName { get; init; }

    public required CompanyValue CompanyValue { get; init; }

    public required string Message { get; init; }

    public required KudosVisibility Visibility { get; init; }

    // Derived from the visibility, so the two can never disagree.
    public required bool IsAnnounced { get; init; }

    public required bool IsPinned { get; init; }

    public required IReadOnlyList<KudosReactionDto> Reactions { get; init; }

    public required IReadOnlyList<KudosCommentDto> Comments { get; init; }

    // Whether the caller may take this down, so the screen can withhold the
    // button rather than offering one that fails.
    public required bool CanRemove { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record KudosReactionDto
{
    public required ReactionType Type { get; init; }

    // Counted from the rows, never stored.
    public required int Count { get; init; }

    // Whether the caller is one of them — the same fact as the count, asked
    // from the other side, which is why neither is a column.
    public required bool Mine { get; init; }
}

public sealed record KudosCommentDto
{
    public required Guid Id { get; init; }

    public required Guid AuthorEmployeeId { get; init; }

    public string? AuthorName { get; init; }

    public string? AuthorDepartmentName { get; init; }

    public required string Message { get; init; }

    public required bool CanRemove { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record KudosStandingDto
{
    public required KudosStanding Standing { get; init; }

    public string? EmployeeName { get; init; }

    public string? DepartmentName { get; init; }
}

public sealed record KudosForMeDto
{
    public required IReadOnlyList<KudosPostDto> Received { get; init; }

    public required IReadOnlyList<KudosPostDto> Sent { get; init; }

    public required int ReceivedCount { get; init; }

    public required int SentCount { get; init; }

    // Consecutive weeks of giving — see KudosStanding.Streak for the
    // definition, which the client leaves undefined and hardcodes to zero.
    public required int Streak { get; init; }
}

public sealed record KudosQuery : PagedQuery
{
    public KudosType? KudosType { get; init; }

    public CompanyValue? CompanyValue { get; init; }

    // The stat cards drill down to a person or a month.
    public Guid? RecipientEmployeeId { get; init; }

    public Guid? SenderEmployeeId { get; init; }

    // "2026-04". The card that counts this month filters the feed to it.
    public string? Month { get; init; }

    public bool? PinnedOnly { get; init; }

    public string? Search { get; init; }
}

public sealed record KudosRequest
{
    // One or the other. A department kudos names no individual, and naming
    // both is refused rather than silently preferring one.
    public Guid? RecipientEmployeeId { get; init; }

    public Guid? RecipientDepartmentId { get; init; }

    public KudosType KudosType { get; init; }

    public string? CustomTypeName { get; init; }

    public CompanyValue CompanyValue { get; init; }

    public required string Message { get; init; }

    public KudosVisibility Visibility { get; init; } = KudosVisibility.Public;
}

public enum KudosOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,
    DepartmentNotFound,
    Refused,
    Forbidden,

    // The signed-in account has no employee record, so it cannot give or
    // receive recognition. The account owner is permanently in this state.
    NoEmployeeRecord,
}

public sealed record KudosResult
{
    public required KudosOutcome Outcome { get; init; }

    public KudosPostDto? Post { get; init; }

    public KudosRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == KudosOutcome.Succeeded;

    public static KudosResult Failed(KudosOutcome outcome) => new() { Outcome = outcome };

    public static KudosResult Refused(KudosRefusal refusal) =>
        new() { Outcome = KudosOutcome.Refused, Refusal = refusal };

    public static KudosResult Ok(KudosPostDto post) =>
        new() { Outcome = KudosOutcome.Succeeded, Post = post };

    public static KudosResult Done() => new() { Outcome = KudosOutcome.Succeeded };
}
