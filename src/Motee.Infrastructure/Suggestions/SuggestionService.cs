using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Suggestions;
using Motee.Application.Tenancy;
using Motee.Domain.Suggestions;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Suggestions;

// The suggestion box.
//
// The one rule worth stating twice: an anonymous suggestion's submitter is
// never put into a DTO, for anybody except its author. Not for HR, not for the
// account owner. A box that can be unmasked by whoever is senior enough is not
// an anonymous box — and the only way to be sure is for the name to have
// nowhere to travel, which is why the DTO has no field for it rather than a
// flag asking the client not to draw it.
internal sealed class SuggestionService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    TimeProvider timeProvider) : ISuggestionService
{
    private const int MineTake = 50;

    private string? _zone;

    public async Task<PagedResult<SuggestionDto>> ListAsync(
        SuggestionQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Suggestion> suggestions = dbContext.Suggestions.AsNoTracking();

        if (query.Status is SuggestionStatus status)
        {
            suggestions = suggestions.Where(row => row.Status == status);
        }

        if (query.UnderReviewOnly == true)
        {
            suggestions = suggestions.Where(row =>
                row.Status == SuggestionStatus.UnderReview
                || row.Status == SuggestionStatus.Accepted);
        }

        if (query.Category is SuggestionCategory category)
        {
            suggestions = suggestions.Where(row => row.Category == category);
        }

        if (query.Priority is SuggestionPriority priority)
        {
            suggestions = suggestions.Where(row => row.Priority == priority);
        }

        if (query.FeaturedOnly == true)
        {
            suggestions = suggestions.Where(row => row.IsFeatured);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            suggestions = suggestions.Where(row =>
                EF.Functions.ILike(row.Title, pattern, "\\")
                || EF.Functions.ILike(row.Description, pattern, "\\"));
        }

        // Most-upvoted and trending are both read off the vote rows, so
        // neither can be ordered or filtered in SQL. Everything above narrows
        // first.
        List<Suggestion> rows = await suggestions.ToListAsync(cancellationToken);

        List<SuggestionDto> described = await DescribeAsync(rows, cancellationToken);

        if (query.TrendingOnly == true)
        {
            described = [.. described.Where(row => row.IsTrending)];
        }

        described =
        [
            .. query.Sort switch
            {
                SuggestionSort.MostUpvoted => described
                    .OrderByDescending(row => row.IsFeatured)
                    .ThenByDescending(row => row.Upvotes)
                    .ThenByDescending(row => row.CreatedAt),

                SuggestionSort.Oldest => described
                    .OrderByDescending(row => row.IsFeatured)
                    .ThenBy(row => row.CreatedAt),

                _ => described
                    .OrderByDescending(row => row.IsFeatured)
                    .ThenByDescending(row => row.CreatedAt),
            },
        ];

        return new PagedResult<SuggestionDto>
        {
            Items = [.. described.Skip(query.Skip).Take(query.PageSize)],
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = described.Count,
        };
    }

