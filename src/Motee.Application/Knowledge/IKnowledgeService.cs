using Motee.Application.Common;
using Motee.Domain.Knowledge;

namespace Motee.Application.Knowledge;

// The knowledge base.
//
// Reading is near-universal — workspace.knowledge is granted to almost every
// role and to the self-service bundle — so the line that matters is not who
// may read but what. A draft is not an article yet and an archived one has
// been withdrawn; neither leaves the service for a reader, and the caller's
// authoring permission is what decides, passed in rather than inferred here.
public interface IKnowledgeService
{
    // The authoring list. `includeUnpublished` is the caller's Edit grant:
    // without it this returns the same set a reader would see, so a missing
    // permission narrows the list rather than leaking drafts.
    Task<KnowledgeListDto> ListAsync(
        KnowledgeQuery query,
        bool includeUnpublished,
        CancellationToken cancellationToken = default);

    // What the company can read, with the per-category counts the employee
    // tiles are built from.
    Task<KnowledgeListDto> PublishedAsync(
        KnowledgeQuery query,
        CancellationToken cancellationToken = default);

    // The four KPI cards. Over everything, because they are an authoring view.
    Task<KnowledgeTally> SummaryAsync(CancellationToken cancellationToken = default);

    Task<KnowledgeArticleDto?> GetAsync(
        Guid id,
        bool includeUnpublished,
        CancellationToken cancellationToken = default);

    Task<KnowledgeResult> CreateAsync(
        KnowledgeArticleRequest request,
        CancellationToken cancellationToken = default);

    Task<KnowledgeResult> UpdateAsync(
        Guid id,
        KnowledgeArticleRequest request,
        CancellationToken cancellationToken = default);

    // Publishing stamps the day it first went out; archiving keeps it; pulling
    // back to a draft clears it.
    Task<KnowledgeResult> SetStatusAsync(
        Guid id,
        ArticleStatus status,
        CancellationToken cancellationToken = default);

    Task<KnowledgeResult> SetFeaturedAsync(
        Guid id,
        bool featured,
        CancellationToken cancellationToken = default);

    Task<KnowledgeResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    // Records that the caller opened it. One row per reader, counting their
    // own opens — so the base can report how many people an article reached
    // as well as how often it is read.
    Task<KnowledgeResult> RecordReadAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // Was this helpful. Changeable, because a reader can switch their answer.
    Task<KnowledgeResult> VoteAsync(
        Guid id,
        ArticleVote vote,
        CancellationToken cancellationToken = default);

    Task<KnowledgeResult> ClearVoteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public sealed record KnowledgeArticleDto
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public string? Summary { get; init; }

    // One field. The client's type carries both `content` and `body` and maps
    // each to the other, so there are two places to look and no rule for
    // which wins.
    public required string Body { get; init; }

    public required ArticleCategory Category { get; init; }

    public required ArticleStatus Status { get; init; }

    public required Guid AuthorEmployeeId { get; init; }

    public string? AuthorName { get; init; }

    public required IReadOnlyList<string> Tags { get; init; }

    public required bool IsFeatured { get; init; }

    public DateOnly? PublishedOn { get; init; }

    // Counted from the read rows.
    public required int Views { get; init; }

    public required int Readers { get; init; }

    public required bool HaveIRead { get; init; }

    public required int HelpfulVotes { get; init; }

    public required int NotHelpfulVotes { get; init; }

    // Null when nobody has voted — not zero, which reads as "everybody said
    // it was useless".
    public decimal? HelpfulPercent { get; init; }

    // The caller's own vote, or null if they have not said.
    public ArticleVote? MyVote { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ArticleCategoryCount
{
    public required ArticleCategory Category { get; init; }

    public required int Count { get; init; }
}

public sealed record KnowledgeListDto
{
    public required PagedResult<KnowledgeArticleDto> Articles { get; init; }

    // Over the filtered set before the category filter, so picking a category
    // does not empty the tiles that offered the choice.
    public required IReadOnlyList<ArticleCategoryCount> CategoryCounts { get; init; }
}

public sealed record KnowledgeQuery : PagedQuery
{
    public ArticleStatus? Status { get; init; }

    public ArticleCategory? Category { get; init; }

    // Matched against the normalised tags, so "Payroll" finds "payroll".
    public string? Tag { get; init; }

    public bool? FeaturedOnly { get; init; }

    // Title, summary and body.
    public string? Search { get; init; }
}

public sealed record KnowledgeArticleRequest
{
    public required string Title { get; init; }

    public string? Summary { get; init; }

    public required string Body { get; init; }

    public ArticleCategory Category { get; init; } = ArticleCategory.General;

    public ArticleStatus Status { get; init; } = ArticleStatus.Draft;

    // Sent as the form holds them. Trimmed, lower-cased and de-duplicated on
    // the way in.
    public IReadOnlyList<string>? Tags { get; init; }

    public bool IsFeatured { get; init; }
}

public enum KnowledgeOutcome
{
    Succeeded,
    NotFound,
    Refused,

    // The signed-in account has no employee record, so it cannot author an
    // article or vote on one. The account owner is permanently in this state.
    NoEmployeeRecord,
}

public sealed record KnowledgeResult
{
    public required KnowledgeOutcome Outcome { get; init; }

    public KnowledgeArticleDto? Article { get; init; }

    public KnowledgeRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == KnowledgeOutcome.Succeeded;

    public static KnowledgeResult Failed(KnowledgeOutcome outcome) => new() { Outcome = outcome };

    public static KnowledgeResult Refused(KnowledgeRefusal refusal) =>
        new() { Outcome = KnowledgeOutcome.Refused, Refusal = refusal };

    public static KnowledgeResult Ok(KnowledgeArticleDto article) =>
        new() { Outcome = KnowledgeOutcome.Succeeded, Article = article };

    public static KnowledgeResult Done() => new() { Outcome = KnowledgeOutcome.Succeeded };
}
