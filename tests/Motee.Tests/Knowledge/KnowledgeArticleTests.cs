using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Domain.Knowledge;

namespace Motee.Tests.Knowledge;

// The knowledge base.
//
// Three things the client's type cannot keep straight, pinned here: `content`
// and `body` hold the same text, `helpful`/`helpfulVotes`/`notHelpfulVotes`
// are three fields for a two-sided vote where the third was always zero, and
// `publishedAt` is derived from the status as the creation date — so an
// article written in January and published in June reads as published in
// January.
public class KnowledgeArticleTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly DateOnly Today = new(2026, 6, 15);

    private const string Title = "How to claim a travel expense";

    private static readonly string Body = new('a', KnowledgeArticles.ShortestBody);

    private static KnowledgeArticle Article(
        string title = Title,
        string? body = null,
        string? summary = null,
        ArticleCategory category = ArticleCategory.Payroll,
        ArticleStatus status = ArticleStatus.Draft,
        IEnumerable<string>? tags = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Body = body ?? Body,
            Summary = summary,
            Category = category,
            Status = status,
            AuthorEmployeeId = Guid.NewGuid(),
            Tags = [.. tags ?? []],
        };

    // ── the wire shape ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(ArticleCategory.HrPolicies, "\"hrPolicies\"")]
    [InlineData(ArticleCategory.Onboarding, "\"onboarding\"")]
    [InlineData(ArticleStatus.Archived, "\"archived\"")]
    [InlineData(ArticleVote.NotHelpful, "\"notHelpful\"")]
    public void SerialisesAsCamelCase(object value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Web));
    }

    [Fact]
    public void CarriesEveryCategoryTheClientOffers()
    {
        Assert.Equal(7, Enum.GetValues<ArticleCategory>().Length);
        Assert.Equal(3, Enum.GetValues<ArticleStatus>().Length);
    }

    // The client writes hr_policies; this API emits hrPolicies. Both parse, so
    // a row written by an earlier version keeps reading.
    [Theory]
    [InlineData("hr_policies", ArticleCategory.HrPolicies)]
    [InlineData("hrPolicies", ArticleCategory.HrPolicies)]
    [InlineData("general", ArticleCategory.General)]
    public void AcceptsBothSpellingsOfACategory(string input, ArticleCategory expected)
    {
        Assert.True(ArticleCategories.TryParse(input, out ArticleCategory category));
        Assert.Equal(expected, category);
    }

    // ── what the form will not accept ───────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Pay")]
    public void RefusesATitleTooShortToFind(string title)
    {
        Assert.Equal(
            KnowledgeRefusal.TitleRequired,
            KnowledgeArticles.Refusal(Article(title: title)));
    }

    [Fact]
    public void RefusesATitleLongerThanTheFormAllows()
    {
        Assert.Equal(
            KnowledgeRefusal.TitleTooLong,
            KnowledgeArticles.Refusal(
                Article(title: new string('a', KnowledgeArticles.LongestTitle + 1))));
    }

    [Fact]
    public void RefusesABodyTooShortToBeAnArticle()
    {
        Assert.Equal(
            KnowledgeRefusal.BodyRequired,
            KnowledgeArticles.Refusal(Article(body: "Ask HR.")));
    }

    [Fact]
    public void RefusesABodyLongerThanTheFormAllows()
    {
        Assert.Equal(
            KnowledgeRefusal.BodyTooLong,
            KnowledgeArticles.Refusal(
                Article(body: new string('a', KnowledgeArticles.LongestBody + 1))));
    }

    [Fact]
    public void AcceptsAnArticleTheFormWouldAccept()
    {
        Assert.Equal(KnowledgeRefusal.None, KnowledgeArticles.Refusal(Article()));
    }

    [Fact]
    public void RefusesMoreTagsThanAnybodyWillRead()
    {
        IEnumerable<string> tooMany = Enumerable
            .Range(0, KnowledgeArticles.MostTags + 1)
            .Select(number => $"tag{number}");

        Assert.Equal(
            KnowledgeRefusal.TooManyTags,
            KnowledgeArticles.Refusal(Article(tags: tooMany)));
    }

    // ── tags ────────────────────────────────────────────────────────────────

    // The form takes one comma-separated string, so "Payroll, payroll , PAYROLL"
    // arrives as three tags that filter as three different things. One tag.
    [Fact]
    public void FoldsTagsThatDifferOnlyByCaseOrSpacing()
    {
        Assert.Equal(
            ["payroll"],
            KnowledgeArticles.NormaliseTags(["Payroll", " payroll ", "PAYROLL"]));
    }

    [Fact]
    public void DropsEmptyTags()
    {
        Assert.Equal(
            ["expenses"],
            KnowledgeArticles.NormaliseTags(["expenses", "", "   ", null!]));
    }

    [Fact]
    public void KeepsTagsInTheOrderTheyWereGiven()
    {
        Assert.Equal(
            ["travel", "expenses", "policy"],
            KnowledgeArticles.NormaliseTags(["travel", "expenses", "policy"]));
    }

    [Fact]
    public void TrimsATagTooLongToBeALabel()
    {
        string huge = new('a', KnowledgeArticles.LongestTag + 10);

        Assert.Equal(
            [huge[..KnowledgeArticles.LongestTag]],
            KnowledgeArticles.NormaliseTags([huge]));
    }

    // ── publishing ──────────────────────────────────────────────────────────

    // The client derives publishedAt from the status as the creation date, so
    // an article written in January and published in June reads as published
    // in January. This is stamped by the move into published.
    [Fact]
    public void PublishingStampsTheDay()
    {
        Assert.Equal(
            Today,
            KnowledgeArticles.PublishedOn(ArticleStatus.Published, was: null, asAt: Today));
    }

    [Fact]
    public void RepublishingKeepsTheDayItFirstWentOut()
    {
        DateOnly first = new(2026, 1, 20);

        Assert.Equal(
            first,
            KnowledgeArticles.PublishedOn(ArticleStatus.Published, was: first, asAt: Today));
    }

    // Archiving is not unpublishing: it was published, and that it once was is
    // a fact worth keeping.
    [Fact]
    public void ArchivingKeepsThePublicationDate()
    {
        DateOnly first = new(2026, 1, 20);

        Assert.Equal(
            first,
            KnowledgeArticles.PublishedOn(ArticleStatus.Archived, was: first, asAt: Today));
    }

    // Pulling it back to a draft does unpublish it — it is not out there any
    // more, and a publication date would say it is.
    [Fact]
    public void PullingAnArticleBackToDraftClearsThePublicationDate()
    {
        Assert.Null(KnowledgeArticles.PublishedOn(
            ArticleStatus.Draft, was: new DateOnly(2026, 1, 20), asAt: Today));
    }

    // ── who can read it ─────────────────────────────────────────────────────

    [Fact]
    public void OnlyAPublishedArticleIsReadableByTheCompany()
    {
        Assert.True(KnowledgeArticles.IsReadable(Article(status: ArticleStatus.Published)));
        Assert.False(KnowledgeArticles.IsReadable(Article(status: ArticleStatus.Draft)));
        Assert.False(KnowledgeArticles.IsReadable(Article(status: ArticleStatus.Archived)));
    }

    // ── was it helpful ──────────────────────────────────────────────────────

    [Fact]
    public void HelpfulnessIsTheShareOfVotesThatSaidYes()
    {
        Assert.Equal(75m, KnowledgeArticles.HelpfulPercent(helpful: 3, notHelpful: 1));
    }

    // Not nought per cent, which reads as "everybody said it was useless".
    // Nobody has said anything.
    [Fact]
    public void AnArticleNobodyHasVotedOnHasNoScoreRatherThanZero()
    {
        Assert.Null(KnowledgeArticles.HelpfulPercent(helpful: 0, notHelpful: 0));
    }

    [Fact]
    public void EverybodySayingNoIsZeroPerCent()
    {
        Assert.Equal(0m, KnowledgeArticles.HelpfulPercent(helpful: 0, notHelpful: 4));
    }

    [Fact]
    public void RoundsHelpfulnessToOneDecimal()
    {
        Assert.Equal(66.7m, KnowledgeArticles.HelpfulPercent(helpful: 2, notHelpful: 1));
    }

    // ── the base's figures ──────────────────────────────────────────────────

    private static ArticleFacts Facts(ArticleStatus status, int views, int readers) =>
        new() { Status = status, Views = views, Readers = readers };

    [Fact]
    public void ReportsTheFourKpiCards()
    {
        KnowledgeTally tally = KnowledgeBase.Of(
        [
            Facts(ArticleStatus.Published, 120, 40),
            Facts(ArticleStatus.Published, 30, 12),
            Facts(ArticleStatus.Draft, 0, 0),
            Facts(ArticleStatus.Archived, 5, 3),
        ]);

        Assert.Equal(4, tally.Total);
        Assert.Equal(2, tally.Published);
        Assert.Equal(1, tally.Drafts);
        Assert.Equal(1, tally.Archived);
        Assert.Equal(155, tally.TotalViews);

        // Reported alongside the views, because "155 views" and "55 people
        // read it" answer different questions and only one of them tells you
        // whether the article reached anybody new.
        Assert.Equal(55, tally.TotalReaders);
    }

    [Fact]
    public void AnEmptyBaseCountsNothing()
    {
        KnowledgeTally tally = KnowledgeBase.Of([]);

        Assert.Equal(0, tally.Total);
        Assert.Equal(0, tally.TotalViews);
    }
}
