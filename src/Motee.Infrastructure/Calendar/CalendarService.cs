using Microsoft.EntityFrameworkCore;
using Motee.Application.Calendar;
using Motee.Application.Common;
using Motee.Application.Tenancy;
using Motee.Domain.Calendar;
using Motee.Domain.Common;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Domain.Performance;
using Motee.Domain.Recruitment;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Calendar;

// This file's own namespace shadows the domain's helper, so the rules it holds
// are aliased rather than written out in full at each call.
using Rules = Motee.Domain.Calendar.CalendarEvents;

// Nine sources, one window.
//
// Only the first of them is a table. The rest are read from the records that
// already hold the facts — leave requests, public holidays, dates of birth,
// start dates, interviews, review due dates — and shaped into items on the way
// out. The alternative, a calendar table kept in step by events, gives two
// answers to "is Bola off on Thursday" and no way to tell which is stale.
//
// Each source carries its own visibility rule, and they are not the same rule.
// A public holiday is everybody's. An interview belongs to its panel. Leave is
// the hardest: colleagues need to know somebody is away, and nobody needs to be
// told it is maternity leave, so the kind is named only to the person taking it.
internal sealed class CalendarService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    TimeProvider timeProvider) : ICalendarService
{
    // Who the calendar is about this week. Somebody serving notice is included
    // where the dashboard's headcount would leave them out — a calendar answers
    // "who is here", not "who counts".
    private static readonly EmployeeStatus[] Here =
    [
        EmployeeStatus.Probation,
        EmployeeStatus.Active,
        EmployeeStatus.OnLeave,
        EmployeeStatus.Offboarding,
    ];

    private static readonly LeaveRequestStatus[] Happening =
    [
        LeaveRequestStatus.Pending,
        LeaveRequestStatus.Approved,
    ];

    private Dictionary<Guid, Person>? _people;

    private Dictionary<Guid, string>? _kinds;

    private string? _zone;

    public async Task<IReadOnlyList<CalendarItemDto>> AgendaAsync(
        CalendarQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.To < query.From)
        {
            return [];
        }

        Guid? viewer = await currentEmployee.IdAsync(cancellationToken);

        HashSet<Guid> subjects = query.EmployeeIds.Count > 0
            ? [.. query.EmployeeIds]
            : viewer is Guid self ? [self] : [];

        IReadOnlyDictionary<Guid, Person> people = await PeopleAsync(cancellationToken);

        List<CalendarItemDto> items =
        [
            .. await HolidaysAsync(query, cancellationToken),
            .. await EventsAsync(query, viewer, subjects, people, cancellationToken),
            .. await LeaveAsync(query, viewer, subjects, people, cancellationToken),
            .. Celebrations(query, people),
            .. await InterviewsAsync(query, viewer, subjects, people, cancellationToken),
            .. await ReviewsAsync(query, viewer, people, cancellationToken),
        ];

        return [.. items.OrderBy(item => item.Date)
            .ThenByDescending(item => item.AllDay)
            .ThenBy(item => item.StartTime ?? TimeOnly.MinValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<CalendarItemDto>> UpcomingAsync(
        int days = 7,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = TenantClock.DayOf(
            timeProvider.GetUtcNow(), await ZoneAsync(cancellationToken));

        return await AgendaAsync(
            new CalendarQuery { From = today, To = today.AddDays(Math.Max(1, days) - 1) },
            cancellationToken);
    }

    public async Task<CalendarEventDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        CalendarEvent? stored = await dbContext.CalendarEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(calendarEvent => calendarEvent.Id == id, cancellationToken);

        if (stored is null)
        {
            return null;
        }

        Guid? viewer = await currentEmployee.IdAsync(cancellationToken);
        List<Guid> attendees = await AttendeesOfAsync(id, cancellationToken);

        // Same rule as the agenda: an event you are neither on nor arranged is
        // one you have no business reading the title of.
        bool visible = stored.Audience == CalendarAudience.Company
            || (viewer is Guid self && (attendees.Contains(self) || stored.CreatedByEmployeeId == self));

        return visible
            ? await DescribeAsync(stored, attendees, cancellationToken)
            : null;
    }

    public async Task<CalendarEventResult> CreateAsync(
        CalendarEventRequest request,
        bool canActForAnyone = false,
        CancellationToken cancellationToken = default)
    {
        Guid? viewer = await currentEmployee.IdAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();

        CalendarEvent stored = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Title = request.Title,
            CreatedByEmployeeId = viewer,
            CreatedAt = now,
            UpdatedAt = now,
        };

        return await SaveAsync(stored, request, viewer, canActForAnyone, true, cancellationToken);
    }

