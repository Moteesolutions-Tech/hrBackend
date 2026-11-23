using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Knowledge;
using Motee.Application.Tenancy;
using Motee.Domain.Knowledge;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Knowledge;

// The knowledge base.
//
// A draft never leaves this service for a reader. `includeUnpublished` is the
// caller's authoring grant, resolved in the controller and passed down — so a
// missing permission narrows what comes back rather than relying on the client
// to filter a list it was given.
//
// Views and helpfulness are read from their rows. The client keeps a `views`
// counter plus three fields for a two-sided vote, two of which held the same
// number and one of which was always zero.
internal sealed class KnowledgeService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    TimeProvider timeProvider) : IKnowledgeService
{
    private string? _zone;

    public Task<KnowledgeListDto> ListAsync(
        KnowledgeQuery query,
        bool includeUnpublished,
        CancellationToken cancellationToken = default) =>
        PageAsync(query, includeUnpublished, cancellationToken);

    public Task<KnowledgeListDto> PublishedAsync(
        KnowledgeQuery query,
        CancellationToken cancellationToken = default) =>
        PageAsync(query, includeUnpublished: false, cancellationToken);

    public async Task<KnowledgeTally> SummaryAsync(CancellationToken cancellationToken = default)
    {
        List<Counted> rows = await dbContext.KnowledgeArticles
            .AsNoTracking()
            .Select(article => new Counted(
                article.Status,
                dbContext.ArticleReads
                    .Where(read => read.ArticleId == article.Id)
                    .Sum(read => read.Views),
                dbContext.ArticleReads.Count(read => read.ArticleId == article.Id)))
            .ToListAsync(cancellationToken);

        return KnowledgeBase.Of(
            [
                .. rows.Select(row => new ArticleFacts
                {
                    Status = row.Status,
                    Views = row.Views,
                    Readers = row.Readers,
                }),
            ]);
    }

    public async Task<KnowledgeArticleDto?> GetAsync(
        Guid id,
        bool includeUnpublished,
        CancellationToken cancellationToken = default)
    {
        KnowledgeArticle? article = await dbContext.KnowledgeArticles
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (article is null)
        {
            return null;
        }

        // Null rather than a refusal: a reader who may not see a draft has no
        // business learning that the id resolves to one.
        return !includeUnpublished && !KnowledgeArticles.IsReadable(article)
            ? null
            : (await DescribeAsync([article], cancellationToken))[0];
    }

    public async Task<KnowledgeResult> CreateAsync(
        KnowledgeArticleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return KnowledgeResult.Failed(KnowledgeOutcome.NoEmployeeRecord);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        KnowledgeArticle article = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Title = string.Empty,
            Body = string.Empty,
            AuthorEmployeeId = me,
            CreatedAt = now,
        };

        Apply(article, request, now);

        // A request that arrives already published gets its publication date
        // from the same rule as a later publish, rather than a second path.
        article.PublishedOn = KnowledgeArticles.PublishedOn(
            article.Status, was: null, asAt: await TodayAsync(cancellationToken));

        KnowledgeRefusal refusal = KnowledgeArticles.Refusal(article);

        if (refusal != KnowledgeRefusal.None)
        {
            return KnowledgeResult.Refused(refusal);
        }

        dbContext.KnowledgeArticles.Add(article);

        await dbContext.SaveChangesAsync(cancellationToken);

