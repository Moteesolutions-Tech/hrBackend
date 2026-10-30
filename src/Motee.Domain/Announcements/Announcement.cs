using Motee.Domain.Common;

namespace Motee.Domain.Announcements;

public enum AnnouncementType
{
    General,
    Policy,
    Event,
    Urgent,
}

// Where an announcement is in its life, as somebody decided it.
//
// Expired is absent, and so is the moment a scheduled one goes live. Both are
// facts about a date rather than decisions: a scheduled announcement becomes
// visible when its time passes, whether or not a job has run, and an expired one
// stops being current at midnight. Storing either would need a sweep to flip it
// and would be wrong in the window before that sweep.
public enum AnnouncementStatus
{
    Draft,

    // Written and dated, not yet visible. Becomes visible on its own when the
    // date passes — there is no job, because the date is the answer.
    Scheduled,

    Published,

    Archived,
}

public enum AnnouncementPriority
{
    Standard,
    Urgent,
}

// Who it is for.
public enum AnnouncementAudience
{
    AllStaff,

    // Named departments.
    Department,

    // Anybody with somebody reporting to them. Derived from the reporting line
    // rather than a flag, so it stays right as teams change.
    Managers,

    // The top of the reporting tree: people with no manager of their own.
    //
    // Worth naming as a choice rather than a fact. The product has no concept of
    // a leadership team, and the alternatives were a flag somebody has to
    // maintain or an access level, which is about permissions rather than
    // seniority. "Reports to nobody" is at least derivable and self-correcting.
    Leadership,
}

public class Announcement : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Title { get; set; }

    public required string Body { get; set; }

    public AnnouncementType Type { get; set; }

    public AnnouncementStatus Status { get; set; } = AnnouncementStatus.Draft;

    public AnnouncementPriority Priority { get; set; } = AnnouncementPriority.Standard;

    public AnnouncementAudience Audience { get; set; } = AnnouncementAudience.AllStaff;

    // Department ids when the audience is Department. Ids rather than names, so a
    // renamed department does not quietly stop being addressed.
    public List<Guid> TargetDepartmentIds { get; set; } = [];

    // Held at the top of the feed. Separate from priority, which is about how it
    // reads: an urgent notice from last month should not outrank a pinned one.
    public bool IsPinned { get; set; }

    public bool RequiresAcknowledgement { get; set; }

    // When it should appear. Null means now, for something published directly.
    public DateTimeOffset? ScheduledFor { get; set; }

    // When it stops being current. Null for the ones that never do.
    public DateTimeOffset? ExpiresAt { get; set; }

    // One attachment, in stored_files with every other upload.
    public Guid? AttachmentFileId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
}

// Somebody saying they have read it.
//
// Against the announcement rather than a version, unlike a document: an
// announcement is not reissued. Editing one after people have agreed to it is
// the thing to prevent, and that is a rule rather than a second row.
public class AnnouncementAcknowledgement : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid AnnouncementId { get; set; }

    public Guid EmployeeId { get; set; }

    public DateTimeOffset AcknowledgedAt { get; set; }
}

// Somebody having opened it.
//
// A row per person rather than a counter, so the number means unique readers
// rather than clicks — and so a counter cannot drift under two people opening it
// at once.
public class AnnouncementView : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid AnnouncementId { get; set; }

    public Guid EmployeeId { get; set; }

    public DateTimeOffset ViewedAt { get; set; }
}

public static class Announcements
{
    // Visible to its audience now: published, or scheduled for a moment that has
    // passed. Derived, so a scheduled notice goes live on time without a job and
    // without anybody pressing anything.
    public static bool IsLive(Announcement announcement, DateTimeOffset now) =>
        !IsExpired(announcement, now)
            && announcement.Status switch
            {
                AnnouncementStatus.Published => true,
                AnnouncementStatus.Scheduled =>
                    announcement.ScheduledFor is DateTimeOffset at && at <= now,
                _ => false,
            };

    public static bool IsExpired(Announcement announcement, DateTimeOffset now) =>
        announcement.ExpiresAt is DateTimeOffset expiry && expiry <= now;

    // What the screen shows, which is not always what is stored. A scheduled one
    // whose time has come reads as published; anything past its expiry reads as
    // expired whatever it was before.
    public static string EffectiveStatus(Announcement announcement, DateTimeOffset now)
    {
        if (announcement.Status == AnnouncementStatus.Archived)
        {
            return "archived";
        }

        if (IsExpired(announcement, now))
        {
            return "expired";
        }

        return IsLive(announcement, now)
            ? "published"
            : announcement.Status == AnnouncementStatus.Scheduled ? "scheduled" : "draft";
    }
}