    public async Task<CalendarEventResult> UpdateAsync(
        Guid id,
        CalendarEventRequest request,
        bool canActForAnyone = false,
        CancellationToken cancellationToken = default)
    {
        CalendarEvent? stored = await dbContext.CalendarEvents
            .FirstOrDefaultAsync(calendarEvent => calendarEvent.Id == id, cancellationToken);

        if (stored is null)
        {
            return CalendarEventResult.Failed(CalendarEventOutcome.NotFound);
        }

        Guid? viewer = await currentEmployee.IdAsync(cancellationToken);

        if (!MayChange(stored, viewer, canActForAnyone))
        {
            return CalendarEventResult.Failed(CalendarEventOutcome.NotYoursToChange);
        }

        return await SaveAsync(stored, request, viewer, canActForAnyone, false, cancellationToken);
    }

    public async Task<CalendarEventOutcome> DeleteAsync(
        Guid id,
        bool canActForAnyone = false,
        CancellationToken cancellationToken = default)
    {
        CalendarEvent? stored = await dbContext.CalendarEvents
            .FirstOrDefaultAsync(calendarEvent => calendarEvent.Id == id, cancellationToken);

        if (stored is null)
        {
            return CalendarEventOutcome.NotFound;
        }

        Guid? viewer = await currentEmployee.IdAsync(cancellationToken);

        if (!MayChange(stored, viewer, canActForAnyone))
        {
            return CalendarEventOutcome.NotYoursToChange;
        }

        dbContext.CalendarEventAttendees.RemoveRange(
            dbContext.CalendarEventAttendees.Where(
                attendee => attendee.CalendarEventId == id));

        dbContext.CalendarEvents.Remove(stored);

        await dbContext.SaveChangesAsync(cancellationToken);

        return CalendarEventOutcome.Succeeded;
    }

    // ── writing ─────────────────────────────────────────────────────────────

    private static bool MayChange(CalendarEvent stored, Guid? viewer, bool canActForAnyone) =>
        canActForAnyone || (viewer is Guid self && stored.CreatedByEmployeeId == self);

