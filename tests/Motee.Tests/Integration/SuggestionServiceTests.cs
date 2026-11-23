using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Suggestions;
using Motee.Domain.Employees;
using Motee.Domain.Organisation;
using Motee.Domain.Suggestions;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The suggestion box, out of the database.
//
// The tests that matter most are the anonymity ones. An anonymous suggestion's
// submitter must not come back for anybody — not a colleague, not HR, not the
// account owner — and must still come back to its author, who wrote it and has
// to be able to find it. Everything else is the usual: votes are rows, the
// count is read from them, and the resolved date cannot drift from the status.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class SuggestionServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(new DateOnly(2026, 4, 23).ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

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

    private static ISuggestionService Box(ServiceProvider provider) =>
        Resolve<ISuggestionService>(provider);

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

    private const string Title = "Move the all-hands to Tuesday mornings";

    private const string Description =
        "Thursday afternoons clash with the regional stand-ups, so half the team "
        + "joins late or not at all.";

    private static SuggestionRequest Asking(
        string title = Title,
        string description = Description,
        SuggestionCategory category = SuggestionCategory.Process,
        bool anonymous = false) => new()
        {
            Title = title,
            Description = description,
            Category = category,
            IsAnonymous = anonymous,
        };

    private static async Task<SuggestionDto> SubmittedAsync(
        ServiceProvider provider,
        SuggestionRequest request)
    {
        SuggestionResult result = await Box(provider).SubmitAsync(request);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Suggestion!;
    }

    // ── anonymity ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ASignedSuggestionNamesItsSubmitter()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto suggestion = await SubmittedAsync(provider, Asking());

        Assert.False(suggestion.IsAnonymous);
        Assert.Equal(_adaId, suggestion.SubmitterEmployeeId);
        Assert.Equal("Ada Okafor", suggestion.SubmitterName);
        Assert.Equal("Engineering", suggestion.SubmitterDepartmentName);
        Assert.True(suggestion.IsMine);
    }

    // The whole point of the box. Nobody is exempt.
    [SkippableFact]
    public async Task AnAnonymousSuggestionNamesNobodyToAColleague()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto submitted = await SubmittedAsync(provider, Asking(anonymous: true));

        await ActAsync(provider, _bolaId);

        SuggestionDto read = (await Box(provider).GetAsync(submitted.Id))!;

        Assert.True(read.IsAnonymous);
        Assert.Null(read.SubmitterEmployeeId);
        Assert.Null(read.SubmitterName);
        Assert.Null(read.SubmitterDepartmentName);
        Assert.False(read.IsMine);
    }

    // Including the account owner, who has no employee record and is as close
    // to "root" as this product has.
    [SkippableFact]
    public async Task AnAnonymousSuggestionNamesNobodyToTheAccountOwner()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto submitted = await SubmittedAsync(provider, Asking(anonymous: true));

        provider.GetRequiredService<PostgresFixture.StubRequestContext>().UserId =
            Guid.NewGuid().ToString();

        SuggestionDto read = (await Box(provider).GetAsync(submitted.Id))!;

        Assert.Null(read.SubmitterEmployeeId);
        Assert.Null(read.SubmitterName);
    }

    // And it is still theirs to find.
    [SkippableFact]
    public async Task AnAnonymousSuggestionIsStillRecognisableToItsAuthor()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto submitted = await SubmittedAsync(provider, Asking(anonymous: true));

        SuggestionDto read = (await Box(provider).GetAsync(submitted.Id))!;

        Assert.True(read.IsMine);
        Assert.Equal(_adaId, read.SubmitterEmployeeId);

        IReadOnlyList<SuggestionDto> mine = await Box(provider).MineAsync();

        Assert.Equal([submitted.Id], mine.Select(row => row.Id));
    }

    // The name is on the row even when it is never disclosed — an anonymous
    // suggestion still has to belong to somebody, or the same person could
    // vote on their own idea twice over and nobody could act on abuse.
    [SkippableFact]
    public async Task TheSubmitterIsRecordedEvenWhenItIsNeverDisclosed()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto submitted = await SubmittedAsync(provider, Asking(anonymous: true));

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        Suggestion row = await read.Suggestions.FirstAsync(item => item.Id == submitted.Id);

        Assert.Equal(_adaId, row.SubmitterEmployeeId);
    }

    // ── submitting ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ANewSuggestionStartsSubmittedAtMediumPriority()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto suggestion = await SubmittedAsync(provider, Asking());

        Assert.Equal(SuggestionStatus.Submitted, suggestion.Status);
        Assert.Equal(SuggestionPriority.Medium, suggestion.Priority);
        Assert.False(suggestion.IsResolved);
        Assert.Null(suggestion.ResolvedOn);
        Assert.Equal(0, suggestion.Upvotes);
        Assert.False(suggestion.IsTrending);
    }

    [SkippableFact]
    public async Task RefusesATitleTooShortToScan()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            SuggestionRefusal.TitleRequired,
            (await Box(provider).SubmitAsync(Asking(title: "Wifi"))).Refusal);
    }

    [SkippableFact]
    public async Task RefusesADescriptionTooShortToActOn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            SuggestionRefusal.DescriptionRequired,
            (await Box(provider).SubmitAsync(Asking(description: "Too short"))).Refusal);
    }

    // ── upvotes ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task VotingTwiceTakesTheVoteBack()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto suggestion = await SubmittedAsync(provider, Asking());

        SuggestionResult on = await Box(provider).ToggleUpvoteAsync(suggestion.Id);

        Assert.Equal(1, on.Suggestion!.Upvotes);
        Assert.True(on.Suggestion.HasMyVote);

        SuggestionResult off = await Box(provider).ToggleUpvoteAsync(suggestion.Id);

        Assert.Equal(0, off.Suggestion!.Upvotes);
        Assert.False(off.Suggestion.HasMyVote);
    }

    [SkippableFact]
    public async Task CountsEverybodysVotesAndKnowsWhichAreMine()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto suggestion = await SubmittedAsync(provider, Asking());

        await Box(provider).ToggleUpvoteAsync(suggestion.Id);

        await ActAsync(provider, _bolaId);

        SuggestionResult mine = await Box(provider).ToggleUpvoteAsync(suggestion.Id);

        Assert.Equal(2, mine.Suggestion!.Upvotes);
        Assert.True(mine.Suggestion.HasMyVote);
    }

    // The client calls anything over ten votes trending for ever, so a
    // two-year-old suggestion with eleven votes sits in the trending rail.
    [SkippableFact]
    public async Task TrendingIsRecentVotesNotLifetimeVotes()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto fresh = await SubmittedAsync(provider, Asking(title: "Trending one here"));
        SuggestionDto stale = await SubmittedAsync(provider, Asking(title: "Popular long ago"));

        await VotesAsync(provider, fresh.Id, new DateOnly(2026, 4, 20), SuggestionRules.TrendingVotes);
        await VotesAsync(provider, stale.Id, new DateOnly(2024, 1, 1), SuggestionRules.TrendingVotes + 5);

        Assert.True((await Box(provider).GetAsync(fresh.Id))!.IsTrending);

        SuggestionDto old = (await Box(provider).GetAsync(stale.Id))!;

        Assert.Equal(SuggestionRules.TrendingVotes + 5, old.Upvotes);
        Assert.False(old.IsTrending);
    }

    // Seeded directly: proving the window needs more voters than the roster,
    // and the votes themselves are what is under test rather than the route.
    private async Task VotesAsync(
        ServiceProvider provider,
        Guid suggestionId,
        DateOnly votedOn,
        int howMany)
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        for (int number = 0; number < howMany; number++)
        {
            Guid voterId = await HireAsync(provider, $"Voter{number}{votedOn.Year}");

            seed.SuggestionVotes.Add(new SuggestionVote
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                SuggestionId = suggestionId,
                EmployeeId = voterId,
                VotedOn = votedOn,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await seed.SaveChangesAsync();
    }

    // ── HR's triage ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ResolvingStampsTheDayAndReopeningClearsIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto suggestion = await SubmittedAsync(provider, Asking());

        SuggestionResult done = await Box(provider)
            .SetStatusAsync(suggestion.Id, SuggestionStatus.Implemented);

        Assert.True(done.Suggestion!.IsResolved);
        Assert.Equal(new DateOnly(2026, 4, 23), done.Suggestion.ResolvedOn);

        SuggestionResult reopened = await Box(provider)
            .SetStatusAsync(suggestion.Id, SuggestionStatus.UnderReview);

        Assert.False(reopened.Suggestion!.IsResolved);
        Assert.Null(reopened.Suggestion.ResolvedOn);
    }

    [SkippableFact]
    public async Task MovingBetweenTwoClosedStatesKeepsTheOriginalDay()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto suggestion = await SubmittedAsync(provider, Asking());

        await Box(provider).SetStatusAsync(suggestion.Id, SuggestionStatus.Declined);

        _clock.Advance(TimeSpan.FromDays(10));

        SuggestionResult moved = await Box(provider)
            .SetStatusAsync(suggestion.Id, SuggestionStatus.Implemented);

        Assert.Equal(new DateOnly(2026, 4, 23), moved.Suggestion!.ResolvedOn);
    }

    [SkippableFact]
    public async Task SavesThePriorityAndThePublishedResponse()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto suggestion = await SubmittedAsync(provider, Asking());

        Assert.Equal(
            SuggestionPriority.High,
            (await Box(provider).SetPriorityAsync(suggestion.Id, SuggestionPriority.High))
                .Suggestion!.Priority);

        SuggestionResult responded = await Box(provider).RespondAsync(
            suggestion.Id,
            new SuggestionResponseRequest
            {
                AdminResponse = "Agreed — moving it from May.",
                ImplementationNotes = "Calendar invites reissued.",
            });

        Assert.Equal("Agreed — moving it from May.", responded.Suggestion!.AdminResponse);
        Assert.Equal("Calendar invites reissued.", responded.Suggestion.ImplementationNotes);
    }

    [SkippableFact]
    public async Task FeaturedSuggestionsComeFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SubmittedAsync(provider, Asking(title: "The ordinary one here"));

        SuggestionDto featured = await SubmittedAsync(
            provider, Asking(title: "The featured one here"));

        await Box(provider).SetFeaturedAsync(featured.Id, true);

        PagedResult<SuggestionDto> listed = await Box(provider).ListAsync(new SuggestionQuery());

        Assert.Equal(featured.Id, listed.Items[0].Id);

        Assert.Equal(
            [featured.Id],
            (await Box(provider).ListAsync(new SuggestionQuery { FeaturedOnly = true }))
                .Items.Select(row => row.Id));
    }

    // ── comments ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ACommentFromHrIsBadgedAsOfficialAndOneFromAColleagueIsNot()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto suggestion = await SubmittedAsync(provider, Asking());

        await Box(provider).CommentAsync(suggestion.Id, "Good idea.", official: false);
        await Box(provider).CommentAsync(suggestion.Id, "We are doing this.", official: true);

        IReadOnlyList<SuggestionCommentDto> comments =
            (await Box(provider).GetAsync(suggestion.Id))!.Comments;

        Assert.Equal([false, true], comments.Select(comment => comment.IsOfficial));
        Assert.All(comments, comment => Assert.Equal("Ada Okafor", comment.AuthorName));
    }

    [SkippableFact]
    public async Task RefusesAnEmptyComment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto suggestion = await SubmittedAsync(provider, Asking());

        Assert.Equal(
            SuggestionRefusal.CommentRequired,
            (await Box(provider).CommentAsync(suggestion.Id, "   ", official: false)).Refusal);
    }

    [SkippableFact]
    public async Task OnlyTheAuthorOrAModeratorCanRemoveAComment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto suggestion = await SubmittedAsync(provider, Asking());

        Guid commentId = (await Box(provider)
            .CommentAsync(suggestion.Id, "Good idea.", official: false))
            .Suggestion!.Comments[0].Id;

        await ActAsync(provider, _bolaId);

        Assert.Equal(
            SuggestionOutcome.Forbidden,
            (await Box(provider).RemoveCommentAsync(
                suggestion.Id, commentId, canRemoveAnybodys: false)).Outcome);

        Assert.True((await Box(provider).RemoveCommentAsync(
            suggestion.Id, commentId, canRemoveAnybodys: true)).Succeeded);
    }

    // A comment on an anonymous suggestion still names its own author — they
    // chose to speak up, and only the submitter asked not to be named.
    [SkippableFact]
    public async Task CommentsOnAnAnonymousSuggestionStillNameTheirAuthors()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto suggestion = await SubmittedAsync(provider, Asking(anonymous: true));

        await ActAsync(provider, _bolaId);
        await Box(provider).CommentAsync(suggestion.Id, "Seconded.", official: false);

        SuggestionDto read = (await Box(provider).GetAsync(suggestion.Id))!;

        Assert.Null(read.SubmitterName);
        Assert.Equal("Bola Okafor", Assert.Single(read.Comments).AuthorName);
    }

    // ── the board ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ReportsTheFourKpiCards()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto implemented = await SubmittedAsync(provider, Asking(title: "Shipped one now"));
        SuggestionDto reviewing = await SubmittedAsync(provider, Asking(title: "Looking at this"));
        SuggestionDto accepted = await SubmittedAsync(provider, Asking(title: "Agreed to do it"));
        await SubmittedAsync(provider, Asking(title: "Still just submitted"));

        await Box(provider).SetStatusAsync(implemented.Id, SuggestionStatus.Implemented);
        await Box(provider).SetStatusAsync(reviewing.Id, SuggestionStatus.UnderReview);
        await Box(provider).SetStatusAsync(accepted.Id, SuggestionStatus.Accepted);

        SuggestionTally tally = await Box(provider).SummaryAsync();

        Assert.Equal(4, tally.Total);
        Assert.Equal(1, tally.Implemented);

        // Under review counts accepted ones too — the client's own rule.
        Assert.Equal(2, tally.UnderReview);
    }

    [SkippableFact]
    public async Task SortsByMostUpvoted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        SuggestionDto quiet = await SubmittedAsync(provider, Asking(title: "The quiet one here"));
        SuggestionDto popular = await SubmittedAsync(provider, Asking(title: "The popular one"));

        await Box(provider).ToggleUpvoteAsync(popular.Id);

        PagedResult<SuggestionDto> listed = await Box(provider).ListAsync(
            new SuggestionQuery { Sort = SuggestionSort.MostUpvoted });

        Assert.Equal([popular.Id, quiet.Id], listed.Items.Select(row => row.Id));
    }

    [SkippableFact]
    public async Task FiltersByCategoryAndStatus()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SubmittedAsync(provider, Asking(category: SuggestionCategory.Wellbeing));
        SuggestionDto tool = await SubmittedAsync(provider, Asking(
            title: "Buy a second monitor", category: SuggestionCategory.Tools));

        await Box(provider).SetStatusAsync(tool.Id, SuggestionStatus.Declined);

        Assert.Equal(
            [tool.Id],
            (await Box(provider).ListAsync(
                new SuggestionQuery { Category = SuggestionCategory.Tools }))
                .Items.Select(row => row.Id));

        Assert.Equal(
            [tool.Id],
            (await Box(provider).ListAsync(
                new SuggestionQuery { Status = SuggestionStatus.Declined }))
                .Items.Select(row => row.Id));
    }

    [SkippableFact]
    public async Task DoesNotTreatASearchTermAsAWildcard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SubmittedAsync(provider, Asking(title: "Hit 100% on the survey"));
        await SubmittedAsync(provider, Asking(title: "Something else entirely"));

        Assert.Single(
            (await Box(provider).ListAsync(new SuggestionQuery { Search = "100%" })).Items);
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersBox()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await SubmittedAsync(provider, Asking());

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

        Assert.Empty((await Box(provider).ListAsync(new SuggestionQuery())).Items);
        Assert.Equal(0, (await Box(provider).SummaryAsync()).Total);
    }
}