    public async Task<SuggestionDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Suggestion? suggestion = await dbContext.Suggestions
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        return suggestion is null
            ? null
            : (await DescribeAsync([suggestion], cancellationToken))[0];
    }

    public async Task<SuggestionTally> SummaryAsync(CancellationToken cancellationToken = default)
    {
        List<Counted> rows = await dbContext.Suggestions
            .AsNoTracking()
            .Select(suggestion => new Counted(
                suggestion.Status,
                dbContext.SuggestionVotes.Count(vote => vote.SuggestionId == suggestion.Id)))
            .ToListAsync(cancellationToken);

        return SuggestionBoard.Of(
            [
                .. rows.Select(row => new SuggestionFacts
                {
                    Status = row.Status,
                    Upvotes = row.Upvotes,
                }),
            ]);
    }

    public async Task<IReadOnlyList<SuggestionDto>> MineAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return [];
        }

        List<Suggestion> rows = await dbContext.Suggestions
            .AsNoTracking()
            .Where(suggestion => suggestion.SubmitterEmployeeId == me)
            .OrderByDescending(suggestion => suggestion.CreatedAt)
            .Take(MineTake)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(rows, cancellationToken);
    }

    public async Task<SuggestionResult> SubmitAsync(
        SuggestionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return SuggestionResult.Failed(SuggestionOutcome.NoEmployeeRecord);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        Suggestion suggestion = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Title = request.Title?.Trim() ?? string.Empty,
            Description = request.Description?.Trim() ?? string.Empty,
            Category = request.Category,
            Status = SuggestionStatus.Submitted,
            Priority = SuggestionPriority.Medium,

            // Always recorded. Whether anybody is ever told is the next line.
            SubmitterEmployeeId = me,
            IsAnonymous = request.IsAnonymous,
            CreatedAt = now,
            UpdatedAt = now,
        };

        SuggestionRefusal refusal = SuggestionRules.Refusal(suggestion);

        if (refusal != SuggestionRefusal.None)
        {
            return SuggestionResult.Refused(refusal);
        }

        dbContext.Suggestions.Add(suggestion);

        await dbContext.SaveChangesAsync(cancellationToken);

        return SuggestionResult.Ok((await DescribeAsync([suggestion], cancellationToken))[0]);
    }

    public async Task<SuggestionResult> SetStatusAsync(
        Guid id,
        SuggestionStatus status,
        CancellationToken cancellationToken = default)
    {
        Suggestion? suggestion = await FindAsync(id, cancellationToken);

        if (suggestion is null)
        {
            return SuggestionResult.Failed(SuggestionOutcome.NotFound);
        }

        suggestion.Status = status;

        // Stamped on the way in and cleared on the way out, so the date and
        // the status cannot drift apart.
        suggestion.ResolvedOn = SuggestionRules.ResolvedOn(
            status, suggestion.ResolvedOn, await TodayAsync(cancellationToken));

        suggestion.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return SuggestionResult.Ok((await DescribeAsync([suggestion], cancellationToken))[0]);
    }

    public async Task<SuggestionResult> SetPriorityAsync(
        Guid id,
        SuggestionPriority priority,
        CancellationToken cancellationToken = default)
    {
        Suggestion? suggestion = await FindAsync(id, cancellationToken);

        if (suggestion is null)
        {
            return SuggestionResult.Failed(SuggestionOutcome.NotFound);
        }

        suggestion.Priority = priority;
        suggestion.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return SuggestionResult.Ok((await DescribeAsync([suggestion], cancellationToken))[0]);
    }

    public async Task<SuggestionResult> RespondAsync(
        Guid id,
        SuggestionResponseRequest request,
        CancellationToken cancellationToken = default)
    {
        Suggestion? suggestion = await FindAsync(id, cancellationToken);

        if (suggestion is null)
        {
            return SuggestionResult.Failed(SuggestionOutcome.NotFound);
        }

        suggestion.AdminResponse = Blank(request.AdminResponse);
        suggestion.ImplementationNotes = Blank(request.ImplementationNotes);

        SuggestionRefusal refusal = SuggestionRules.Refusal(suggestion);

        if (refusal != SuggestionRefusal.None)
        {
            return SuggestionResult.Refused(refusal);
        }

        suggestion.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return SuggestionResult.Ok((await DescribeAsync([suggestion], cancellationToken))[0]);
    }

    public async Task<SuggestionResult> SetFeaturedAsync(
        Guid id,
        bool featured,
        CancellationToken cancellationToken = default)
    {
        Suggestion? suggestion = await FindAsync(id, cancellationToken);

        if (suggestion is null)
        {
            return SuggestionResult.Failed(SuggestionOutcome.NotFound);
        }

        suggestion.IsFeatured = featured;
        suggestion.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return SuggestionResult.Ok((await DescribeAsync([suggestion], cancellationToken))[0]);
    }

    public async Task<SuggestionOutcome> RemoveAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Suggestion? suggestion = await FindAsync(id, cancellationToken);

        if (suggestion is null)
        {
            return SuggestionOutcome.NotFound;
        }

        // The comments and votes go with it, by the cascade already declared on both.
        // Left behind they would be rows pointing at nothing, and the votes would keep
        // counting towards the board average for a suggestion that is gone.
        dbContext.Suggestions.Remove(suggestion);

        await dbContext.SaveChangesAsync(cancellationToken);

        return SuggestionOutcome.Succeeded;
    }

    public async Task<SuggestionResult> ToggleUpvoteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return SuggestionResult.Failed(SuggestionOutcome.NoEmployeeRecord);
        }

        Suggestion? suggestion = await FindAsync(id, cancellationToken);

        if (suggestion is null)
        {
            return SuggestionResult.Failed(SuggestionOutcome.NotFound);
        }

        SuggestionVote? existing = await dbContext.SuggestionVotes
            .FirstOrDefaultAsync(
                vote => vote.SuggestionId == id && vote.EmployeeId == me, cancellationToken);

        if (existing is not null)
        {
            dbContext.SuggestionVotes.Remove(existing);
        }
        else
        {
            dbContext.SuggestionVotes.Add(new SuggestionVote
            {
                Id = Guid.NewGuid(),
                TenantId = suggestion.TenantId,
                SuggestionId = id,
                EmployeeId = me,

                // Dated, because trending is about the last fortnight rather
                // than the lifetime total.
                VotedOn = await TodayAsync(cancellationToken),
                CreatedAt = timeProvider.GetUtcNow(),
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return SuggestionResult.Ok((await DescribeAsync([suggestion], cancellationToken))[0]);
    }

    public async Task<SuggestionResult> CommentAsync(
        Guid id,
        string message,
        bool official,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return SuggestionResult.Failed(SuggestionOutcome.NoEmployeeRecord);
        }

        Suggestion? suggestion = await FindAsync(id, cancellationToken);

        if (suggestion is null)
        {
            return SuggestionResult.Failed(SuggestionOutcome.NotFound);
        }

        SuggestionRefusal refusal = SuggestionRules.CommentRefusal(message);

        if (refusal != SuggestionRefusal.None)
        {
            return SuggestionResult.Refused(refusal);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        dbContext.SuggestionComments.Add(new SuggestionComment
        {
            Id = Guid.NewGuid(),
            TenantId = suggestion.TenantId,
            SuggestionId = id,
            AuthorEmployeeId = me,
            Message = message.Trim(),

            // Frozen onto the row rather than read back from the author's
            // current permissions: leaving HR should not un-badge the answers
            // somebody gave while they were there.
            IsOfficial = official,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return SuggestionResult.Ok((await DescribeAsync([suggestion], cancellationToken))[0]);
    }

    public async Task<SuggestionResult> RemoveCommentAsync(
        Guid id,
        Guid commentId,
        bool canRemoveAnybodys,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return SuggestionResult.Failed(SuggestionOutcome.NoEmployeeRecord);
        }

        Suggestion? suggestion = await FindAsync(id, cancellationToken);

        if (suggestion is null)
        {
            return SuggestionResult.Failed(SuggestionOutcome.NotFound);
        }

        SuggestionComment? comment = await dbContext.SuggestionComments
            .FirstOrDefaultAsync(
                row => row.Id == commentId && row.SuggestionId == id, cancellationToken);

        if (comment is null)
        {
            return SuggestionResult.Failed(SuggestionOutcome.NotFound);
        }

        if (comment.AuthorEmployeeId != me && !canRemoveAnybodys)
        {
            return SuggestionResult.Failed(SuggestionOutcome.Forbidden);
        }

        dbContext.SuggestionComments.Remove(comment);

        await dbContext.SaveChangesAsync(cancellationToken);

        return SuggestionResult.Ok((await DescribeAsync([suggestion], cancellationToken))[0]);
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private Task<Suggestion?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Suggestions.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

    private async Task<List<SuggestionDto>> DescribeAsync(
        IReadOnlyList<Suggestion> suggestions,
        CancellationToken cancellationToken)
    {
        if (suggestions.Count == 0)
        {
            return [];
        }

        Guid me = await currentEmployee.IdAsync(cancellationToken) ?? Guid.Empty;

        List<Guid> ids = [.. suggestions.Select(suggestion => suggestion.Id)];

        List<SuggestionVote> votes = await dbContext.SuggestionVotes
            .AsNoTracking()
            .Where(vote => ids.Contains(vote.SuggestionId))
            .ToListAsync(cancellationToken);

        List<SuggestionComment> comments = await dbContext.SuggestionComments
            .AsNoTracking()
            .Where(comment => ids.Contains(comment.SuggestionId))
            .OrderBy(comment => comment.CreatedAt)
            .ToListAsync(cancellationToken);

        // Only the people who may actually be named: comment authors always,
        // and submitters only where the suggestion is not anonymous or the
        // caller wrote it. An anonymous submitter's id is never even looked up.
        List<Guid> nameable =
        [
            .. comments.Select(comment => comment.AuthorEmployeeId)
                .Concat(suggestions
                    .Where(suggestion => SuggestionRules.NamesSubmitter(suggestion, me))
                    .Select(suggestion => suggestion.SubmitterEmployeeId))
                .Distinct(),
        ];

        Dictionary<Guid, Person> people = await PeopleAsync(nameable, cancellationToken);

        DateOnly today = await TodayAsync(cancellationToken);

        return
        [
            .. suggestions.Select(suggestion =>
            {
                List<SuggestionVote> own =
                    [.. votes.Where(vote => vote.SuggestionId == suggestion.Id)];

                bool named = SuggestionRules.NamesSubmitter(suggestion, me);

                Person? submitter = named
                    ? people.GetValueOrDefault(suggestion.SubmitterEmployeeId)
                    : null;

                return new SuggestionDto
                {
                    Id = suggestion.Id,
                    Title = suggestion.Title,
                    Description = suggestion.Description,
                    Category = suggestion.Category,
                    Status = suggestion.Status,
                    Priority = suggestion.Priority,
                    IsAnonymous = suggestion.IsAnonymous,

                    // Null all the way down when it is not ours to disclose.
                    SubmitterEmployeeId = named ? suggestion.SubmitterEmployeeId : null,
                    SubmitterName = submitter?.Name,
                    SubmitterDepartmentName = submitter?.DepartmentName,
                    IsMine = suggestion.SubmitterEmployeeId == me && me != Guid.Empty,
                    AdminResponse = suggestion.AdminResponse,
                    ImplementationNotes = suggestion.ImplementationNotes,
                    IsFeatured = suggestion.IsFeatured,
                    IsTrending = SuggestionRules.IsTrending(
                        [.. own.Select(vote => vote.VotedOn)], today),
                    Upvotes = own.Count,
                    HasMyVote = own.Exists(vote => vote.EmployeeId == me),
                    IsResolved = SuggestionRules.IsResolved(suggestion.Status),
                    ResolvedOn = suggestion.ResolvedOn,
                    Comments =
                    [
                        .. comments
                            .Where(comment => comment.SuggestionId == suggestion.Id)
                            .Select(comment => new SuggestionCommentDto
                            {
                                Id = comment.Id,
                                AuthorEmployeeId = comment.AuthorEmployeeId,
                                AuthorName =
                                    people.GetValueOrDefault(comment.AuthorEmployeeId)?.Name,
                                AuthorDepartmentName = people
                                    .GetValueOrDefault(comment.AuthorEmployeeId)?.DepartmentName,
                                Message = comment.Message,
                                IsOfficial = comment.IsOfficial,
                                CanRemove = comment.AuthorEmployeeId == me,
                                CreatedAt = comment.CreatedAt,
                            }),
                    ],
                    CreatedAt = suggestion.CreatedAt,
                    UpdatedAt = suggestion.UpdatedAt,
                };
            }),
        ];
    }

    private async Task<Dictionary<Guid, Person>> PeopleAsync(
        IReadOnlyList<Guid> employeeIds,
        CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id,
                department => department.Name,
                cancellationToken);

        return await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                employee.DepartmentId,
            })
            .ToDictionaryAsync(
                row => row.Id,
                row => new Person(
                    row.Name,
                    row.DepartmentId is Guid id ? departments.GetValueOrDefault(id) : null),
                cancellationToken);
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

    private sealed record Person(string Name, string? DepartmentName);

    private sealed record Counted(SuggestionStatus Status, int Upvotes);
}