        return KnowledgeResult.Ok((await DescribeAsync([article], cancellationToken))[0]);
    }

    public async Task<KnowledgeResult> UpdateAsync(
        Guid id,
        KnowledgeArticleRequest request,
        CancellationToken cancellationToken = default)
    {
        KnowledgeArticle? article = await FindAsync(id, cancellationToken);

        if (article is null)
        {
            return KnowledgeResult.Failed(KnowledgeOutcome.NotFound);
        }

        Apply(article, request, timeProvider.GetUtcNow());

        article.PublishedOn = KnowledgeArticles.PublishedOn(
            article.Status, article.PublishedOn, await TodayAsync(cancellationToken));

        KnowledgeRefusal refusal = KnowledgeArticles.Refusal(article);

        if (refusal != KnowledgeRefusal.None)
        {
            return KnowledgeResult.Refused(refusal);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return KnowledgeResult.Ok((await DescribeAsync([article], cancellationToken))[0]);
    }

    public async Task<KnowledgeResult> SetStatusAsync(
        Guid id,
        ArticleStatus status,
        CancellationToken cancellationToken = default)
    {
        KnowledgeArticle? article = await FindAsync(id, cancellationToken);

        if (article is null)
        {
            return KnowledgeResult.Failed(KnowledgeOutcome.NotFound);
        }

        article.Status = status;

        article.PublishedOn = KnowledgeArticles.PublishedOn(
            status, article.PublishedOn, await TodayAsync(cancellationToken));

        article.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return KnowledgeResult.Ok((await DescribeAsync([article], cancellationToken))[0]);
    }

    public async Task<KnowledgeResult> SetFeaturedAsync(
        Guid id,
        bool featured,
        CancellationToken cancellationToken = default)
    {
        KnowledgeArticle? article = await FindAsync(id, cancellationToken);

        if (article is null)
        {
            return KnowledgeResult.Failed(KnowledgeOutcome.NotFound);
        }

        article.IsFeatured = featured;
        article.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return KnowledgeResult.Ok((await DescribeAsync([article], cancellationToken))[0]);
    }

    public async Task<KnowledgeResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        KnowledgeArticle? article = await FindAsync(id, cancellationToken);

        if (article is null)
        {
            return KnowledgeResult.Failed(KnowledgeOutcome.NotFound);
        }

        dbContext.KnowledgeArticles.Remove(article);

        await dbContext.SaveChangesAsync(cancellationToken);

        return KnowledgeResult.Done();
    }

    public async Task<KnowledgeResult> RecordReadAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        (KnowledgeArticle? article, Guid me, KnowledgeOutcome outcome) =
            await ReadableOrRefusedAsync(id, cancellationToken);

        if (article is null)
        {
            return KnowledgeResult.Failed(outcome);
        }

        ArticleRead? existing = await dbContext.ArticleReads
            .FirstOrDefaultAsync(
                read => read.ArticleId == id && read.EmployeeId == me, cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        // One row per reader, counting their own opens. A row per view would
        // grow without bound on a popular article and still not answer how
        // many people it reached.
        if (existing is not null)
        {
            existing.Views++;
            existing.LastReadAt = now;
        }
        else
        {
            dbContext.ArticleReads.Add(new ArticleRead
            {
                Id = Guid.NewGuid(),
                TenantId = article.TenantId,
                ArticleId = id,
                EmployeeId = me,
                Views = 1,
                FirstReadAt = now,
                LastReadAt = now,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return KnowledgeResult.Ok((await DescribeAsync([article], cancellationToken))[0]);
    }

    public async Task<KnowledgeResult> VoteAsync(
        Guid id,
        ArticleVote vote,
        CancellationToken cancellationToken = default)
    {
        (KnowledgeArticle? article, Guid me, KnowledgeOutcome outcome) =
            await ReadableOrRefusedAsync(id, cancellationToken);

        if (article is null)
        {
            return KnowledgeResult.Failed(outcome);
        }

        ArticleFeedback? existing = await dbContext.ArticleFeedback
            .FirstOrDefaultAsync(
                row => row.ArticleId == id && row.EmployeeId == me, cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Changed rather than added, so switching from helpful to not does not
        // count as two votes.
        if (existing is not null)
        {
            existing.Vote = vote;
            existing.UpdatedAt = now;
        }
        else
        {
            dbContext.ArticleFeedback.Add(new ArticleFeedback
            {
                Id = Guid.NewGuid(),
                TenantId = article.TenantId,
                ArticleId = id,
                EmployeeId = me,
                Vote = vote,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return KnowledgeResult.Ok((await DescribeAsync([article], cancellationToken))[0]);
    }

    public async Task<KnowledgeResult> ClearVoteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        (KnowledgeArticle? article, Guid me, KnowledgeOutcome outcome) =
            await ReadableOrRefusedAsync(id, cancellationToken);

        if (article is null)
        {
            return KnowledgeResult.Failed(outcome);
        }

        ArticleFeedback? existing = await dbContext.ArticleFeedback
            .FirstOrDefaultAsync(
                row => row.ArticleId == id && row.EmployeeId == me, cancellationToken);

        if (existing is not null)
        {
            dbContext.ArticleFeedback.Remove(existing);

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return KnowledgeResult.Ok((await DescribeAsync([article], cancellationToken))[0]);
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private Task<KnowledgeArticle?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.KnowledgeArticles.FirstOrDefaultAsync(
            row => row.Id == id, cancellationToken);

    private async Task<(KnowledgeArticle? Article, Guid Me, KnowledgeOutcome Outcome)>
        ReadableOrRefusedAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return (null, Guid.Empty, KnowledgeOutcome.NoEmployeeRecord);
        }

        KnowledgeArticle? article = await FindAsync(id, cancellationToken);

        if (article is null)
        {
            return (null, me, KnowledgeOutcome.NotFound);
        }

        // Reading or voting on something that is not published. Recording a
        // view against a draft would also let an author inflate their own
        // figures before anybody could see it.
        return KnowledgeArticles.IsReadable(article)
            ? (article, me, KnowledgeOutcome.Succeeded)
            : (null, me, KnowledgeOutcome.Refused);
    }

    private async Task<KnowledgeListDto> PageAsync(
        KnowledgeQuery query,
        bool includeUnpublished,
        CancellationToken cancellationToken)
    {
        IQueryable<KnowledgeArticle> articles = dbContext.KnowledgeArticles.AsNoTracking();

        if (!includeUnpublished)
        {
            articles = articles.Where(article => article.Status == ArticleStatus.Published);
        }
        else if (query.Status is ArticleStatus status)
        {
            articles = articles.Where(article => article.Status == status);
        }

        if (query.FeaturedOnly == true)
        {
            articles = articles.Where(article => article.IsFeatured);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            articles = articles.Where(article =>
                EF.Functions.ILike(article.Title, pattern, "\\")
                || EF.Functions.ILike(article.Body, pattern, "\\")
                || (article.Summary != null
                    && EF.Functions.ILike(article.Summary, pattern, "\\")));
        }

        // Tags live in jsonb, which Postgres will not let a query filter on,
        // so the tag filter and the category counts are resolved in memory
        // after everything else has narrowed the set.
        List<KnowledgeArticle> rows = await articles.ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            // Folded the same way the stored tags were, so "Payroll" finds
            // "payroll".
            string tag = KnowledgeArticles.NormaliseTags([query.Tag]).FirstOrDefault()
                ?? string.Empty;

            rows = [.. rows.Where(article => article.Tags.Contains(tag, StringComparer.Ordinal))];
        }

        // Before the category filter, so picking a category does not empty
        // the tiles that offered the choice.
        List<ArticleCategoryCount> categoryCounts =
        [
            .. rows
                .GroupBy(article => article.Category)
                .OrderBy(group => group.Key)
                .Select(group => new ArticleCategoryCount
                {
                    Category = group.Key,
                    Count = group.Count(),
                }),
        ];

        if (query.Category is ArticleCategory category)
        {
            rows = [.. rows.Where(article => article.Category == category)];
        }

        List<KnowledgeArticle> page =
        [
            .. rows
                .OrderByDescending(article => article.IsFeatured)
                .ThenByDescending(article => article.PublishedOn ?? DateOnly.MinValue)
                .ThenByDescending(article => article.UpdatedAt)
                .Skip(query.Skip)
                .Take(query.PageSize),
        ];

        return new KnowledgeListDto
        {
            Articles = new PagedResult<KnowledgeArticleDto>
            {
                Items = await DescribeAsync(page, cancellationToken),
                Page = query.Page,
                PageSize = query.PageSize,
                TotalItems = rows.Count,
            },
            CategoryCounts = categoryCounts,
        };
    }

    private async Task<List<KnowledgeArticleDto>> DescribeAsync(
        IReadOnlyList<KnowledgeArticle> articles,
        CancellationToken cancellationToken)
    {
        if (articles.Count == 0)
        {
            return [];
        }

        Guid me = await currentEmployee.IdAsync(cancellationToken) ?? Guid.Empty;

        List<Guid> ids = [.. articles.Select(article => article.Id)];

        List<ArticleRead> reads = await dbContext.ArticleReads
            .AsNoTracking()
            .Where(read => ids.Contains(read.ArticleId))
            .ToListAsync(cancellationToken);

        List<ArticleFeedback> feedback = await dbContext.ArticleFeedback
            .AsNoTracking()
            .Where(row => ids.Contains(row.ArticleId))
            .ToListAsync(cancellationToken);

        List<Guid> authorIds = [.. articles.Select(article => article.AuthorEmployeeId).Distinct()];

        Dictionary<Guid, string> authors = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => authorIds.Contains(employee.Id))
            .ToDictionaryAsync(
                employee => employee.Id,
                employee => employee.FirstName + " " + employee.LastName,
                cancellationToken);

        return
        [
            .. articles.Select(article =>
            {
                List<ArticleRead> own = [.. reads.Where(read => read.ArticleId == article.Id)];

                int helpful = feedback.Count(row =>
                    row.ArticleId == article.Id && row.Vote == ArticleVote.Helpful);

                int notHelpful = feedback.Count(row =>
                    row.ArticleId == article.Id && row.Vote == ArticleVote.NotHelpful);

                ArticleFeedback? mine = feedback.Find(row =>
                    row.ArticleId == article.Id && row.EmployeeId == me);

                return new KnowledgeArticleDto
                {
                    Id = article.Id,
                    Title = article.Title,
                    Summary = article.Summary,
                    Body = article.Body,
                    Category = article.Category,
                    Status = article.Status,
                    AuthorEmployeeId = article.AuthorEmployeeId,
                    AuthorName = authors.GetValueOrDefault(article.AuthorEmployeeId),
                    Tags = article.Tags,
                    IsFeatured = article.IsFeatured,
                    PublishedOn = article.PublishedOn,
                    Views = own.Sum(read => read.Views),
                    Readers = own.Count,
                    HaveIRead = own.Exists(read => read.EmployeeId == me),
                    HelpfulVotes = helpful,
                    NotHelpfulVotes = notHelpful,
                    HelpfulPercent = KnowledgeArticles.HelpfulPercent(helpful, notHelpful),
                    MyVote = mine?.Vote,
                    CreatedAt = article.CreatedAt,
                    UpdatedAt = article.UpdatedAt,
                };
            }),
        ];
    }

    private static void Apply(
        KnowledgeArticle article,
        KnowledgeArticleRequest request,
        DateTimeOffset now)
    {
        article.Title = request.Title?.Trim() ?? string.Empty;
        article.Summary = Blank(request.Summary);
        article.Body = request.Body?.Trim() ?? string.Empty;
        article.Category = request.Category;
        article.Status = request.Status;
        article.IsFeatured = request.IsFeatured;

        // Replaced rather than mutated: the jsonb converter compares by
        // serialising, so an in-place edit is not seen as a change.
        article.Tags = [.. KnowledgeArticles.NormaliseTags(request.Tags ?? [])];
        article.UpdatedAt = now;
    }

    private static string Escaped(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        TenantClock.DayOf(timeProvider.GetUtcNow(), await ZoneAsync(cancellationToken));

    private async Task<string?> ZoneAsync(CancellationToken cancellationToken)
    {
        if (_zone is not null)
        {
            return _zone;
        }

        if (currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        _zone = settings?.TimeZoneId;

        return _zone;
    }

    private sealed record Counted(ArticleStatus Status, int Views, int Readers);
}
