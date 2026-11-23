using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Community;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Domain.Community;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The community feed, out of the database.
//
// The poll is the part worth testing hardest: one vote per person, moved
// rather than added when somebody changes their mind, and options that cannot
// be rewritten once votes point at them. The client keeps a list of voters on
// each option, which allows all three of those to go wrong.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class CommunityServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _adaId;
    private Guid _bolaId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Owner",
            LastName = "Okafor",
            Email = "owner@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        _departmentId = (await Resolve<IDepartmentService>(provider)
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _adaId = await HireAsync(provider, "Ada");
        _bolaId = await HireAsync(provider, "Bola");

        await AccountAsync(_adaId, "ada");
        await AccountAsync(_bolaId, "bola");

        await ActAsync(provider, _adaId);

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static ICommunityService Feed(ServiceProvider provider) =>
        Resolve<ICommunityService>(provider);

    private async Task<Guid> HireAsync(ServiceProvider provider, string first) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = _departmentId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2024, 1, 10),
        })).Employee!.Id;

    private async Task AccountAsync(Guid employeeId, string handle)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Users.Add(new Motee.Infrastructure.Identity.ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            EmployeeId = employeeId,
            FirstName = char.ToUpperInvariant(handle[0]) + handle[1..],
            LastName = "Okafor",
            Email = $"{handle}-login@acme.com",
            NormalizedEmail = $"{handle.ToUpperInvariant()}-LOGIN@ACME.COM",
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    private async Task ActAsync(ServiceProvider provider, Guid employeeId)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        Guid userId = await seed.Users
            .IgnoreQueryFilters()
            .Where(user => user.EmployeeId == employeeId && user.TenantId == _tenantId)
            .Select(user => user.Id)
            .FirstAsync();

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            userId.ToString();
    }

    private const string Content = "The new starter handbook is up on the knowledge base.";

    private static PostRequest Writing(
        PostType type = PostType.Update,
        string content = Content,
        DateOnly? eventOn = null,
        string? eventLocation = null,
        string? pollQuestion = null,
        IReadOnlyList<string>? pollOptions = null,
        CelebrationKind? celebrationKind = null,
        Guid? celebrationPersonId = null) => new()
        {
            PostType = type,
            Content = content,
            EventOn = eventOn,
            EventLocation = eventLocation,
            PollQuestion = pollQuestion,
            PollOptions = pollOptions,
            CelebrationKind = celebrationKind,
            CelebrationPersonId = celebrationPersonId,
        };

    private static async Task<PostDto> PostedAsync(
        ServiceProvider provider,
        PostRequest? request = null)
    {
        PostResult result = await Feed(provider).PostAsync(request ?? Writing());

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Post!;
    }

    private static PostRequest Poll() => Writing(
        type: PostType.Poll,
        content: "Picking a day for the next socials.",
        pollQuestion: "Which day suits you?",
        pollOptions: ["Tuesday", "Thursday"]);

    // ── posting ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ANewPostNamesItsAuthorAndCountsNothingYet()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider);

        Assert.Equal(_adaId, post.AuthorEmployeeId);
        Assert.Equal("Ada Okafor", post.AuthorName);
        Assert.Equal("Engineering", post.AuthorDepartmentName);
        Assert.Equal(0, post.Likes);
        Assert.False(post.HasMyLike);
        Assert.Empty(post.Comments);
        Assert.True(post.CanManage);
    }

    [SkippableFact]
    public async Task RefusesAPostWithNothingInIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            PostRefusal.ContentRequired,
            (await Feed(provider).PostAsync(Writing(content: "   "))).Refusal);
    }

    [SkippableFact]
    public async Task RefusesAnEventWithNoDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            PostRefusal.EventDateRequired,
            (await Feed(provider).PostAsync(Writing(type: PostType.Event))).Refusal);
    }

    [SkippableFact]
    public async Task PostsAnEventWithADateAndAPlace()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider, Writing(
            type: PostType.Event,
            content: "Quarterly all-hands.",
            eventOn: new DateOnly(2026, 7, 1),
            eventLocation: "The Lagos office"));

        Assert.Equal(new DateOnly(2026, 7, 1), post.EventOn);
        Assert.Equal("The Lagos office", post.EventLocation);
    }

    [SkippableFact]
    public async Task RefusesACelebrationWithNobodyItIsAbout()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            PostRefusal.CelebrationPersonRequired,
            (await Feed(provider).PostAsync(Writing(
                type: PostType.Milestone,
                celebrationKind: CelebrationKind.Anniversary))).Refusal);
    }

    [SkippableFact]
    public async Task NamesThePersonACelebrationIsAbout()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider, Writing(
            type: PostType.Milestone,
            content: "Five years with us today.",
            celebrationKind: CelebrationKind.Anniversary,
            celebrationPersonId: _bolaId));

        Assert.Equal("Bola Okafor", post.CelebrationPersonName);
    }

    [SkippableFact]
    public async Task RefusesACelebrationForSomebodyWhoDoesNotWorkHere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            PostOutcome.EmployeeNotFound,
            (await Feed(provider).PostAsync(Writing(
                type: PostType.Milestone,
                celebrationKind: CelebrationKind.Birthday,
                celebrationPersonId: Guid.NewGuid()))).Outcome);
    }

    // ── likes ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task LikingTwiceTakesItBack()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider);

        PostResult liked = await Feed(provider).ToggleLikeAsync(post.Id);

        Assert.Equal(1, liked.Post!.Likes);
        Assert.True(liked.Post.HasMyLike);

        PostResult unliked = await Feed(provider).ToggleLikeAsync(post.Id);

        Assert.Equal(0, unliked.Post!.Likes);
        Assert.False(unliked.Post.HasMyLike);
    }

    [SkippableFact]
    public async Task CountsEverybodysLikesAndKnowsWhichAreMine()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider);

        await Feed(provider).ToggleLikeAsync(post.Id);

        await ActAsync(provider, _bolaId);

        PostResult theirs = await Feed(provider).ToggleLikeAsync(post.Id);

        Assert.Equal(2, theirs.Post!.Likes);
        Assert.True(theirs.Post.HasMyLike);
    }

    // ── polls ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task APollStartsWithNoVotesAndNoShares()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider, Poll());

        Assert.Equal(2, post.PollOptions.Count);
        Assert.Equal(0, post.PollVotes);
        Assert.Null(post.MyVoteOptionId);

        // Null rather than nought across the board, so the screen can say "no
        // votes yet" instead of drawing empty bars that read as abstention.
        Assert.All(post.PollOptions, option => Assert.Null(option.Share));
    }

    [SkippableFact]
    public async Task RefusesAPollWithFewerThanTwoAnswers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostResult result = await Feed(provider).PostAsync(Writing(
            type: PostType.Poll,
            pollQuestion: "Which day?",
            pollOptions: ["Tuesday"]));

        Assert.Equal(PostRefusal.PollNeedsTwoOptions, result.Refusal);
    }

    // Two answers worded the same make a result nobody can read.
    [SkippableFact]
    public async Task FoldsPollAnswersThatDifferOnlyByCase()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostResult result = await Feed(provider).PostAsync(Writing(
            type: PostType.Poll,
            pollQuestion: "Which day?",
            pollOptions: ["Tuesday", " tuesday "]));

        // Folded down to one, which is no longer a poll.
        Assert.Equal(PostRefusal.PollNeedsTwoOptions, result.Refusal);
    }

    [SkippableFact]
    public async Task SharesOutThePollAcrossItsAnswers()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider, Poll());

        await Feed(provider).VoteAsync(post.Id, post.PollOptions[0].Id);

        await ActAsync(provider, _bolaId);

        PostResult second = await Feed(provider).VoteAsync(post.Id, post.PollOptions[0].Id);

        Assert.Equal(2, second.Post!.PollVotes);
        Assert.Equal(100m, second.Post.PollOptions[0].Share);
        Assert.Equal(0m, second.Post.PollOptions[1].Share);
    }

    // Changing your mind moves the vote rather than adding one, so the shares
    // always total a hundred.
    [SkippableFact]
    public async Task VotingAgainMovesTheVoteRatherThanAddingOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider, Poll());

        await Feed(provider).VoteAsync(post.Id, post.PollOptions[0].Id);

        PostResult moved = await Feed(provider).VoteAsync(post.Id, post.PollOptions[1].Id);

        Assert.Equal(1, moved.Post!.PollVotes);
        Assert.Equal(0, moved.Post.PollOptions[0].Votes);
        Assert.Equal(1, moved.Post.PollOptions[1].Votes);
        Assert.Equal(post.PollOptions[1].Id, moved.Post.MyVoteOptionId);
    }

    // An answer from somebody else's poll would otherwise count towards this
    // one.
    [SkippableFact]
    public async Task RefusesAnAnswerFromAnotherPoll()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto first = await PostedAsync(provider, Poll());
        PostDto second = await PostedAsync(provider, Poll());

        Assert.Equal(
            PostRefusal.UnknownOption,
            (await Feed(provider).VoteAsync(first.Id, second.PollOptions[0].Id)).Refusal);
    }

    [SkippableFact]
    public async Task CannotVoteOnSomethingThatIsNotAPoll()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider);

        Assert.Equal(
            PostRefusal.NotAPoll,
            (await Feed(provider).VoteAsync(post.Id, Guid.NewGuid())).Refusal);
    }

    // The votes point at the option rows, so rewriting them would strand
    // every vote already cast.
    [SkippableFact]
    public async Task APollWithVotesCannotHaveItsAnswersRewritten()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider, Poll());

        await Feed(provider).VoteAsync(post.Id, post.PollOptions[0].Id);

        PostResult result = await Feed(provider).UpdateAsync(
            post.Id,
            Writing(
                type: PostType.Poll,
                content: "Picking a day for the next socials.",
                pollQuestion: "Which day suits you?",
                pollOptions: ["Monday", "Friday"]),
            canModerate: false);

        Assert.Equal(PostOutcome.PollHasVotes, result.Outcome);
    }

    // Editing the wording around an untouched set of answers is fine.
    [SkippableFact]
    public async Task APollWithVotesCanStillHaveItsWordingFixed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider, Poll());

        await Feed(provider).VoteAsync(post.Id, post.PollOptions[0].Id);

        PostResult result = await Feed(provider).UpdateAsync(
            post.Id,
            Writing(
                type: PostType.Poll,
                content: "Picking a day for the next socials — please vote by Friday.",
                pollQuestion: "Which day suits you?",
                pollOptions: ["Tuesday", "Thursday"]),
            canModerate: false);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");
        Assert.Equal(1, result.Post!.PollVotes);
    }

    // ── comments ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CommentsOnAPost()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider);

        PostResult result = await Feed(provider).CommentAsync(post.Id, "Useful, thanks.");

        PostCommentDto comment = Assert.Single(result.Post!.Comments);

        Assert.Equal("Useful, thanks.", comment.Message);
        Assert.Equal("Ada Okafor", comment.AuthorName);
        Assert.True(comment.CanRemove);
    }

    [SkippableFact]
    public async Task RefusesAnEmptyComment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider);

        Assert.Equal(
            PostRefusal.CommentRequired,
            (await Feed(provider).CommentAsync(post.Id, "   ")).Refusal);
    }

    [SkippableFact]
    public async Task OnlyTheAuthorOrAModeratorCanRemoveAComment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider);

        Guid commentId = (await Feed(provider).CommentAsync(post.Id, "Useful, thanks."))
            .Post!.Comments[0].Id;

        await ActAsync(provider, _bolaId);

        Assert.Equal(
            PostRefusal.NotYours,
            (await Feed(provider).RemoveCommentAsync(
                post.Id, commentId, canModerate: false)).Refusal);

        Assert.True((await Feed(provider).RemoveCommentAsync(
            post.Id, commentId, canModerate: true)).Succeeded);
    }

    // ── editing and removing ────────────────────────────────────────────────

    [SkippableFact]
    public async Task OnlyTheAuthorOrAModeratorCanRemoveAPost()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider);

        await ActAsync(provider, _bolaId);

        Assert.Equal(
            PostOutcome.Forbidden,
            (await Feed(provider).DeleteAsync(post.Id, canModerate: false)).Outcome);

        Assert.True((await Feed(provider).DeleteAsync(post.Id, canModerate: true)).Succeeded);
    }

    [SkippableFact]
    public async Task RemovingAPostTakesItsLikesCommentsAndVotesWithIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto post = await PostedAsync(provider, Poll());

        await Feed(provider).ToggleLikeAsync(post.Id);
        await Feed(provider).CommentAsync(post.Id, "Tuesday works.");
        await Feed(provider).VoteAsync(post.Id, post.PollOptions[0].Id);

        Assert.True((await Feed(provider).DeleteAsync(post.Id, canModerate: false)).Succeeded);

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        Assert.Empty(await read.PostReactions.ToListAsync());
        Assert.Empty(await read.PostComments.ToListAsync());
        Assert.Empty(await read.PostPollVotes.ToListAsync());
        Assert.Empty(await read.PostPollOptions.ToListAsync());
    }

    // ── the feed ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task PinnedPostsComeFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PostedAsync(provider, Writing(content: "An ordinary update."));

        PostDto pinned = await PostedAsync(provider, Writing(content: "Read this first."));

        await Feed(provider).SetPinnedAsync(pinned.Id, true);

        PagedResult<PostDto> feed = await Feed(provider).FeedAsync(new FeedQuery());

        Assert.Equal(pinned.Id, feed.Items[0].Id);
        Assert.True(feed.Items[0].IsPinned);
    }

    [SkippableFact]
    public async Task FiltersToUpcomingEvents()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto upcoming = await PostedAsync(provider, Writing(
            type: PostType.Event,
            content: "Next month's all-hands.",
            eventOn: new DateOnly(2026, 7, 1)));

        await PostedAsync(provider, Writing(
            type: PostType.Event,
            content: "Last month's all-hands.",
            eventOn: new DateOnly(2026, 5, 1)));

        await PostedAsync(provider, Writing(content: "Not an event at all."));

        PagedResult<PostDto> events = await Feed(provider)
            .FeedAsync(new FeedQuery { UpcomingEventsOnly = true });

        Assert.Equal([upcoming.Id], events.Items.Select(row => row.Id));
    }

    [SkippableFact]
    public async Task FiltersByPostType()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PostedAsync(provider);
        PostDto poll = await PostedAsync(provider, Poll());

        PagedResult<PostDto> polls = await Feed(provider)
            .FeedAsync(new FeedQuery { PostType = PostType.Poll });

        Assert.Equal([poll.Id], polls.Items.Select(row => row.Id));
    }

    [SkippableFact]
    public async Task DoesNotTreatASearchTermAsAWildcard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PostedAsync(provider, Writing(content: "We hit 100% of the quarter's target."));
        await PostedAsync(provider, Writing(content: "Something else entirely."));

        Assert.Single((await Feed(provider).FeedAsync(new FeedQuery { Search = "100%" })).Items);
    }

    // ── the figures ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ReportsTheFeedsFigures()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        PostDto update = await PostedAsync(provider);
        PostDto poll = await PostedAsync(provider, Poll());

        await Feed(provider).ToggleLikeAsync(update.Id);
        await Feed(provider).CommentAsync(update.Id, "Useful, thanks.");
        await Feed(provider).VoteAsync(poll.Id, poll.PollOptions[0].Id);

        FeedTally tally = await Feed(provider).SummaryAsync();

        Assert.Equal(2, tally.Posts);
        Assert.Equal(1, tally.Reactions);
        Assert.Equal(1, tally.Comments);
        Assert.Equal(1, tally.PollVotes);

        // Ada and Bola. The account owner has no employee record.
        Assert.Equal(2, tally.Members);
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersFeed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PostedAsync(provider);

        RegisterTenantResult other = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ola",
            LastName = "Bello",
            Email = "ola@other.com",
            CompanyName = "Other Limited",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(other.Succeeded, string.Join("; ", other.Errors));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = other.TenantId;

        Assert.Empty((await Feed(provider).FeedAsync(new FeedQuery())).Items);
        Assert.Equal(0, (await Feed(provider).SummaryAsync()).Posts);
    }
}
