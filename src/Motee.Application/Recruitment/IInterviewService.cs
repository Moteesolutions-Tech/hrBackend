using Motee.Domain.Recruitment;

namespace Motee.Application.Recruitment;

// Interviews, scheduled and rated.
//
// Scheduling reports clashes rather than refusing on them. A panel member
// double-booked is nearly always a mistake, but sometimes it is two people
// sharing an id in a fixture or a deliberate overlap somebody intends to sort
// out — refusing outright would leave them unable to record the diary they
// already have.
public interface IInterviewService
{
    Task<IReadOnlyList<InterviewDto>> ForCandidateAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InterviewDto>> UpcomingAsync(
        Guid? requisitionId = null,
        CancellationToken cancellationToken = default);

    Task<InterviewResult> ScheduleAsync(
        InterviewRequest request,
        CancellationToken cancellationToken = default);

    Task<InterviewResult> RescheduleAsync(
        Guid id,
        InterviewRequest request,
        CancellationToken cancellationToken = default);

    Task<InterviewResult> CompleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<InterviewResult> CancelAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record InterviewDto
{
    public required Guid Id { get; init; }

    public required Guid CandidateId { get; init; }

    public required string CandidateName { get; init; }

    public required Guid RequisitionId { get; init; }

    public string? RequisitionTitle { get; init; }

    public required string Round { get; init; }

    public required DateTimeOffset ScheduledAt { get; init; }

    public required int DurationMinutes { get; init; }

    public required InterviewMode Mode { get; init; }

    public required IReadOnlyList<Guid> Panel { get; init; }

    public required IReadOnlyList<string> PanelNames { get; init; }

    public string? Location { get; init; }

    public required InterviewStatus Status { get; init; }

    // Panel members already booked elsewhere at this time. Reported, not
    // refused — the diary is the user's to resolve.
    public required IReadOnlyList<string> Clashes { get; init; }
}

public sealed record InterviewRequest
{
    public required Guid CandidateId { get; init; }

    public required string Round { get; init; }

    public required DateTimeOffset ScheduledAt { get; init; }

    public int DurationMinutes { get; init; } = 60;

    public InterviewMode Mode { get; init; } = InterviewMode.Video;

    // Left out, this falls back to the vacancy's standing panel rather than to
    // nobody.
    public IReadOnlyList<Guid>? Panel { get; init; }

    public string? Location { get; init; }
}

public enum InterviewOutcome
{
    Succeeded,
    NotFound,
    CandidateNotFound,

    RoundRequired,

    DurationNotPositive,

    // Scheduling an interview for somebody already out of the running.
    CandidateRejected,

    // Completing or cancelling one that is already completed or cancelled.
    NotScheduled,
}

public sealed record InterviewResult
{
    public required InterviewOutcome Outcome { get; init; }

    public InterviewDto? Interview { get; init; }

    public bool Succeeded => Outcome == InterviewOutcome.Succeeded;

    public static InterviewResult Failed(InterviewOutcome outcome) => new() { Outcome = outcome };

    public static InterviewResult Ok(InterviewDto interview) =>
        new() { Outcome = InterviewOutcome.Succeeded, Interview = interview };
}
