using Motee.Domain.Common;

namespace Motee.Domain.Knowledge;

public enum ArticleCategory
{
    Onboarding,
    HrPolicies,
    Payroll,
    Benefits,
    Compliance,
    Tools,
    General,
}

public enum ArticleStatus
{
    Draft,
    Published,

    // Was published and no longer is. Distinct from a draft, which never was.
    Archived,
}

public enum ArticleVote
{
    Helpful,
    NotHelpful,
}

// One article in the knowledge base.
//
// The body is one field. The client's type carries both `content` and `body`
// and maps each to the other, so there are two places to look and no rule
// about which wins.
public class KnowledgeArticle : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Title { get; set; }

    // The line under the title in the list. Optional, because a good title
    // often needs no gloss.
    public string? Summary { get; set; }

    public required string Body { get; set; }

    public ArticleCategory Category { get; set; }

    public ArticleStatus Status { get; set; } = ArticleStatus.Draft;

    public Guid AuthorEmployeeId { get; set; }

    // Normalised on the way in — the form takes one comma-separated string, so
    // "Payroll, payroll , PAYROLL" would otherwise be three tags that filter
    // as three different things.
    public List<string> Tags { get; set; } = [];

    public bool IsFeatured { get; set; }

    // Stamped by the move into published and kept through archiving, because
    // that it once went out is a fact worth keeping. Cleared only by a pull
    // back to draft, which means it is not out there any more.
    //
    // The client derives this from the status as the creation date, so an
    // article written in January and published in June reads as published in
    // January.
    public DateOnly? PublishedOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One person's reading of one article.
//
// A row per reader rather than a hit counter on the article, which gives both
// figures the base needs: how many people it reached, and how often it is
// opened. Bounded by the headcount, unlike a row per view.
public class ArticleRead : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ArticleId { get; set; }

    public Guid EmployeeId { get; set; }

    // How many times this person has opened it.
    public int Views { get; set; } = 1;

    public DateTimeOffset FirstReadAt { get; set; }

    public DateTimeOffset LastReadAt { get; set; }
}

// One person's answer to "was this helpful".
//
// A row with a direction, rather than the client's three fields — `helpful`,
// `helpfulVotes` and `notHelpfulVotes` — where the first two held the same
// number and the third was always zero. Changeable, because the reader can
// switch their vote, and removable, because they can take it back.
public class ArticleFeedback : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ArticleId { get; set; }

    public Guid EmployeeId { get; set; }

    public ArticleVote Vote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum KnowledgeRefusal
{
    None,
    TitleRequired,
    TitleTooLong,
    BodyRequired,
    BodyTooLong,
    SummaryTooLong,
    TooManyTags,

    // Reading, or voting on, something that is not published.
    NotReadable,
}

public static class ArticleCategories
{
    // The client writes hr_policies; this API emits hrPolicies. Both parse, so
    // a row written by an earlier version keeps reading.
    public static bool TryParse(string? value, out ArticleCategory category)
    {
        category = ArticleCategory.General;

        string normalised = (value ?? string.Empty)
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal);

        return Enum.TryParse(normalised, ignoreCase: true, out category)
            && Enum.IsDefined(category);
    }
}

public static class KnowledgeArticles
{
    // The form's own limits.
    public const int ShortestTitle = 5;

    public const int LongestTitle = 200;

    public const int ShortestBody = 50;

    public const int LongestBody = 10_000;

    public const int LongestSummary = 500;

    public const int MostTags = 10;

    public const int LongestTag = 40;

    public static KnowledgeRefusal Refusal(KnowledgeArticle article)
    {
        string title = article.Title?.Trim() ?? string.Empty;
        string body = article.Body?.Trim() ?? string.Empty;

        if (title.Length < ShortestTitle)
        {
            return KnowledgeRefusal.TitleRequired;
        }

        if (title.Length > LongestTitle)
        {
            return KnowledgeRefusal.TitleTooLong;
        }

        if (body.Length < ShortestBody)
        {
            return KnowledgeRefusal.BodyRequired;
        }

        if (body.Length > LongestBody)
        {
            return KnowledgeRefusal.BodyTooLong;
        }

        if ((article.Summary?.Trim().Length ?? 0) > LongestSummary)
        {
            return KnowledgeRefusal.SummaryTooLong;
        }

        return article.Tags.Count > MostTags
            ? KnowledgeRefusal.TooManyTags
            : KnowledgeRefusal.None;
    }

    // Trimmed, lower-cased, de-duplicated and capped, in the order given. The
    // form sends one comma-separated string, so without this a reader filters
    // by "payroll" and misses everything tagged "Payroll".
    public static IReadOnlyList<string> NormaliseTags(IEnumerable<string?> tags)
    {
        List<string> kept = [];

        foreach (string? tag in tags)
        {
            string trimmed = tag?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Length > LongestTag)
            {
                trimmed = trimmed[..LongestTag];
            }

            string folded = trimmed.ToLowerInvariant();

            if (!kept.Contains(folded, StringComparer.Ordinal))
            {
                kept.Add(folded);
            }
        }

        return kept;
    }

    public static DateOnly? PublishedOn(ArticleStatus status, DateOnly? was, DateOnly asAt) =>
        status switch
        {
            // First time out stamps the day; a republish keeps the original.
            ArticleStatus.Published => was ?? asAt,

            // It was published, and that it once was is worth keeping.
            ArticleStatus.Archived => was,

            // Back to a draft is not out there any more.
            _ => null,
        };

    public static bool IsReadable(KnowledgeArticle article) =>
        article.Status == ArticleStatus.Published;

    // Null rather than zero when nobody has voted. Nought per cent reads as
    // "everybody said it was useless", which is a different thing from
    // "nobody has said anything".
    public static decimal? HelpfulPercent(int helpful, int notHelpful)
    {
        int votes = helpful + notHelpful;

        return votes == 0
            ? null
            : Math.Round(helpful / (decimal)votes * 100m, 1, MidpointRounding.AwayFromZero);
    }
}

// One article, reduced to what the base's figures need.
public sealed record ArticleFacts
{
    public required ArticleStatus Status { get; init; }

    public required int Views { get; init; }

    public required int Readers { get; init; }
}

public sealed record KnowledgeTally
{
    public required int Total { get; init; }

    public required int Published { get; init; }

    public required int Drafts { get; init; }

    public required int Archived { get; init; }

    public required int TotalViews { get; init; }

    // Reported beside the views, because "155 views" and "55 people read it"
    // answer different questions and only the second says whether an article
    // reached anybody new.
    public required int TotalReaders { get; init; }
}

public static class KnowledgeBase
{
    public static KnowledgeTally Of(IReadOnlyList<ArticleFacts> articles) => new()
    {
        Total = articles.Count,
        Published = articles.Count(row => row.Status == ArticleStatus.Published),
        Drafts = articles.Count(row => row.Status == ArticleStatus.Draft),
        Archived = articles.Count(row => row.Status == ArticleStatus.Archived),
        TotalViews = articles.Sum(row => row.Views),
        TotalReaders = articles.Sum(row => row.Readers),
    };
}
