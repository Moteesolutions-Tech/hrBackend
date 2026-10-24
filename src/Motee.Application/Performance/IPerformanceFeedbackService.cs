using Motee.Application.Common;
using Motee.Domain.Performance;

namespace Motee.Application.Performance;

// Feedback, asked for and given.
//
// Two directions that look similar and are not: the requests somebody has sent
// about themselves, and the requests waiting on them to answer about a colleague.
// Keeping them as separate reads is what stops a screen showing one and labelling
// it the other.
public interface IPerformanceFeedbackService
{
    // Written about the caller.
    Task<IReadOnlyList<PerformanceFeedbackDto>> AboutMeAsync(
        CancellationToken cancellationToken = default);

    // Asks the caller has sent out.
    Task<IReadOnlyList<FeedbackRequestDto>> MyRequestsAsync(
        CancellationToken cancellationToken = default);

    // Asks waiting on the caller to answer about somebody else.
    Task<IReadOnlyList<FeedbackRequestDto>> AwaitingMeAsync(
        CancellationToken cancellationToken = default);

    Task<FeedbackRequestResult> AskAsync(
        AskForFeedbackRequest request,
        CancellationToken cancellationToken = default);

    Task<FeedbackRequestResult> DeclineAsync(
        Guid requestId,
        CancellationToken cancellationToken = default);

    // Give it — answering an ask, or unprompted.
    Task<FeedbackResult> GiveAsync(
        GiveFeedbackRequest request,
        CancellationToken cancellationToken = default);

    // Everything written about one person, for whoever is running their review.
    Task<PagedResult<PerformanceFeedbackDto>> ListAsync(
        FeedbackQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record PerformanceFeedbackDto
{
    public required Guid Id { get; init; }

    public required Guid AboutEmployeeId { get; init; }

    public required string AboutEmployeeName { get; init; }

    public required Guid FromEmployeeId { get; init; }

    public required string FromName { get; init; }

    public string? FromJobTitle { get; init; }

    // Worked out from the reporting line rather than taken from the client, so a
    // report cannot label their own feedback "manager" and give it weight it has
    // not earned.
    public required FeedbackType Type { get; init; }

    public required string Message { get; init; }

    public Guid? RequestId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record FeedbackRequestDto
{
    public required Guid Id { get; init; }

    public required Guid AskedByEmployeeId { get; init; }

    public required string AskedByName { get; init; }

    public required Guid AskedOfEmployeeId { get; init; }

    public required string AskedOfName { get; init; }

    public string? Context { get; init; }

    public required FeedbackRequestStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? RespondedAt { get; init; }
}

public sealed record AskForFeedbackRequest
{
    public required Guid ColleagueId { get; init; }

    public string? Context { get; init; }
}

public sealed record GiveFeedbackRequest
{
    public required Guid AboutEmployeeId { get; init; }

    public required string Message { get; init; }

    // The ask this answers, when it answers one. Checked against the caller, so a
    // valid id cannot be used to close somebody else's request.
    public Guid? RequestId { get; init; }
}

public sealed record FeedbackQuery : PagedQuery
{
    public Guid? AboutEmployeeId { get; init; }

    public FeedbackType? Type { get; init; }
}

public enum FeedbackOutcome
{
    Succeeded,
    NotFound,
    EmployeeNotFound,

    // Nobody reviews themselves through the back door. A self-assessment is its
    // own field on a review, written knowing it is one.
    CannotBeAboutYourself,

    MessageRequired,

    // One open ask per colleague. A second is a nudge, not a new question, and two
    // in a list look like two different requests.
    AlreadyAsked,

    // Answered or declined already.
    AlreadyResolved,
}

public sealed record FeedbackResult
{
    public required FeedbackOutcome Outcome { get; init; }

    public PerformanceFeedbackDto? Feedback { get; init; }

    public bool Succeeded => Outcome == FeedbackOutcome.Succeeded;

    public static FeedbackResult Failed(FeedbackOutcome outcome) => new() { Outcome = outcome };

    public static FeedbackResult Ok(PerformanceFeedbackDto feedback) =>
        new() { Outcome = FeedbackOutcome.Succeeded, Feedback = feedback };
}

public sealed record FeedbackRequestResult
{
    public required FeedbackOutcome Outcome { get; init; }

    public FeedbackRequestDto? Request { get; init; }

    public bool Succeeded => Outcome == FeedbackOutcome.Succeeded;

    public static FeedbackRequestResult Failed(FeedbackOutcome outcome) =>
        new() { Outcome = outcome };

    public static FeedbackRequestResult Ok(FeedbackRequestDto request) =>
        new() { Outcome = FeedbackOutcome.Succeeded, Request = request };
}
