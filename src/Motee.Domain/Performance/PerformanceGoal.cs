using Motee.Domain.Common;

namespace Motee.Domain.Performance;

public enum GoalCategory
{
    Technical,
    Leadership,
    Communication,
    Growth,
    Operational,
}

// Where a goal stands, as somebody decided it.
//
// Overdue is deliberately absent. The interface shows it, but it is a fact about the
// date rather than a decision anybody made — storing it would need a job to flip
// every goal at midnight, and a goal that was overdue on Tuesday and finished on
// Wednesday would keep saying so. Derived on read, like every other date-relative
// answer here.
public enum GoalStatus
{
    OnTrack,
    AtRisk,
    Completed,
    Cancelled,
}

// What somebody is working towards.
public class PerformanceGoal : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public GoalCategory Category { get; set; }

    public GoalStatus Status { get; set; } = GoalStatus.OnTrack;

    // 0 to 100. Independent of status on purpose: a goal at 100 per cent is not
    // finished until somebody says so, and one at 40 can be at risk or on track
    // depending on how much time is left — which is a judgement, not arithmetic.
    public int Progress { get; set; }

    public DateOnly DueDate { get; set; }

    // The framework the company uses to write them — "SMART", "OKR". Free text
    // because it is theirs and nothing branches on it.
    public string? GoalType { get; set; }

    // Check-ins, newest first. Each is a progress figure with a note and a date.
    //
    // Kept as a list rather than overwriting Progress alone, because the story of how
    // something got to 60 per cent is the half of a goal worth reading at review
    // time — a single number says nothing about whether it stalled in March.
    public List<GoalUpdate> Updates { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed record GoalUpdate
{
    public required DateTimeOffset At { get; init; }

    public required int Progress { get; init; }

    public string? Note { get; init; }

    // Who checked in. A manager nudging a goal forward and the person doing the work
    // are different events, and a review that cannot tell them apart is one where
    // somebody's own progress notes look like their manager's.
    public Guid? ByUserId { get; init; }
}

public static class PerformanceGoals
{
    public const int MinimumProgress = 0;

    public const int MaximumProgress = 100;

    // Past its date and nobody has finished or stopped it. Derived rather than
    // stored: the answer changes at midnight without anybody touching the row.
    public static bool IsOverdue(PerformanceGoal goal, DateOnly today) =>
        goal.DueDate < today
            && goal.Status is not (GoalStatus.Completed or GoalStatus.Cancelled);

    // Finishing and abandoning are both ends. Neither reopens: a goal that comes back
    // is a new goal, and reopening would leave CompletedAt describing a completion
    // that no longer happened.
    public static bool IsFinal(GoalStatus status) =>
        status is GoalStatus.Completed or GoalStatus.Cancelled;
}
