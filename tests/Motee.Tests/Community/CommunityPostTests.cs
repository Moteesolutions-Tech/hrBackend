using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Domain.Community;

namespace Motee.Tests.Community;

// The community feed.
//
// Only the feed is new. The celebrations rail is birthdays and work
// anniversaries within a window, which the calendar already derives from
// AnnualDates; the directory is the employee list, which the employees module
// already serves — and its `skills` array is hardcoded empty because nothing
// in the product records skills yet. Neither needs a store of its own, so
// there isn't one.
public class CommunityPostTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private const string Content = "The new starter handbook is up on the knowledge base.";

    private static CommunityPost Post(
        PostType type = PostType.Update,
        string content = Content,
        DateOnly? eventOn = null,
        string? eventLocation = null,
        string? pollQuestion = null,
        CelebrationKind? celebrationKind = null,
        Guid? celebrationPersonId = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            PostType = type,
            AuthorEmployeeId = Guid.NewGuid(),
            Content = content,
            EventOn = eventOn,
            EventLocation = eventLocation,
            PollQuestion = pollQuestion,
            CelebrationKind = celebrationKind,
            CelebrationPersonId = celebrationPersonId,
        };

    // ── the wire shape ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(PostType.Shoutout, "\"shoutout\"")]
    [InlineData(PostType.Milestone, "\"milestone\"")]
    [InlineData(CelebrationKind.NewHire, "\"newHire\"")]
    [InlineData(CelebrationKind.Anniversary, "\"anniversary\"")]
    public void SerialisesAsCamelCase(object value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Web));
    }

    [Fact]
    public void CarriesEveryPostTypeAndCelebrationKindTheClientOffers()
    {
        Assert.Equal(5, Enum.GetValues<PostType>().Length);
        Assert.Equal(4, Enum.GetValues<CelebrationKind>().Length);
    }

    [Theory]
    [InlineData("new_hire", CelebrationKind.NewHire)]
    [InlineData("newHire", CelebrationKind.NewHire)]
    [InlineData("birthday", CelebrationKind.Birthday)]
    public void AcceptsBothSpellingsOfACelebrationKind(string input, CelebrationKind expected)
    {
        Assert.True(CelebrationKinds.TryParse(input, out CelebrationKind kind));
        Assert.Equal(expected, kind);
    }

    // ── what the form will not accept ───────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesAPostWithNothingInIt(string content)
    {
        Assert.Equal(
            PostRefusal.ContentRequired,
            CommunityPosts.Refusal(Post(content: content), options: []));
    }

    [Fact]
    public void RefusesAPostLongerThanTheFieldAllows()
    {
        Assert.Equal(
            PostRefusal.ContentTooLong,
            CommunityPosts.Refusal(
                Post(content: new string('a', CommunityPosts.LongestContent + 1)),
                options: []));
    }

    [Fact]
    public void AcceptsAnOrdinaryUpdate()
    {
        Assert.Equal(PostRefusal.None, CommunityPosts.Refusal(Post(), options: []));
    }

    // ── events ──────────────────────────────────────────────────────────────

    // An event post with no date is an announcement, not an event — and it
    // would sit in the feed with nothing to put on a calendar.
    [Fact]
    public void RefusesAnEventWithNoDate()
    {
        Assert.Equal(
            PostRefusal.EventDateRequired,
            CommunityPosts.Refusal(Post(type: PostType.Event), options: []));
    }

    [Fact]
    public void AcceptsAnEventWithADate()
    {
        Assert.Equal(
            PostRefusal.None,
            CommunityPosts.Refusal(
                Post(
                    type: PostType.Event,
                    eventOn: new DateOnly(2026, 7, 1),
                    eventLocation: "The Lagos office"),
                options: []));
    }

    // ── polls ───────────────────────────────────────────────────────────────

    [Fact]
    public void RefusesAPollWithNoQuestion()
    {
        Assert.Equal(
            PostRefusal.PollQuestionRequired,
            CommunityPosts.Refusal(
                Post(type: PostType.Poll),
                options: ["Tuesday", "Thursday"]));
    }

    // A poll with one answer is not a poll.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RefusesAPollWithFewerThanTwoOptions(int howMany)
    {
        Assert.Equal(
            PostRefusal.PollNeedsTwoOptions,
            CommunityPosts.Refusal(
                Post(type: PostType.Poll, pollQuestion: "Which day suits the socials?"),
                options: [.. Enumerable.Range(0, howMany).Select(number => $"Option {number}")]));
    }

    [Fact]
    public void AcceptsAPollWithAQuestionAndTwoOptions()
    {
        Assert.Equal(
            PostRefusal.None,
            CommunityPosts.Refusal(
                Post(type: PostType.Poll, pollQuestion: "Which day suits the socials?"),
                options: ["Tuesday", "Thursday"]));
    }

    [Fact]
    public void RefusesAPollWithMoreOptionsThanAnybodyWillRead()
    {
        IReadOnlyList<string> tooMany =
            [.. Enumerable.Range(0, CommunityPosts.MostPollOptions + 1)
                .Select(number => $"Option {number}")];

        Assert.Equal(
            PostRefusal.TooManyPollOptions,
            CommunityPosts.Refusal(
                Post(type: PostType.Poll, pollQuestion: "Which day suits the socials?"),
                options: tooMany));
    }

    // Two options with the same wording make a result nobody can read.
    [Fact]
    public void FoldsPollOptionsThatDifferOnlyByCaseOrSpacing()
    {
        Assert.Equal(
            ["tuesday", "thursday"],
            CommunityPosts.NormaliseOptions(["Tuesday", " tuesday ", "Thursday"]));
    }

    [Fact]
    public void DropsEmptyPollOptions()
    {
        Assert.Equal(
            ["tuesday"],
            CommunityPosts.NormaliseOptions(["Tuesday", "", "   ", null!]));
    }

    // ── celebrations ────────────────────────────────────────────────────────

    // A milestone post that names a kind but nobody it is about has nothing to
    // celebrate.
    [Fact]
    public void RefusesACelebrationWithNobodyItIsAbout()
    {
        Assert.Equal(
            PostRefusal.CelebrationPersonRequired,
            CommunityPosts.Refusal(
                Post(type: PostType.Milestone, celebrationKind: CelebrationKind.Anniversary),
                options: []));
    }

    [Fact]
    public void AcceptsACelebrationThatNamesSomebody()
    {
        Assert.Equal(
            PostRefusal.None,
            CommunityPosts.Refusal(
                Post(
                    type: PostType.Milestone,
                    celebrationKind: CelebrationKind.Anniversary,
                    celebrationPersonId: Guid.NewGuid()),
                options: []));
    }

    // A milestone need not be about a person — "we shipped the thing" is a
    // milestone with nobody's birthday attached.
    [Fact]
    public void AcceptsAMilestoneWithNoCelebrationAtAll()
    {
        Assert.Equal(
            PostRefusal.None,
            CommunityPosts.Refusal(Post(type: PostType.Milestone), options: []));
    }

    // ── comments ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void RefusesAnEmptyComment(string message)
    {
        Assert.Equal(PostRefusal.CommentRequired, CommunityPosts.CommentRefusal(message));
    }

    [Fact]
    public void RefusesACommentLongerThanTheFieldAllows()
    {
        Assert.Equal(
            PostRefusal.CommentTooLong,
            CommunityPosts.CommentRefusal(new string('a', CommunityPosts.LongestComment + 1)));
    }

    // ── poll results ────────────────────────────────────────────────────────

    [Fact]
    public void SharesOutThePollAcrossItsOptions()
    {
        IReadOnlyList<PollResult> results = CommunityPosts.PollResults(
        [
            new PollTally { Label = "tuesday", Votes = 3 },
            new PollTally { Label = "thursday", Votes = 1 },
        ]);

        Assert.Equal(75m, results[0].Share);
        Assert.Equal(25m, results[1].Share);
    }

    // Null rather than nought per cent across the board, so a screen can say
    // "no votes yet" instead of drawing four empty bars that look like
    // everybody abstained.
    [Fact]
    public void APollNobodyHasVotedOnHasNoShares()
    {
        IReadOnlyList<PollResult> results = CommunityPosts.PollResults(
        [
            new PollTally { Label = "tuesday", Votes = 0 },
            new PollTally { Label = "thursday", Votes = 0 },
        ]);

        Assert.All(results, result => Assert.Null(result.Share));
    }

    [Fact]
    public void RoundsThePollShareToOneDecimal()
    {
        IReadOnlyList<PollResult> results = CommunityPosts.PollResults(
        [
            new PollTally { Label = "a", Votes = 1 },
            new PollTally { Label = "b", Votes = 1 },
            new PollTally { Label = "c", Votes = 1 },
        ]);

        Assert.Equal(33.3m, results[0].Share);
    }

    // ── the feed's figures ──────────────────────────────────────────────────

    [Fact]
    public void ReportsTheFeedsFigures()
    {
        FeedTally tally = CommunityFeed.Of(
        [
            new PostFacts { PostType = PostType.Update, Likes = 4, Comments = 2, PollVotes = 0 },
            new PostFacts { PostType = PostType.Poll, Likes = 1, Comments = 0, PollVotes = 7 },
        ],
            members: 12);

        Assert.Equal(2, tally.Posts);
        Assert.Equal(5, tally.Reactions);
        Assert.Equal(2, tally.Comments);
        Assert.Equal(7, tally.PollVotes);
        Assert.Equal(12, tally.Members);
    }

    [Fact]
    public void AnEmptyFeedCountsNothing()
    {
        FeedTally tally = CommunityFeed.Of([], members: 0);

        Assert.Equal(0, tally.Posts);
        Assert.Equal(0, tally.Reactions);
    }
}
