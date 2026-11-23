using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Domain.Suggestions;

namespace Motee.Tests.Suggestions;

// The suggestion box.
//
// Anonymity is the thing this module exists to get right, and the client
// cannot: its Suggestion type carries `isAnonymous` alongside submitterName,
// submitterInitials and submitterDept, so the identity travels to the browser
// and the screen is trusted not to draw it. These tests pin the rule that
// decides whether a name may be sent at all.
public class SuggestionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Guid Submitter = Guid.NewGuid();

    private static readonly DateOnly Today = new(2026, 4, 23);

    private const string Title = "Move the all-hands to Tuesday mornings";

    private const string Description =
        "Thursday afternoons clash with the regional stand-ups, so half the team "
        + "joins late or not at all.";

    private static Suggestion Idea(
        string title = Title,
        string description = Description,
        SuggestionCategory category = SuggestionCategory.Process,
        SuggestionStatus status = SuggestionStatus.Submitted,
        SuggestionPriority priority = SuggestionPriority.Medium,
        bool anonymous = false,
        Guid? submitter = null,
        string? adminResponse = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            Category = category,
            Status = status,
            Priority = priority,
            IsAnonymous = anonymous,
            SubmitterEmployeeId = submitter ?? Submitter,
            AdminResponse = adminResponse,
        };

    // ── the wire shape ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(SuggestionStatus.UnderReview, "\"underReview\"")]
    [InlineData(SuggestionStatus.InProgress, "\"inProgress\"")]
    [InlineData(SuggestionCategory.Wellbeing, "\"wellbeing\"")]
    [InlineData(SuggestionPriority.High, "\"high\"")]
    public void SerialisesAsCamelCase(object value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Web));
    }

    [Fact]
    public void CarriesEveryStatusAndCategoryTheClientOffers()
    {
        Assert.Equal(6, Enum.GetValues<SuggestionStatus>().Length);
        Assert.Equal(7, Enum.GetValues<SuggestionCategory>().Length);
    }

    // The client's own mapper accepts these older spellings, so the API has to
    // as well or a migrated row stops reading.
    [Theory]
    [InlineData("new", SuggestionStatus.Submitted)]
    [InlineData("under-review", SuggestionStatus.UnderReview)]
    [InlineData("under_review", SuggestionStatus.UnderReview)]
    [InlineData("rejected", SuggestionStatus.Declined)]
    [InlineData("implemented", SuggestionStatus.Implemented)]
    public void AcceptsTheLegacySpellingsTheClientMaps(string input, SuggestionStatus expected)
    {
        Assert.True(SuggestionStatuses.TryParse(input, out SuggestionStatus status));
        Assert.Equal(expected, status);
    }

    // ── what the form will not accept ───────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Wifi")]
    public void RefusesATitleTooShortToScan(string title)
    {
        Assert.Equal(SuggestionRefusal.TitleRequired, SuggestionRules.Refusal(Idea(title: title)));
    }

    [Fact]
    public void RefusesATitleLongerThanTheFormAllows()
    {
        Assert.Equal(
            SuggestionRefusal.TitleTooLong,
            SuggestionRules.Refusal(Idea(title: new string('a', SuggestionRules.LongestTitle + 1))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Too short to act on")]
    public void RefusesADescriptionTooShortToActOn(string description)
    {
        Assert.Equal(
            SuggestionRefusal.DescriptionRequired,
            SuggestionRules.Refusal(Idea(description: description)));
    }

    [Fact]
    public void RefusesADescriptionLongerThanTheFormAllows()
    {
        Assert.Equal(
            SuggestionRefusal.DescriptionTooLong,
            SuggestionRules.Refusal(
                Idea(description: new string('a', SuggestionRules.LongestDescription + 1))));
    }

    [Fact]
    public void RefusesAnAdminResponseLongerThanTheFieldAllows()
    {
        Assert.Equal(
            SuggestionRefusal.AdminResponseTooLong,
            SuggestionRules.Refusal(
                Idea(adminResponse: new string('a', SuggestionRules.LongestResponse + 1))));
    }

    [Fact]
    public void AcceptsASuggestionTheFormWouldAccept()
    {
        Assert.Equal(SuggestionRefusal.None, SuggestionRules.Refusal(Idea()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesAnEmptyComment(string message)
    {
        Assert.Equal(SuggestionRefusal.CommentRequired, SuggestionRules.CommentRefusal(message));
    }

    [Fact]
    public void RefusesACommentLongerThanTheFieldAllows()
    {
        Assert.Equal(
            SuggestionRefusal.CommentTooLong,
            SuggestionRules.CommentRefusal(new string('a', SuggestionRules.LongestComment + 1)));
    }

    // ── anonymity ───────────────────────────────────────────────────────────

    [Fact]
    public void ASignedSuggestionNamesItsSubmitterToAnybody()
    {
        Assert.True(SuggestionRules.NamesSubmitter(Idea(anonymous: false), Guid.NewGuid()));
    }

    // The whole point of the box. Nobody is exempt — not HR, not the owner —
    // because a box that can be unmasked by whoever is senior enough is not an
    // anonymous box, it is a box with a queue for unmasking.
    [Fact]
    public void AnAnonymousSuggestionNamesItsSubmitterToNobody()
    {
        Assert.False(SuggestionRules.NamesSubmitter(Idea(anonymous: true), Guid.NewGuid()));
    }

    // Except back to the person who wrote it, who already knows and needs to
    // find their own submissions.
    [Fact]
    public void AnAnonymousSuggestionIsStillTheirsToRecognise()
    {
        Assert.True(SuggestionRules.NamesSubmitter(
            Idea(anonymous: true, submitter: Submitter), Submitter));
    }

    // ── trending ────────────────────────────────────────────────────────────

    // The client calls anything over ten votes trending, for ever. A
    // suggestion from two years ago with eleven votes is not trending; it is
    // popular and old. Trending is the last fortnight.
    [Fact]
    public void TrendingIsRecentVotesNotLifetimeVotes()
    {
        List<DateOnly> old =
        [
            .. Enumerable.Range(0, 15)
                .Select(_ => new DateOnly(2024, 1, 1)),
        ];

        Assert.False(SuggestionRules.IsTrending(old, Today));
    }

    [Fact]
    public void TrendingIsEnoughVotesInsideTheWindow()
    {
        List<DateOnly> recent =
        [
            .. Enumerable.Range(0, SuggestionRules.TrendingVotes)
                .Select(day => Today.AddDays(-day % SuggestionRules.TrendingWindowDays)),
        ];

        Assert.True(SuggestionRules.IsTrending(recent, Today));
    }

    [Fact]
    public void AFewRecentVotesAreNotYetTrending()
    {
        Assert.False(SuggestionRules.IsTrending([Today, Today.AddDays(-1)], Today));
    }

    [Fact]
    public void NothingWithNoVotesIsTrending()
    {
        Assert.False(SuggestionRules.IsTrending([], Today));
    }

    // ── resolved ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SuggestionStatus.Implemented)]
    [InlineData(SuggestionStatus.Declined)]
    public void ImplementedAndDeclinedAreTheEndOfIt(SuggestionStatus status)
    {
        Assert.True(SuggestionRules.IsResolved(status));
    }

    [Theory]
    [InlineData(SuggestionStatus.Submitted)]
    [InlineData(SuggestionStatus.UnderReview)]
    [InlineData(SuggestionStatus.Accepted)]
    [InlineData(SuggestionStatus.InProgress)]
    public void EverythingElseIsStillOpen(SuggestionStatus status)
    {
        Assert.False(SuggestionRules.IsResolved(status));
    }

    // The resolved date is set by the move into a terminal status and cleared
    // by the move out of one, so the two can never disagree — the client has
    // a free status dropdown and a separate resolvedAt that nothing keeps in
    // step.
    [Fact]
    public void ResolvingStampsTheDay()
    {
        Assert.Equal(
            Today,
            SuggestionRules.ResolvedOn(SuggestionStatus.Implemented, was: null, asAt: Today));
    }

    [Fact]
    public void ReopeningClearsTheResolvedDay()
    {
        Assert.Null(SuggestionRules.ResolvedOn(
            SuggestionStatus.UnderReview, was: new DateOnly(2026, 1, 5), asAt: Today));
    }

    // Moving between the two terminal states keeps the day it was first
    // closed, which is the date anybody asking "when was this decided" means.
    [Fact]
    public void MovingBetweenTwoClosedStatesKeepsTheOriginalDay()
    {
        DateOnly closed = new(2026, 1, 5);

        Assert.Equal(
            closed,
            SuggestionRules.ResolvedOn(SuggestionStatus.Declined, was: closed, asAt: Today));
    }

    // ── the board's figures ─────────────────────────────────────────────────

    private static SuggestionFacts Facts(SuggestionStatus status, int upvotes) =>
        new() { Status = status, Upvotes = upvotes };

    [Fact]
    public void ReportsTheFourKpiCards()
    {
        SuggestionTally tally = SuggestionBoard.Of(
        [
            Facts(SuggestionStatus.Implemented, 12),
            Facts(SuggestionStatus.UnderReview, 4),

            // The card labelled "Under Review" counts accepted ones too,
            // which is the client's own rule.
            Facts(SuggestionStatus.Accepted, 2),
            Facts(SuggestionStatus.Declined, 0),
        ]);

        Assert.Equal(4, tally.Total);
        Assert.Equal(1, tally.Implemented);
        Assert.Equal(2, tally.UnderReview);
        Assert.Equal(1, tally.Declined);
        Assert.Equal(4.5m, tally.AverageUpvotes);
    }

    [Fact]
    public void AnEmptyBoxAveragesNothingRatherThanDividingByZero()
    {
        SuggestionTally tally = SuggestionBoard.Of([]);

        Assert.Equal(0, tally.Total);
        Assert.Equal(0m, tally.AverageUpvotes);
    }

    [Fact]
    public void RoundsTheAverageToOneDecimal()
    {
        SuggestionTally tally = SuggestionBoard.Of(
        [
            Facts(SuggestionStatus.Submitted, 1),
            Facts(SuggestionStatus.Submitted, 1),
            Facts(SuggestionStatus.Submitted, 2),
        ]);

        Assert.Equal(1.3m, tally.AverageUpvotes);
    }
}