    private async Task<CalendarEventResult> SaveAsync(
        CalendarEvent stored,
        CalendarEventRequest request,
        Guid? viewer,
        bool canActForAnyone,
        bool isNew,
        CancellationToken cancellationToken)
    {
        List<Guid> attendees = [.. request.EmployeeIds.Distinct()];

        Apply(stored, request);

        CalendarRefusal refusal = Rules.Refusal(stored, attendees);

        if (refusal != CalendarRefusal.None)
        {
            return CalendarEventResult.Refused(refusal);
        }

        CalendarEventOutcome reach = await ReachAsync(
            attendees, viewer, canActForAnyone, cancellationToken);

        if (reach != CalendarEventOutcome.Succeeded)
        {
            return CalendarEventResult.Failed(reach);
        }

        stored.UpdatedAt = timeProvider.GetUtcNow();

        if (isNew)
        {
            dbContext.CalendarEvents.Add(stored);
        }
        else
        {
            dbContext.CalendarEventAttendees.RemoveRange(
                dbContext.CalendarEventAttendees.Where(
                    attendee => attendee.CalendarEventId == stored.Id));
        }

        foreach (Guid employeeId in attendees)
        {
            dbContext.CalendarEventAttendees.Add(new CalendarEventAttendee
            {
                Id = Guid.NewGuid(),
                TenantId = stored.TenantId,
                CalendarEventId = stored.Id,
                EmployeeId = employeeId,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return CalendarEventResult.Ok(
            await DescribeAsync(stored, attendees, cancellationToken));
    }

    private static void Apply(CalendarEvent stored, CalendarEventRequest request)
    {
        stored.Title = request.Title.Trim();
        stored.Description = Blank(request.Description);
        stored.Date = request.Date;
        stored.EndDate = request.EndDate == request.Date ? null : request.EndDate;
        stored.AllDay = request.AllDay;

        // An all-day event keeps no times. Storing the 09:00 the form happened to
        // have in it would put a start time on something the grid draws across
        // the whole day, and the agenda row would then disagree with the grid.
        stored.StartTime = request.AllDay ? null : request.StartTime;
        stored.EndTime = request.AllDay ? null : request.EndTime;
        stored.Category = request.Category;
        stored.Audience = request.Audience;
    }

    // Whether these are calendars this caller may write on. Your own always;
    // your reports' because that is what a line manager's diary is for; anyone
    // else's only with the module, resolved at the API edge.
    private async Task<CalendarEventOutcome> ReachAsync(
        IReadOnlyCollection<Guid> attendees,
        Guid? viewer,
        bool canActForAnyone,
        CancellationToken cancellationToken)
    {
        if (attendees.Count == 0)
        {
            return CalendarEventOutcome.Succeeded;
        }

        List<Reach> found = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => attendees.Contains(employee.Id)
                && employee.Status != EmployeeStatus.Deleted)
            .Select(employee => new Reach(employee.Id, employee.ManagerId))
            .ToListAsync(cancellationToken);

        if (found.Count != attendees.Count)
        {
            return CalendarEventOutcome.EmployeeNotFound;
        }

        if (canActForAnyone)
        {
            return CalendarEventOutcome.Succeeded;
        }

        return found.TrueForAll(
            person => person.Id == viewer || (viewer is not null && person.ManagerId == viewer))
            ? CalendarEventOutcome.Succeeded
            : CalendarEventOutcome.NotYoursToAddTo;
    }

    private async Task<CalendarEventDto> DescribeAsync(
        CalendarEvent stored,
        IReadOnlyList<Guid> attendees,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, Person> people = await PeopleAsync(cancellationToken);

        return new CalendarEventDto
        {
            Id = stored.Id,
            Title = stored.Title,
            Description = stored.Description,
            Date = stored.Date,
            EndDate = stored.EndDate,
            AllDay = stored.AllDay,
            StartTime = stored.StartTime,
            EndTime = stored.EndTime,
            Category = stored.Category,
            Audience = stored.Audience,
            CreatedByEmployeeId = stored.CreatedByEmployeeId,
            CreatedByName = NameOf(people, stored.CreatedByEmployeeId),
            Attendees =
            [
                .. attendees.Select(id => new CalendarAttendeeDto
                {
                    EmployeeId = id,
                    Name = NameOf(people, id) ?? "Unknown",
                }),
            ],
            CreatedAt = stored.CreatedAt,
            UpdatedAt = stored.UpdatedAt,
        };
    }

    private Task<List<Guid>> AttendeesOfAsync(Guid eventId, CancellationToken cancellationToken) =>
        dbContext.CalendarEventAttendees
            .AsNoTracking()
            .Where(attendee => attendee.CalendarEventId == eventId)
            .Select(attendee => attendee.EmployeeId)
            .ToListAsync(cancellationToken);

    // ── reading: stored events ──────────────────────────────────────────────

    private async Task<IReadOnlyList<CalendarItemDto>> EventsAsync(
        CalendarQuery query,
        Guid? viewer,
        IReadOnlySet<Guid> subjects,
        IReadOnlyDictionary<Guid, Person> people,
        CancellationToken cancellationToken)
    {
        List<CalendarEvent> stored = await dbContext.CalendarEvents
            .AsNoTracking()
            .Where(calendarEvent => calendarEvent.Date <= query.To
                && (calendarEvent.EndDate ?? calendarEvent.Date) >= query.From)
            .ToListAsync(cancellationToken);

        if (stored.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. stored.Select(calendarEvent => calendarEvent.Id)];

        List<CalendarEventAttendee> guests = await dbContext.CalendarEventAttendees
            .AsNoTracking()
            .Where(attendee => ids.Contains(attendee.CalendarEventId))
            .ToListAsync(cancellationToken);

        ILookup<Guid, Guid> byEvent = guests.ToLookup(
            attendee => attendee.CalendarEventId, attendee => attendee.EmployeeId);

        List<CalendarItemDto> items = [];

        foreach (CalendarEvent calendarEvent in stored)
        {
            bool mine = viewer is Guid self
                && (calendarEvent.CreatedByEmployeeId == self
                    || byEvent[calendarEvent.Id].Contains(self));

            if (calendarEvent.Audience == CalendarAudience.Company)
            {
                items.Add(Item(calendarEvent, null, people, mine));
                continue;
            }

            if (!mine)
            {
                continue;
            }

            List<Guid> shown = [.. byEvent[calendarEvent.Id].Where(subjects.Contains)];

            // Something you arranged for other people is on your calendar too,
            // even when you are not on the guest list. Without this, HR books a
            // one-to-one for two colleagues and can never find it again.
            if (shown.Count == 0 && viewer is Guid arranger
                && calendarEvent.CreatedByEmployeeId == arranger)
            {
                shown = [.. byEvent[calendarEvent.Id]];
            }

            items.AddRange(shown.Select(personId => Item(calendarEvent, personId, people, mine)));
        }

        return items;
    }

    private static CalendarItemDto Item(
        CalendarEvent stored,
        Guid? personId,
        IReadOnlyDictionary<Guid, Person> people,
        bool mine) =>
        new()
        {
            Id = $"event:{stored.Id:D}",
            Title = stored.Title,
            Date = stored.Date,
            EndDate = stored.EndDate,
            StartTime = stored.StartTime,
            EndTime = stored.EndTime,
            AllDay = stored.AllDay,
            Category = stored.Category,
            Layer = Rules.LayerOf(stored.Category, stored.Audience),
            Description = stored.Description,
            PersonId = personId,
            PersonName = NameOf(people, personId),
            EventId = stored.Id,

            // What this caller may remove, which is what they put there.
            // Somebody holding the module may remove more than this says; the
            // read has no way to know, and offering a button that fails is
            // worse than withholding one that would have worked.
            CanDelete = mine && stored.CreatedByEmployeeId is not null,
        };

    // ── reading: public holidays ────────────────────────────────────────────

    private async Task<IReadOnlyList<CalendarItemDto>> HolidaysAsync(
        CalendarQuery query,
        CancellationToken cancellationToken)
    {
        List<PublicHoliday> holidays = await dbContext.PublicHolidays
            .AsNoTracking()
            .Where(holiday => holiday.Date >= query.From && holiday.Date <= query.To)
            .ToListAsync(cancellationToken);

        return
        [
            .. holidays.Select(holiday => new CalendarItemDto
            {
                Id = $"holiday:{holiday.Id:D}",
                Title = holiday.Name,
                Date = holiday.Date,
                AllDay = true,
                Category = CalendarCategory.Holiday,
                Layer = CalendarLayer.Holidays,
            }),
        ];
    }

    // ── reading: leave and cover ────────────────────────────────────────────

    private async Task<IReadOnlyList<CalendarItemDto>> LeaveAsync(
        CalendarQuery query,
        Guid? viewer,
        IReadOnlySet<Guid> subjects,
        IReadOnlyDictionary<Guid, Person> people,
        CancellationToken cancellationToken)
    {
        if (subjects.Count == 0)
        {
            return [];
        }

        List<Guid> wanted = [.. subjects];

        List<LeaveRequest> requests = await dbContext.LeaveRequests
            .AsNoTracking()
            .Where(request => Happening.Contains(request.Status)
                && request.StartDate <= query.To
                && request.EndDate >= query.From
                && (wanted.Contains(request.EmployeeId)
                    || (request.ReliefEmployeeId != null
                        && wanted.Contains(request.ReliefEmployeeId.Value))))
            .ToListAsync(cancellationToken);

        if (requests.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<Guid, string> kinds = await KindsAsync(cancellationToken);

        List<CalendarItemDto> items = [];

        foreach (LeaveRequest request in requests)
        {
            if (subjects.Contains(request.EmployeeId))
            {
                items.Add(LeaveItem(request, viewer, people, kinds));
            }

            if (request.ReliefEmployeeId is Guid relief && subjects.Contains(relief))
            {
                items.Add(CoverItem(request, relief, people));
            }
        }

        return items;
    }

    // Whose leave it is decides what it is called.
    //
    // Maternity leave on a calendar a colleague can open announces a pregnancy;
    // religious leave announces a religion. Neither is the calendar's to tell,
    // and the frontend already applies the same reasoning to sickness. What a
    // colleague needs is the dates, which stay.
    private static CalendarItemDto LeaveItem(
        LeaveRequest request,
        Guid? viewer,
        IReadOnlyDictionary<Guid, Person> people,
        IReadOnlyDictionary<Guid, string> kinds)
    {
        bool pending = request.Status == LeaveRequestStatus.Pending;
        bool theirs = viewer is Guid self && request.EmployeeId == self;
        string kind = kinds.TryGetValue(request.LeaveTypeId, out string? named) ? named : "Leave";
        string who = NameOf(people, request.EmployeeId) ?? "A colleague";

        return new CalendarItemDto
        {
            Id = $"leave:{request.Id:D}",
            Title = theirs
                ? pending ? $"{kind} (requested)" : kind
                : pending ? $"{who} — leave requested" : $"{who} — on leave",
            Date = request.StartDate,
            EndDate = request.EndDate == request.StartDate ? null : request.EndDate,
            AllDay = true,
            Category = pending ? CalendarCategory.LeaveRequest : CalendarCategory.Leave,
            Layer = CalendarLayer.Schedule,

            // The reason somebody gave their own manager is not calendar
            // furniture for the rest of the company.
            Description = theirs ? request.Reason : null,
            PersonId = request.EmployeeId,
            PersonName = who,
            Pending = pending,
        };
    }

    private static CalendarItemDto CoverItem(
        LeaveRequest request,
        Guid relief,
        IReadOnlyDictionary<Guid, Person> people) =>
        new()
        {
            Id = $"cover:{request.Id:D}",
            Title = $"Covering {NameOf(people, request.EmployeeId) ?? "a colleague"}",
            Date = request.StartDate,
            EndDate = request.EndDate == request.StartDate ? null : request.EndDate,
            AllDay = true,
            Category = CalendarCategory.Cover,
            Layer = CalendarLayer.Schedule,
            PersonId = relief,
            PersonName = NameOf(people, relief),
            Pending = request.Status == LeaveRequestStatus.Pending,
        };

    // ── reading: birthdays and work anniversaries ───────────────────────────

    private static IReadOnlyList<CalendarItemDto> Celebrations(
        CalendarQuery query,
        IReadOnlyDictionary<Guid, Person> people)
    {
        List<CalendarItemDto> items = [];

        foreach (Person person in people.Values)
        {
            if (!Here.Contains(person.Status))
            {
                continue;
            }

            if (person.DateOfBirth is DateOnly born)
            {
                items.AddRange(AnnualDates.Occurrences(born, query.From, query.To)
                    .Select(on => new CalendarItemDto
                    {
                        Id = $"birthday:{person.Id:D}:{on:yyyy-MM-dd}",

                        // The day, never the year. A date of birth is the age
                        // nobody asked to publish, and the card only needs the day.
                        Title = $"{person.Name}'s birthday",
                        Date = on,
                        AllDay = true,
                        Category = CalendarCategory.Birthday,
                        Layer = CalendarLayer.Company,
                        PersonId = person.Id,
                        PersonName = person.Name,
                    }));
            }

            if (person.StartDate is not DateOnly started)
            {
                continue;
            }

            items.AddRange(AnnualDates.Occurrences(started, query.From, query.To)
                .Select(on => new { On = on, Years = on.Year - started.Year })

                // Their first day is not an anniversary of anything, and
                // congratulating somebody on nought years is worse than silence.
                .Where(occurrence => occurrence.Years > 0)
                .Select(occurrence => new CalendarItemDto
                {
                    Id = $"anniversary:{person.Id:D}:{occurrence.On:yyyy-MM-dd}",
                    Title = $"{person.Name} — {occurrence.Years} "
                        + (occurrence.Years == 1 ? "year" : "years"),
                    Date = occurrence.On,
                    AllDay = true,
                    Category = CalendarCategory.Anniversary,
                    Layer = CalendarLayer.Company,
                    PersonId = person.Id,
                    PersonName = person.Name,
                }));
        }

        return items;
    }

    // ── reading: interviews ─────────────────────────────────────────────────

    private async Task<IReadOnlyList<CalendarItemDto>> InterviewsAsync(
        CalendarQuery query,
        Guid? viewer,
        IReadOnlySet<Guid> subjects,
        IReadOnlyDictionary<Guid, Person> people,
        CancellationToken cancellationToken)
    {
        if (subjects.Count == 0)
        {
            return [];
        }

        string? zone = await ZoneAsync(cancellationToken);
        (DateTimeOffset from, DateTimeOffset to) = Instants(query, zone);

        // The panel is jsonb behind a value converter, so it cannot be searched
        // in SQL. Narrowed by the window first, which is what keeps that from
        // mattering: a week holds a handful of interviews, not a table of them.
        List<Interview> scheduled = await dbContext.Interviews
            .AsNoTracking()
            .Where(interview => interview.Status == InterviewStatus.Scheduled
                && interview.ScheduledAt >= from
                && interview.ScheduledAt < to)
            .ToListAsync(cancellationToken);

        List<Interview> relevant =
            [.. scheduled.Where(interview => interview.Panel.Exists(subjects.Contains))];

        if (relevant.Count == 0)
        {
            return [];
        }

        List<Guid> candidateIds = [.. relevant.Select(interview => interview.CandidateId).Distinct()];

        Dictionary<Guid, string> candidates = await dbContext.Candidates
            .AsNoTracking()
            .Where(candidate => candidateIds.Contains(candidate.Id))
            .ToDictionaryAsync(
                candidate => candidate.Id,
                candidate => candidate.FirstName + " " + candidate.LastName,
                cancellationToken);

        List<CalendarItemDto> items = [];

        foreach (Interview interview in relevant)
        {
            DateOnly day = TenantClock.DayOf(interview.ScheduledAt, zone);
            TimeOnly start = TenantClock.TimeOf(interview.ScheduledAt, zone);

            // An interview the panel is sitting in names who they are meeting.
            // Seen from outside, it says only that they are busy: an internal
            // candidate's name on a calendar their manager can open is how
            // somebody learns their report is applying elsewhere.
            bool onThePanel = viewer is Guid self && interview.Panel.Contains(self);

            foreach (Guid personId in interview.Panel.Where(subjects.Contains))
            {
                items.Add(new CalendarItemDto
                {
                    Id = $"interview:{interview.Id:D}:{personId:D}",
                    Title = onThePanel
                        && candidates.TryGetValue(interview.CandidateId, out string? who)
                        ? $"Interview — {interview.Round}: {who}"
                        : $"Interview — {interview.Round}",
                    Date = day,
                    StartTime = start,
                    EndTime = start.AddMinutes(interview.DurationMinutes),
                    AllDay = false,
                    Category = CalendarCategory.Interview,
                    Layer = CalendarLayer.Schedule,
                    Description = onThePanel ? interview.Location : null,
                    PersonId = personId,
                    PersonName = NameOf(people, personId),
                });
            }
        }

        return items;
    }

    // ── reading: review due dates ───────────────────────────────────────────

    // Only ever the caller's own, unlike leave. A review that is not on the
    // usual cycle is a signal about somebody, and it is not one their
    // colleagues get to read off a shared calendar.
    private async Task<IReadOnlyList<CalendarItemDto>> ReviewsAsync(
        CalendarQuery query,
        Guid? viewer,
        IReadOnlyDictionary<Guid, Person> people,
        CancellationToken cancellationToken)
    {
        if (viewer is not Guid self)
        {
            return [];
        }

        List<PerformanceReview> reviews = await dbContext.PerformanceReviews
            .AsNoTracking()
            .Where(review => review.Status != ReviewStatus.Completed
                && review.DueDate >= query.From
                && review.DueDate <= query.To
                && (review.EmployeeId == self || review.ReviewerId == self))
            .ToListAsync(cancellationToken);

        List<CalendarItemDto> items = [];

        foreach (PerformanceReview review in reviews)
        {
            if (review.EmployeeId == self)
            {
                items.Add(ReviewItem(
                    review, self, $"Performance review due — {review.Period}", people));
            }

            if (review.ReviewerId == self && review.EmployeeId != self)
            {
                string who = NameOf(people, review.EmployeeId) ?? "A colleague";

                items.Add(ReviewItem(
                    review, self, $"{who}'s review due — {review.Period}", people));
            }
        }

        return items;
    }

    private static CalendarItemDto ReviewItem(
        PerformanceReview review,
        Guid personId,
        string title,
        IReadOnlyDictionary<Guid, Person> people) =>
        new()
        {
            Id = $"review:{review.Id:D}:{personId:D}",
            Title = title,
            Date = review.DueDate,
            AllDay = true,
            Category = CalendarCategory.Review,
            Layer = CalendarLayer.Schedule,
            PersonId = personId,
            PersonName = NameOf(people, personId),
        };

    // ── shared lookups ──────────────────────────────────────────────────────

    // Everybody, once. Four of the sources need names and two need dates, and a
    // lookup per item would turn a week's calendar into a few hundred queries.
    private async Task<IReadOnlyDictionary<Guid, Person>> PeopleAsync(
        CancellationToken cancellationToken)
    {
        if (_people is not null)
        {
            return _people;
        }

        List<Person> rows = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Status != EmployeeStatus.Deleted)
            .Select(employee => new Person(
                employee.Id,
                employee.FirstName + " " + employee.LastName,
                employee.Status,
                employee.StartDate,
                employee.DateOfBirth))
            .ToListAsync(cancellationToken);

        _people = rows.ToDictionary(person => person.Id);

        return _people;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> KindsAsync(
        CancellationToken cancellationToken)
    {
        _kinds ??= await dbContext.LeaveTypes
            .AsNoTracking()
            .ToDictionaryAsync(type => type.Id, type => type.Name, cancellationToken);

        return _kinds;
    }

    private static string? NameOf(IReadOnlyDictionary<Guid, Person> people, Guid? id) =>
        id is Guid key && people.TryGetValue(key, out Person? person) ? person.Name : null;

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // A window of local days as the instants either side of it. Converted
    // through the zone rather than by adding a fixed offset, so a week holding
    // a daylight-saving change is still a week.
    private static (DateTimeOffset From, DateTimeOffset To) Instants(
        CalendarQuery query,
        string? zoneId)
    {
        TimeZoneInfo zone = TenantClock.Zone(zoneId);

        return (
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(
                query.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone)),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(
                query.To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone)));
    }

    // Scoped by id, because tenants are not themselves tenant-scoped: an
    // unfiltered read returns some arbitrary company's row and judges a Lagos
    // diary against London's clock.
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

    private sealed record Person(
        Guid Id,
        string Name,
        EmployeeStatus Status,
        DateOnly? StartDate,
        DateOnly? DateOfBirth);

    private sealed record Reach(Guid Id, Guid? ManagerId);
}
