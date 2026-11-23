using Motee.Application.Common;
using Motee.Domain.Projects;

namespace Motee.Application.Projects;

// Hours logged against a project.
//
// Not the weekly timesheet: that is attendance, derived from clock-ins. This is
// which project somebody's hours went to, and only the approved ones count as
// spend.
public interface IProjectTimeService
{
    Task<PagedResult<TimeEntryDto>> ListAsync(
        TimeEntryQuery query,
        CancellationToken cancellationToken = default);

    // Hours logged, awaiting approval and in total, for the strip above the
    // table.
    Task<TimeTally> TallyAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<TimeEntryResult> LogAsync(
        Guid projectId,
        TimeEntryRequest request,
        CancellationToken cancellationToken = default);

    Task<TimeEntryResult> SetStatusAsync(
        Guid projectId,
        Guid entryId,
        TimeEntryStatus status,
        CancellationToken cancellationToken = default);

    Task<TimeEntryResult> DeleteAsync(
        Guid projectId,
        Guid entryId,
        CancellationToken cancellationToken = default);
}

public sealed record TimeEntryQuery : PagedQuery
{
    public Guid? ProjectId { get; init; }

    public Guid? TaskId { get; init; }

    public Guid? EmployeeId { get; init; }

    public TimeEntryStatus? Status { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }
}

public sealed record TimeEntryDto
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public string? ProjectName { get; init; }

    public Guid? TaskId { get; init; }

    // "General project work" where no task was named.
    public string? TaskName { get; init; }

    public required Guid EmployeeId { get; init; }

    public string? EmployeeName { get; init; }

    public required DateOnly WorkedOn { get; init; }

    public required decimal Hours { get; init; }

    public string? Notes { get; init; }

    public required TimeEntryStatus Status { get; init; }

    public Guid? ResolvedByUserId { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record TimeTally
{
    public required decimal ApprovedHours { get; init; }

    public required decimal AwaitingApprovalHours { get; init; }

    public required decimal TotalHours { get; init; }

    public required int Entries { get; init; }
}

public sealed record TimeEntryResult
{
    public required ProjectOutcome Outcome { get; init; }

    public TimeEntryDto? Entry { get; init; }

    public ProjectRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == ProjectOutcome.Succeeded;

    public static TimeEntryResult Failed(ProjectOutcome outcome) => new() { Outcome = outcome };

    public static TimeEntryResult Refused(ProjectRefusal refusal) =>
        new() { Outcome = ProjectOutcome.Refused, Refusal = refusal };

    public static TimeEntryResult Ok(TimeEntryDto entry) =>
        new() { Outcome = ProjectOutcome.Succeeded, Entry = entry };

    public static TimeEntryResult Done() => new() { Outcome = ProjectOutcome.Succeeded };
}

public sealed record TimeEntryRequest
{
    public required Guid EmployeeId { get; init; }

    public Guid? TaskId { get; init; }

    public required DateOnly WorkedOn { get; init; }

    public required decimal Hours { get; init; }

    public string? Notes { get; init; }

    // Logged straight to submitted by default: the screen's button is "log
    // time", not "save a draft".
    public TimeEntryStatus? Status { get; init; }
}
