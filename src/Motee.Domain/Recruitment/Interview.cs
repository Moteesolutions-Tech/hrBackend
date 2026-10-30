using Motee.Domain.Common;

namespace Motee.Domain.Recruitment;

public enum InterviewMode
{
    Onsite,
    Video,
    Phone,
}

public enum InterviewStatus
{
    Scheduled,
    Completed,
    Cancelled,
}

public class Interview : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CandidateId { get; set; }

    public Guid RequisitionId { get; set; }

    // "First round", "Technical", "With the CTO". Free text, because the shape
    // of a hiring process differs by role and a fixed set would be wrong for
    // most of them.
    public required string Round { get; set; }

    public DateTimeOffset ScheduledAt { get; set; }

    public int DurationMinutes { get; set; } = 60;

    public InterviewMode Mode { get; set; } = InterviewMode.Video;

    // Employee ids. Empty is allowed at the point of scheduling — a slot in the
    // diary with the panel to be confirmed is a real thing — but a panel of
    // nobody is worth warning about rather than refusing.
    public List<Guid> Panel { get; set; } = [];

    // An address or a meeting link, depending on the mode.
    public string? Location { get; set; }

    public InterviewStatus Status { get; set; } = InterviewStatus.Scheduled;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public static class Interviews
{
    public static DateTimeOffset EndOf(Interview interview) =>
        interview.ScheduledAt.AddMinutes(interview.DurationMinutes);

    // Whether two interviews ask the same person to be in two places at once.
    //
    // Checked across the panel rather than the candidate: a candidate booked
    // twice is unusual but legitimate — two rounds on one day — while an
    // interviewer double-booked is a meeting that will not happen.
    public static bool Clashes(Interview left, Interview right) =>
        left.Id != right.Id
            && left.Status == InterviewStatus.Scheduled
            && right.Status == InterviewStatus.Scheduled
            && left.Panel.Intersect(right.Panel).Any()
            && left.ScheduledAt < EndOf(right)
            && right.ScheduledAt < EndOf(left);

    public static bool CanCancel(InterviewStatus status) => status == InterviewStatus.Scheduled;
}
