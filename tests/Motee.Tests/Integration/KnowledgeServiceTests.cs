using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Knowledge;
using Motee.Application.Organisation;
using Motee.Domain.Employees;
using Motee.Domain.Knowledge;
using Motee.Domain.Organisation;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// The knowledge base, out of the database.
//
// Two things these prove. A draft never leaves the service for a reader — the
// authoring grant is what decides, and without it the list and the detail
// route return what a reader would see rather than failing. And the figures
// come from rows: views and readers are different numbers, and a reader can
// change their mind about an article without it counting twice.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class KnowledgeServiceTests(PostgresFixture fixture)
{
    private readonly MovableClock _clock = new(
        new DateTimeOffset(new DateOnly(2026, 6, 15).ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _departmentId;
    private Guid _authorId;
    private Guid _readerId;

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
            .CreateAsync(new DepartmentRequest { Name = "People", Code = "PPL" }))
            .Department!.Id;

        _authorId = await HireAsync(provider, "Ada");
        _readerId = await HireAsync(provider, "Bola");

        await AccountAsync(_authorId, "ada");
        await AccountAsync(_readerId, "bola");

        await ActAsync(provider, _authorId);

        return provider;
    }

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static IKnowledgeService Base(ServiceProvider provider) =>
        Resolve<IKnowledgeService>(provider);

    private async Task<Guid> HireAsync(ServiceProvider provider, string first) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Adviser",
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

    private const string Title = "How to claim a travel expense";

    private static readonly string Body = string.Join(
        " ",
        Enumerable.Repeat("Attach the receipt and submit it through Expenses.", 3));

    private static KnowledgeArticleRequest Writing(
        string title = Title,
        string? body = null,
        string? summary = null,
        ArticleCategory category = ArticleCategory.Payroll,
        ArticleStatus status = ArticleStatus.Draft,
        IReadOnlyList<string>? tags = null,
        bool featured = false) => new()
        {
            Title = title,
            Body = body ?? Body,
            Summary = summary,
            Category = category,
            Status = status,
            Tags = tags,
            IsFeatured = featured,
        };

    private static async Task<KnowledgeArticleDto> WrittenAsync(
        ServiceProvider provider,
        KnowledgeArticleRequest request)
    {
        KnowledgeResult result = await Base(provider).CreateAsync(request);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Article!;
    }

    private static async Task<KnowledgeArticleDto> PublishedAsync(
        ServiceProvider provider,
        KnowledgeArticleRequest request)
    {
        KnowledgeArticleDto article = await WrittenAsync(provider, request);

        KnowledgeResult result = await Base(provider)
            .SetStatusAsync(article.Id, ArticleStatus.Published);

        Assert.True(result.Succeeded, $"{result.Outcome} / {result.Refusal}");

        return result.Article!;
    }

    // ── writing ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ANewArticleIsADraftWithNoPublicationDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto article = await WrittenAsync(provider, Writing());

        Assert.Equal(ArticleStatus.Draft, article.Status);
        Assert.Null(article.PublishedOn);
        Assert.Equal("Ada Okafor", article.AuthorName);
        Assert.Equal(0, article.Views);
        Assert.Equal(0, article.Readers);

        // Nobody has voted, so there is no score — not nought per cent.
        Assert.Null(article.HelpfulPercent);
    }

    [SkippableFact]
    public async Task RefusesABodyTooShortToBeAnArticle()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            KnowledgeRefusal.BodyRequired,
            (await Base(provider).CreateAsync(Writing(body: "Ask HR."))).Refusal);
    }

    [SkippableFact]
    public async Task RefusesATitleTooShortToFind()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            KnowledgeRefusal.TitleRequired,
            (await Base(provider).CreateAsync(Writing(title: "Pay"))).Refusal);
    }

    // The form sends one comma-separated string, so without folding a reader
    // filters by "payroll" and misses everything tagged "Payroll".
    [SkippableFact]
    public async Task FoldsTagsThatDifferOnlyByCaseOrSpacing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto article = await WrittenAsync(
            provider, Writing(tags: ["Payroll", " payroll ", "Expenses"]));

        Assert.Equal(["payroll", "expenses"], article.Tags);
    }

    [SkippableFact]
    public async Task FindsAnArticleByItsTagWhateverCaseIsAsked()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PublishedAsync(provider, Writing(tags: ["Payroll"]));
        await PublishedAsync(provider, Writing(title: "Booking annual leave", tags: ["leave"]));

        KnowledgeListDto found = await Base(provider)
            .PublishedAsync(new KnowledgeQuery { Tag = "PAYROLL" });

        Assert.Equal([Title], found.Articles.Items.Select(article => article.Title));
    }

    // ── publishing ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task PublishingStampsTheDayAndArchivingKeepsIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto article = await PublishedAsync(provider, Writing());

        Assert.Equal(new DateOnly(2026, 6, 15), article.PublishedOn);

        _clock.Advance(TimeSpan.FromDays(30));

        KnowledgeResult archived = await Base(provider)
            .SetStatusAsync(article.Id, ArticleStatus.Archived);

        // It was published, and that it once was is worth keeping.
        Assert.Equal(new DateOnly(2026, 6, 15), archived.Article!.PublishedOn);
    }

    [SkippableFact]
    public async Task PullingAnArticleBackToDraftClearsThePublicationDate()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto article = await PublishedAsync(provider, Writing());

        KnowledgeResult drafted = await Base(provider)
            .SetStatusAsync(article.Id, ArticleStatus.Draft);

        Assert.Null(drafted.Article!.PublishedOn);
    }

    [SkippableFact]
    public async Task RepublishingKeepsTheDayItFirstWentOut()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto article = await PublishedAsync(provider, Writing());

        await Base(provider).SetStatusAsync(article.Id, ArticleStatus.Archived);

        _clock.Advance(TimeSpan.FromDays(60));

        KnowledgeResult again = await Base(provider)
            .SetStatusAsync(article.Id, ArticleStatus.Published);

        Assert.Equal(new DateOnly(2026, 6, 15), again.Article!.PublishedOn);
    }

    // ── what a reader may see ───────────────────────────────────────────────

    [SkippableFact]
    public async Task ADraftNeverReachesAReader()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto draft = await WrittenAsync(provider, Writing());

        Assert.Empty((await Base(provider)
            .PublishedAsync(new KnowledgeQuery())).Articles.Items);

        Assert.Null(await Base(provider).GetAsync(draft.Id, includeUnpublished: false));

        // And the author can still see it.
        Assert.NotNull(await Base(provider).GetAsync(draft.Id, includeUnpublished: true));
    }

    [SkippableFact]
    public async Task AnArchivedArticleIsWithdrawnFromReaders()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto article = await PublishedAsync(provider, Writing());

        await Base(provider).SetStatusAsync(article.Id, ArticleStatus.Archived);

        Assert.Null(await Base(provider).GetAsync(article.Id, includeUnpublished: false));
    }

    // Without the authoring grant the list narrows rather than failing, so a
    // reader opening the same screen sees the published set.
    [SkippableFact]
    public async Task TheListWithoutTheAuthoringGrantShowsOnlyWhatIsPublished()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await WrittenAsync(provider, Writing(title: "A draft nobody should see"));
        await PublishedAsync(provider, Writing());

        KnowledgeListDto asReader = await Base(provider)
            .ListAsync(new KnowledgeQuery(), includeUnpublished: false);

        Assert.Equal([Title], asReader.Articles.Items.Select(article => article.Title));

        KnowledgeListDto asAuthor = await Base(provider)
            .ListAsync(new KnowledgeQuery(), includeUnpublished: true);

        Assert.Equal(2, asAuthor.Articles.TotalItems);
    }

    // ── reading ─────────────────────────────────────────────────────────────

    // Two different numbers: how many people it reached, and how often it was
    // opened. The client keeps only the second.
    [SkippableFact]
    public async Task CountsViewsAndReadersSeparately()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto article = await PublishedAsync(provider, Writing());

        await Base(provider).RecordReadAsync(article.Id);
        await Base(provider).RecordReadAsync(article.Id);

        await ActAsync(provider, _readerId);

        KnowledgeResult third = await Base(provider).RecordReadAsync(article.Id);

        Assert.Equal(3, third.Article!.Views);
        Assert.Equal(2, third.Article.Readers);
        Assert.True(third.Article.HaveIRead);
    }

    [SkippableFact]
    public async Task SomebodyWhoHasNotOpenedItHasNotReadIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto article = await PublishedAsync(provider, Writing());

        await Base(provider).RecordReadAsync(article.Id);

        await ActAsync(provider, _readerId);

        Assert.False((await Base(provider).GetAsync(article.Id, false))!.HaveIRead);
    }

    // Which also stops an author inflating their own figures before anybody
    // else can see it.
    [SkippableFact]
    public async Task ADraftCannotBeReadOrRated()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto draft = await WrittenAsync(provider, Writing());

        Assert.Equal(
            KnowledgeOutcome.Refused,
            (await Base(provider).RecordReadAsync(draft.Id)).Outcome);

        Assert.Equal(
            KnowledgeOutcome.Refused,
            (await Base(provider).VoteAsync(draft.Id, ArticleVote.Helpful)).Outcome);
    }

    // ── was it helpful ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CountsBothSidesOfTheVote()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto article = await PublishedAsync(provider, Writing());

        await Base(provider).VoteAsync(article.Id, ArticleVote.Helpful);

        await ActAsync(provider, _readerId);

        KnowledgeResult second = await Base(provider)
            .VoteAsync(article.Id, ArticleVote.NotHelpful);

        Assert.Equal(1, second.Article!.HelpfulVotes);
        Assert.Equal(1, second.Article.NotHelpfulVotes);
        Assert.Equal(50m, second.Article.HelpfulPercent);
        Assert.Equal(ArticleVote.NotHelpful, second.Article.MyVote);
    }

    // Changing your mind is not two votes.
    [SkippableFact]
    public async Task SwitchingYourVoteReplacesItRatherThanAddingOne()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto article = await PublishedAsync(provider, Writing());

        await Base(provider).VoteAsync(article.Id, ArticleVote.Helpful);

        KnowledgeResult changed = await Base(provider)
            .VoteAsync(article.Id, ArticleVote.NotHelpful);

        Assert.Equal(0, changed.Article!.HelpfulVotes);
        Assert.Equal(1, changed.Article.NotHelpfulVotes);
    }

    [SkippableFact]
    public async Task WithdrawingFeedbackLeavesNoVote()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto article = await PublishedAsync(provider, Writing());

        await Base(provider).VoteAsync(article.Id, ArticleVote.Helpful);

        KnowledgeResult cleared = await Base(provider).ClearVoteAsync(article.Id);

        Assert.Equal(0, cleared.Article!.HelpfulVotes);
        Assert.Null(cleared.Article.MyVote);
        Assert.Null(cleared.Article.HelpfulPercent);
    }

    // ── the shelf ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task FeaturedArticlesComeFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PublishedAsync(provider, Writing(title: "An ordinary article"));

        KnowledgeArticleDto featured = await PublishedAsync(
            provider, Writing(title: "The featured article"));

        await Base(provider).SetFeaturedAsync(featured.Id, true);

        KnowledgeListDto shelf = await Base(provider).PublishedAsync(new KnowledgeQuery());

        Assert.Equal(featured.Id, shelf.Articles.Items[0].Id);
    }

    // Picking a category must not empty the tiles that offered the choice.
    [SkippableFact]
    public async Task CategoryCountsIgnoreTheCategoryFilter()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PublishedAsync(provider, Writing(category: ArticleCategory.Payroll));
        await PublishedAsync(provider, Writing(
            title: "Your first week here", category: ArticleCategory.Onboarding));

        KnowledgeListDto filtered = await Base(provider).PublishedAsync(
            new KnowledgeQuery { Category = ArticleCategory.Payroll });

        Assert.Single(filtered.Articles.Items);

        Assert.Equal(
            1,
            filtered.CategoryCounts
                .Single(row => row.Category == ArticleCategory.Onboarding).Count);
    }

    [SkippableFact]
    public async Task SearchesTitleSummaryAndBody()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PublishedAsync(provider, Writing(summary: "Receipts and reimbursement"));

        foreach (string term in (string[])["travel", "RECEIPTS", "Expenses"])
        {
            Assert.NotEmpty(
                (await Base(provider).PublishedAsync(new KnowledgeQuery { Search = term }))
                    .Articles.Items);
        }
    }

    [SkippableFact]
    public async Task DoesNotTreatASearchTermAsAWildcard()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PublishedAsync(provider, Writing(title: "Hitting 100% on compliance"));
        await PublishedAsync(provider, Writing(title: "Booking annual leave"));

        Assert.Single(
            (await Base(provider).PublishedAsync(new KnowledgeQuery { Search = "100%" }))
                .Articles.Items);
    }

    [SkippableFact]
    public async Task ReportsTheFourKpiCards()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto published = await PublishedAsync(provider, Writing());
        await WrittenAsync(provider, Writing(title: "A draft in progress"));

        KnowledgeArticleDto archived = await PublishedAsync(
            provider, Writing(title: "An older policy note"));

        await Base(provider).SetStatusAsync(archived.Id, ArticleStatus.Archived);

        await Base(provider).RecordReadAsync(published.Id);
        await Base(provider).RecordReadAsync(published.Id);

        KnowledgeTally tally = await Base(provider).SummaryAsync();

        Assert.Equal(3, tally.Total);
        Assert.Equal(1, tally.Published);
        Assert.Equal(1, tally.Drafts);
        Assert.Equal(1, tally.Archived);
        Assert.Equal(2, tally.TotalViews);
        Assert.Equal(1, tally.TotalReaders);
    }

    // ── deleting ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task DeletingAnArticleTakesItsReadsAndVotesWithIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        KnowledgeArticleDto article = await PublishedAsync(provider, Writing());

        await Base(provider).RecordReadAsync(article.Id);
        await Base(provider).VoteAsync(article.Id, ArticleVote.Helpful);

        Assert.True((await Base(provider).DeleteAsync(article.Id)).Succeeded);

        await using MoteeDbContext read = fixture.CreateContext(_tenantId);

        Assert.Empty(await read.ArticleReads.ToListAsync());
        Assert.Empty(await read.ArticleFeedback.ToListAsync());
    }

    // ── tenancy ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneCompanyNeverSeesAnothersBase()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        await using ServiceProvider provider = await ArrangeAsync();

        await PublishedAsync(provider, Writing());

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

        Assert.Empty((await Base(provider)
            .PublishedAsync(new KnowledgeQuery())).Articles.Items);

        Assert.Equal(0, (await Base(provider).SummaryAsync()).Total);
    }
}
