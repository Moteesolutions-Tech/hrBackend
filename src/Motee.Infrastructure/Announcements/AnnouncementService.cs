using Microsoft.EntityFrameworkCore;
using Motee.Application.Announcements;
using Motee.Application.Common;
using Motee.Domain.Announcements;
using Motee.Domain.Employees;
using Motee.Infrastructure.Persistence;

// This file's own namespace shadows the domain's Announcements helper, so the
// rules it holds are aliased rather than written out in full at each call.
using Rules = Motee.Domain.Announcements.Announcements;

namespace Motee.Infrastructure.Announcements;

// The noticeboard.
//
// Two things drive the shape of this file.
//
// Scheduling and expiry are dates, not states. Nothing here flips a status at
// midnight; the feed asks whether the date has passed. So a notice dated for
// Monday appears on Monday with every worker down, and nobody has to explain why
// the all-hands announcement was still hidden at nine o'clock.
//
// The audience is resolved against the reporting line at read time rather than
// expanded into a recipient list at publish. A notice to managers reaches
// somebody promoted the week after it went out, and a department notice follows
// a transfer. The cost is that targeting runs per read, which is why the people
// query is one round trip over a few columns.
internal sealed class AnnouncementService(
    MoteeDbContext dbContext,
    ICurrentEmployee currentEmployee,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IAnnouncementService
{
    // Everything targeting and describing need, which is less than an employee
    // row. The department name rides along because the acknowledgement panel
    // breaks the list down by it, and fetching it later would be a second trip
    // over the same people.
    private sealed record Person(
        Guid Id,
        string Name,
        Guid? DepartmentId,
        string? Department,
        Guid? ManagerId);

    public async Task<PagedResult<AnnouncementDto>> ListAsync(
        AnnouncementQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Announcement> announcements = dbContext.Announcements.AsNoTracking();

        if (!query.IncludeArchived)
        {
            announcements = announcements.Where(
                announcement => announcement.Status != AnnouncementStatus.Archived);
        }

        if (query.Type is AnnouncementType type)
        {
            announcements = announcements.Where(announcement => announcement.Type == type);
        }

        if (query.Audience is AnnouncementAudience audience)
        {
            announcements = announcements.Where(announcement => announcement.Audience == audience);
        }

        int total = await announcements.CountAsync(cancellationToken);

        // Pinned first, then newest. The same order as the feed, so the list HR
        // writes from and the feed staff read are not in two different orders.
        List<Announcement> page = await announcements
            .OrderByDescending(announcement => announcement.IsPinned)
            .ThenByDescending(announcement => announcement.CreatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<AnnouncementDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<AnnouncementDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Announcement? announcement = await dbContext.Announcements
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return announcement is null
            ? null
            : (await DescribeAsync([announcement], cancellationToken))[0];
    }

    public async Task<AnnouncementResult> CreateAsync(
        AnnouncementRequest request,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        if (Validate(request) is AnnouncementOutcome problem)
        {
            return AnnouncementResult.Failed(problem);
        }

        Announcement announcement = new()
        {
            Id = Guid.NewGuid(),
            Title = request.Title.Trim(),
            Body = request.Body.Trim(),
            Type = request.Type,
            Priority = request.Priority,
            Audience = request.Audience,
            TargetDepartmentIds = [.. request.TargetDepartmentIds ?? []],
            IsPinned = request.IsPinned,
            RequiresAcknowledgement = request.RequiresAcknowledgement,
            ScheduledFor = request.ScheduledFor?.ToUniversalTime(),
            ExpiresAt = request.ExpiresAt?.ToUniversalTime(),
            AttachmentFileId = request.AttachmentFileId,

            // A date in the future schedules it; one in the past or none leaves a
            // draft. Dating it is the whole act — there is no separate schedule
            // step, because a date and a button would be two ways to say one
            // thing and they would eventually disagree.
            Status = request.ScheduledFor is not null
                ? AnnouncementStatus.Scheduled
                : AnnouncementStatus.Draft,

            CreatedByUserId = UserId(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.Announcements.Add(announcement);
        await dbContext.SaveChangesAsync(cancellationToken);

        return AnnouncementResult.Ok((await DescribeAsync([announcement], cancellationToken))[0]);
    }

    public async Task<AnnouncementResult> UpdateAsync(
        Guid id,
        AnnouncementRequest request,
        CancellationToken cancellationToken = default)
    {
        Announcement? announcement = await dbContext.Announcements
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (announcement is null)
        {
            return AnnouncementResult.Failed(AnnouncementOutcome.NotFound);
        }

        if (Validate(request) is AnnouncementOutcome problem)
        {
            return AnnouncementResult.Failed(problem);
        }

        // Somebody's agreement was to what it said at the time. Editing the text
        // underneath them would leave a record of consent to words they never
        // read, which is the one thing an acknowledgement is for.
        bool agreed = await dbContext.AnnouncementAcknowledgements
            .AnyAsync(
                acknowledgement => acknowledgement.AnnouncementId == id, cancellationToken);

        if (agreed)
        {
            return AnnouncementResult.Failed(AnnouncementOutcome.AlreadyAcknowledged);
        }

        announcement.Title = request.Title.Trim();
        announcement.Body = request.Body.Trim();
        announcement.Type = request.Type;
        announcement.Priority = request.Priority;
        announcement.Audience = request.Audience;
        announcement.TargetDepartmentIds = [.. request.TargetDepartmentIds ?? []];
        announcement.IsPinned = request.IsPinned;
        announcement.RequiresAcknowledgement = request.RequiresAcknowledgement;
        announcement.ScheduledFor = request.ScheduledFor?.ToUniversalTime();
        announcement.ExpiresAt = request.ExpiresAt?.ToUniversalTime();
        announcement.AttachmentFileId = request.AttachmentFileId;
        announcement.UpdatedAt = timeProvider.GetUtcNow();

        // Re-dating something not yet out moves it back to scheduled or draft.
        // Something already published keeps its status: it has been seen, and
        // pulling it back out of the feed is archiving rather than editing.
        if (announcement.Status is AnnouncementStatus.Draft or AnnouncementStatus.Scheduled)
        {
            announcement.Status = request.ScheduledFor is not null
                ? AnnouncementStatus.Scheduled
                : AnnouncementStatus.Draft;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return AnnouncementResult.Ok((await DescribeAsync([announcement], cancellationToken))[0]);
    }

    public async Task<AnnouncementResult> PublishAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Announcement? announcement = await dbContext.Announcements
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (announcement is null)
        {
            return AnnouncementResult.Failed(AnnouncementOutcome.NotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        announcement.Status = AnnouncementStatus.Published;

        // Recorded once. Publishing something already out does not restamp it,
        // so "posted three weeks ago" survives somebody pressing it again.
        announcement.PublishedAt ??= now;
        announcement.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return AnnouncementResult.Ok((await DescribeAsync([announcement], cancellationToken))[0]);
    }

    public async Task<AnnouncementResult> ArchiveAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Announcement? announcement = await dbContext.Announcements
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (announcement is null)
        {
            return AnnouncementResult.Failed(AnnouncementOutcome.NotFound);
        }

        announcement.Status = AnnouncementStatus.Archived;
        announcement.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return AnnouncementResult.Ok((await DescribeAsync([announcement], cancellationToken))[0]);
    }

    public async Task<AnnouncementOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Announcement? announcement = await dbContext.Announcements
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (announcement is null)
        {
            return AnnouncementOutcome.NotFound;
        }

        dbContext.Announcements.Remove(announcement);
        await dbContext.SaveChangesAsync(cancellationToken);

        return AnnouncementOutcome.Succeeded;
    }

    public async Task<AcknowledgementSummaryDto?> AcknowledgementsAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Announcement? announcement = await dbContext.Announcements
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (announcement is null)
        {
            return null;
        }

        IReadOnlyList<Person> targeted = Targeted(
            announcement, await PeopleAsync(cancellationToken));

        List<AnnouncementAcknowledgement> agreed = await dbContext.AnnouncementAcknowledgements
            .AsNoTracking()
            .Where(acknowledgement => acknowledgement.AnnouncementId == id)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, DateTimeOffset> when = agreed
            .ToDictionary(
                acknowledgement => acknowledgement.EmployeeId,
                acknowledgement => acknowledgement.AcknowledgedAt);

        return new AcknowledgementSummaryDto
        {
            AnnouncementId = id,
            TotalTargeted = targeted.Count,
            Acknowledged =
            [
                .. targeted
                    .Where(person => when.ContainsKey(person.Id))
                    .Select(person => new AcknowledgedPersonDto
                    {
                        EmployeeId = person.Id,
                        EmployeeName = person.Name,
                        Department = person.Department,
                        AcknowledgedAt = when[person.Id],
                    })
                    .OrderBy(person => person.AcknowledgedAt),
            ],

            // Named rather than counted. "Fourteen outstanding" is a number
            // nobody can act on; a list is something somebody can chase.
            Outstanding =
            [
                .. targeted
                    .Where(person => !when.ContainsKey(person.Id))
                    .Select(person => person.Name)
                    .Order(StringComparer.Ordinal),
            ],
        };
    }

    public async Task<IReadOnlyList<AnnouncementDto>> FeedAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return [];
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Archived is excluded in SQL because it is a stored value. Live is not:
        // it depends on a date comparison the query translator would have to run
        // against scheduled_for and expires_at, and doing it in memory keeps one
        // definition of "live" — the domain's — rather than two that drift.
        List<Announcement> candidates = await dbContext.Announcements
            .AsNoTracking()
            .Where(announcement => announcement.Status != AnnouncementStatus.Archived)
            .ToListAsync(cancellationToken);

        List<Announcement> live = [.. candidates.Where(
            announcement => Rules.IsLive(announcement, now))];

        if (live.Count == 0)
        {
            return [];
        }

        IReadOnlyList<Person> people = await PeopleAsync(cancellationToken);

        if (people.FirstOrDefault(person => person.Id == employeeId) is not Person me)
        {
            return [];
        }

        List<Announcement> mine = [.. live
            .Where(announcement => Reaches(announcement, me, people))

            // Pinned first whatever its date — that is what pinning is for —
            // then newest. Priority is deliberately not in the sort: an urgent
            // notice from last month should not outrank this morning's.
            .OrderByDescending(announcement => announcement.IsPinned)
            .ThenByDescending(announcement => announcement.CreatedAt)];

        return await DescribeAsync(mine, cancellationToken, employeeId, people);
    }

    public async Task<AnnouncementResult> MarkViewedAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (await VisibleToMeAsync(id, cancellationToken) is not (
            Announcement announcement, Guid employeeId))
        {
            return AnnouncementResult.Failed(AnnouncementOutcome.NotAvailable);
        }

        bool seen = await dbContext.AnnouncementViews
            .AnyAsync(
                view => view.AnnouncementId == id && view.EmployeeId == employeeId,
                cancellationToken);

        // Idempotent, so the count means unique readers and a refresh does not
        // inflate it. Silently fine rather than an error: opening something
        // twice is not a mistake anybody needs telling about.
        if (!seen)
        {
            dbContext.AnnouncementViews.Add(new AnnouncementView
            {
                Id = Guid.NewGuid(),
                AnnouncementId = id,
                EmployeeId = employeeId,
                ViewedAt = timeProvider.GetUtcNow(),
            });

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return AnnouncementResult.Ok((await DescribeAsync([announcement], cancellationToken))[0]);
    }

    public async Task<AnnouncementResult> AcknowledgeAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (await VisibleToMeAsync(id, cancellationToken) is not (
            Announcement announcement, Guid employeeId))
        {
            return AnnouncementResult.Failed(AnnouncementOutcome.NotAvailable);
        }

        if (!announcement.RequiresAcknowledgement)
        {
            return AnnouncementResult.Failed(AnnouncementOutcome.AcknowledgementNotRequired);
        }

        bool already = await dbContext.AnnouncementAcknowledgements
            .AnyAsync(
                acknowledgement => acknowledgement.AnnouncementId == id
                    && acknowledgement.EmployeeId == employeeId,
                cancellationToken);

        // Refused rather than ignored, unlike a view. Agreeing is a deliberate
        // act with a timestamp somebody may later rely on, so a second one
        // probably means the first did not register and is worth saying.
        if (already)
        {
            return AnnouncementResult.Failed(AnnouncementOutcome.AlreadyAcknowledgedByYou);
        }

        dbContext.AnnouncementAcknowledgements.Add(new AnnouncementAcknowledgement
        {
            Id = Guid.NewGuid(),
            AnnouncementId = id,
            EmployeeId = employeeId,
            AcknowledgedAt = timeProvider.GetUtcNow(),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return AnnouncementResult.Ok(
            (await DescribeAsync([announcement], cancellationToken, employeeId))[0]);
    }

    // ── targeting ───────────────────────────────────────────────────────────

    // Whether a notice reaches somebody. Worked out from where they sit and who
    // reports to them rather than from a stored recipient list, so it stays true
    // as people move.
    private static bool Reaches(
        Announcement announcement,
        Person person,
        IReadOnlyList<Person> everybody) =>
        announcement.Audience switch
        {
            AnnouncementAudience.AllStaff => true,

            AnnouncementAudience.Department =>
                person.DepartmentId is Guid departmentId
                    && announcement.TargetDepartmentIds.Contains(departmentId),

            AnnouncementAudience.Managers =>
                everybody.Any(other => other.ManagerId == person.Id),

            AnnouncementAudience.Leadership => person.ManagerId is null,

            _ => false,
        };

    private static IReadOnlyList<Person> Targeted(
        Announcement announcement,
        IReadOnlyList<Person> everybody) =>
        [.. everybody.Where(person => Reaches(announcement, person, everybody))];

    // Everyone who could be reached, in one trip. Active only: somebody who has
    // left is not outstanding on an acknowledgement, and counting them would
    // leave a chase list nobody can ever clear.
    private async Task<IReadOnlyList<Person>> PeopleAsync(CancellationToken cancellationToken)
    {
        Dictionary<Guid, string> departments = await DepartmentsAsync(cancellationToken);

        List<Person> people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Status == EmployeeStatus.Active)
            .Select(employee => new Person(
                employee.Id,
                employee.FirstName + " " + employee.LastName,
                employee.DepartmentId,
                null,
                employee.ManagerId))
            .ToListAsync(cancellationToken);

        // Named in memory rather than joined, because the names are already in
        // hand for the targeting lists and a join would fetch each one again
        // per employee.
        return
        [
            .. people.Select(person => person with
            {
                Department = person.DepartmentId is Guid departmentId
                    ? departments.GetValueOrDefault(departmentId)
                    : null,
            }),
        ];
    }

    // Cached for the life of the call. This is a scoped service over a small
    // table, and department names cannot change underneath a single request.
    private Dictionary<Guid, string>? _departments;

    private async Task<Dictionary<Guid, string>> DepartmentsAsync(
        CancellationToken cancellationToken) =>
        _departments ??= await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id,
                department => department.Name,
                cancellationToken);

    // The announcement and who is asking, but only if it is actually theirs to
    // see. Returns nothing for a draft, something expired, or something
    // addressed elsewhere — all of which read the same from outside, because
    // the alternative tells people what notices exist that they cannot read.
    private async Task<(Announcement Announcement, Guid EmployeeId)?> VisibleToMeAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid employeeId)
        {
            return null;
        }

        Announcement? announcement = await dbContext.Announcements
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (announcement is null || !Rules.IsLive(announcement, timeProvider.GetUtcNow()))
        {
            return null;
        }

        IReadOnlyList<Person> people = await PeopleAsync(cancellationToken);

        return people.FirstOrDefault(person => person.Id == employeeId) is Person me
            && Reaches(announcement, me, people)
                ? (announcement, employeeId)
                : null;
    }

    // ── describing ──────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<AnnouncementDto>> DescribeAsync(
        IReadOnlyList<Announcement> announcements,
        CancellationToken cancellationToken,
        Guid? forEmployeeId = null,
        IReadOnlyList<Person>? people = null)
    {
        if (announcements.Count == 0)
        {
            return [];
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        List<Guid> ids = [.. announcements.Select(announcement => announcement.Id)];

        // Counted rather than loaded: the rows themselves are only wanted for the
        // acknowledgement summary, and a busy notice has one per employee.
        Dictionary<Guid, int> views = await dbContext.AnnouncementViews
            .AsNoTracking()
            .Where(view => ids.Contains(view.AnnouncementId))
            .GroupBy(view => view.AnnouncementId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, cancellationToken);

        List<AnnouncementAcknowledgement> acknowledgements =
            await dbContext.AnnouncementAcknowledgements
                .AsNoTracking()
                .Where(acknowledgement => ids.Contains(acknowledgement.AnnouncementId))
                .ToListAsync(cancellationToken);

        IReadOnlyList<Person> everybody = people ?? await PeopleAsync(cancellationToken);

        Dictionary<Guid, Person> byId = everybody.ToDictionary(person => person.Id);

        // Named here rather than left as ids. The rows are already loaded for
        // the count, so the panel's department breakdown costs nothing further.
        Dictionary<Guid, List<AcknowledgedPersonDto>> agreed = acknowledgements
            .GroupBy(acknowledgement => acknowledgement.AnnouncementId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(acknowledgement => acknowledgement.AcknowledgedAt)
                    .Select(acknowledgement => new AcknowledgedPersonDto
                    {
                        EmployeeId = acknowledgement.EmployeeId,
                        EmployeeName = byId.TryGetValue(
                            acknowledgement.EmployeeId, out Person? person)
                            ? person.Name
                            : "Former employee",
                        Department = byId.GetValueOrDefault(acknowledgement.EmployeeId)?.Department,
                        AcknowledgedAt = acknowledgement.AcknowledgedAt,
                    })
                    .ToList());

        Dictionary<Guid, string> authors = await AuthorsAsync(announcements, cancellationToken);

        Dictionary<Guid, string> departments = await DepartmentsAsync(cancellationToken);

        Dictionary<Guid, string> attachments = await AttachmentsAsync(
            announcements, cancellationToken);

        return
        [
            .. announcements.Select(announcement => new AnnouncementDto
            {
                Id = announcement.Id,
                Title = announcement.Title,
                Body = announcement.Body,
                Type = announcement.Type,
                Status = Rules.EffectiveStatus(announcement, now),
                Priority = announcement.Priority,
                Audience = announcement.Audience,
                TargetDepartmentIds = [.. announcement.TargetDepartmentIds],
                TargetDepartments =
                [
                    .. announcement.TargetDepartmentIds
                        .Select(departments.GetValueOrDefault)
                        .OfType<string>(),
                ],
                IsPinned = announcement.IsPinned,
                RequiresAcknowledgement = announcement.RequiresAcknowledgement,
                ScheduledFor = announcement.ScheduledFor,
                ExpiresAt = announcement.ExpiresAt,
                AttachmentFileId = announcement.AttachmentFileId,
                AttachmentName = announcement.AttachmentFileId is Guid attachment
                    ? attachments.GetValueOrDefault(attachment)
                    : null,
                CreatedByName = announcement.CreatedByUserId is Guid author
                    ? authors.GetValueOrDefault(author)
                    : null,
                CreatedAt = announcement.CreatedAt,
                PublishedAt = announcement.PublishedAt,
                ViewCount = views.GetValueOrDefault(announcement.Id),
                AcknowledgedCount = agreed.GetValueOrDefault(announcement.Id)?.Count ?? 0,
                Acknowledgements = agreed.GetValueOrDefault(announcement.Id) ?? [],
                TotalTargeted = Targeted(announcement, everybody).Count,

                // Null when none is asked for, which reads differently from
                // "not yet" — the screen shows a tick for one and nothing for
                // the other.
                AcknowledgedByMe = announcement.RequiresAcknowledgement
                    && forEmployeeId is Guid me
                        ? acknowledgements.Any(acknowledgement =>
                            acknowledgement.AnnouncementId == announcement.Id
                                && acknowledgement.EmployeeId == me)
                        : null,
            }),
        ];
    }

    // The attached file's name. An id is not something a screen can show, and
    // the alternative — storing the name on the announcement too — would go
    // stale the moment the upload is replaced.
    private async Task<Dictionary<Guid, string>> AttachmentsAsync(
        IReadOnlyList<Announcement> announcements,
        CancellationToken cancellationToken)
    {
        List<Guid> ids = [.. announcements
            .Select(announcement => announcement.AttachmentFileId)
            .OfType<Guid>()
            .Distinct()];

        return ids.Count == 0
            ? []
            : await dbContext.StoredFiles
                .AsNoTracking()
                .Where(file => ids.Contains(file.Id))
                .ToDictionaryAsync(file => file.Id, file => file.FileName, cancellationToken);
    }

    // Users are not tenant-scoped — login has to find an account before there is
    // a tenant to scope to — so this needs its own tenant clause. Scoped through
    // the announcement rows rather than a tenant id read from elsewhere: the
    // authors are by definition people who wrote these.
    private async Task<Dictionary<Guid, string>> AuthorsAsync(
        IReadOnlyList<Announcement> announcements,
        CancellationToken cancellationToken)
    {
        List<Guid> ids = [.. announcements
            .Select(announcement => announcement.CreatedByUserId)
            .OfType<Guid>()
            .Distinct()];

        if (ids.Count == 0)
        {
            return [];
        }

        Guid tenantId = announcements[0].TenantId;

        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.TenantId == tenantId && ids.Contains(user.Id))
            .ToDictionaryAsync(
                user => user.Id,
                user => (user.FirstName + " " + user.LastName).Trim(),
                cancellationToken);
    }

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private static AnnouncementOutcome? Validate(AnnouncementRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return AnnouncementOutcome.TitleRequired;
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return AnnouncementOutcome.BodyRequired;
        }

        // A department audience naming no departments reaches nobody, which is
        // not what anybody meant by choosing it. Refused rather than treated as
        // all staff: guessing wrong here sends an internal notice to everyone.
        if (request.Audience == AnnouncementAudience.Department
            && (request.TargetDepartmentIds is null || request.TargetDepartmentIds.Count == 0))
        {
            return AnnouncementOutcome.DepartmentsRequired;
        }

        // Expiring before it appears. The notice would never be seen by anybody,
        // and the only sign would be its absence.
        if (request.ScheduledFor is DateTimeOffset appears
            && request.ExpiresAt is DateTimeOffset expires
            && expires <= appears)
        {
            return AnnouncementOutcome.ExpiresBeforeItAppears;
        }

        return null;
    }
}
