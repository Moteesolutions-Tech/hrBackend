using Motee.Application.Common;
using Motee.Domain.Surveys;

namespace Motee.Application.Surveys;

// Surveys.
//
// Results are aggregates. There is no route that returns one person's answers
// on an anonymous survey, and no field to put a respondent's name in — and
// below the reporting threshold the aggregates are withheld too, because with
// three replies from a team of four an average identifies people as surely as
// a name.
public interface ISurveyService
{
    Task<PagedResult<SurveyDto>> ListAsync(
        SurveyQuery query,
        CancellationToken cancellationToken = default);

    Task<SurveyDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SurveyBoardDto> SummaryAsync(CancellationToken cancellationToken = default);

    // Aggregated answers. Withheld, with a reason, when an anonymous survey
    // has too few replies to hide anybody in.
    Task<SurveyResultsDto?> ResultsAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    // Engagement over time, by month and department, from what people actually
    // answered. The client ships twelve months of hardcoded figures against
    // five hardcoded department names; a new tenant's chart is empty here
    // until real surveys accumulate, which is the truthful answer.
    Task<IReadOnlyList<EngagementPointDto>> EngagementTrendAsync(
        int months = 12,
        CancellationToken cancellationToken = default);

    Task<SurveyResult> CreateAsync(
        SurveyRequest request,
        CancellationToken cancellationToken = default);

    // Questions are replaced wholesale, which is what the builder sends. Only
    // allowed while nobody has answered — editing a question under a reply
    // would leave an answer to a question that no longer exists.
    Task<SurveyResult> UpdateAsync(
        Guid id,
        SurveyRequest request,
        CancellationToken cancellationToken = default);

    // Opening the survey captures how many people were asked.
    Task<SurveyResult> SetStatusAsync(
        Guid id,
        SurveyStatus status,
        CancellationToken cancellationToken = default);

    Task<SurveyResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    // What is open to the signed-in employee and still unanswered by them.
    Task<IReadOnlyList<SurveyDto>> MineAsync(CancellationToken cancellationToken = default);

    Task<SurveyResult> RespondAsync(
        Guid id,
        SurveySubmissionRequest request,
        CancellationToken cancellationToken = default);

    // Whether the caller has already answered, and what they said — on a
    // signed survey. On an anonymous one this reports only that they have
    // answered, because reading it back is a route to their identity.
    Task<MySurveyResponseDto?> MyResponseAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public sealed record SurveyDto
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required SurveyType SurveyType { get; init; }

    public required SurveyStatus Status { get; init; }

    public required SurveyAudience Audience { get; init; }

    public required IReadOnlyList<Guid> TargetDepartmentIds { get; init; }

    public required bool IsAnonymous { get; init; }

    public required bool SendReminder { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public PulseFrequency? PulseFrequency { get; init; }

    public required IReadOnlyList<SurveyQuestionDto> Questions { get; init; }

    // Captured when the survey opened. Null while it is still a draft.
    public int? TotalTargeted { get; init; }

    public required int Responses { get; init; }

    // Null when nobody was asked — not nought per cent, which reads as
    // "nobody replied".
    public decimal? ResponseRate { get; init; }

    public required bool HaveIAnswered { get; init; }

    public Guid? CreatedByEmployeeId { get; init; }

    public string? CreatedByName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SurveyQuestionDto
{
    public required Guid Id { get; init; }

    public required string Text { get; init; }

    public required QuestionType QuestionType { get; init; }

    public required bool Required { get; init; }

    public required IReadOnlyList<string> Options { get; init; }

    public int? ScaleMin { get; init; }

    public int? ScaleMax { get; init; }

    public required int Sequence { get; init; }
}

public sealed record SurveyResultsDto
{
    public required Guid SurveyId { get; init; }

    public required int Responses { get; init; }

    public decimal? ResponseRate { get; init; }

    // Whether the aggregates below are present. False on an anonymous survey
    // with too few replies, with Withheld saying why — so the screen can
    // explain rather than render an empty chart.
    public required bool IsReportable { get; init; }

    public string? Withheld { get; init; }

    // Null where nothing was asked on a scale.
    public decimal? EngagementScore { get; init; }

    // Null where nothing was asked on the NPS scale. Zero is a real score.
    public EnpsTally? Enps { get; init; }

    public required IReadOnlyList<QuestionResultDto> Questions { get; init; }

    // Only groups big enough to report. A department below the threshold is
    // counted into Suppressed rather than listed.
    public required IReadOnlyList<DepartmentScoreDto> ByDepartment { get; init; }

    public required int SuppressedDepartments { get; init; }
}

public sealed record QuestionResultDto
{
    public required Guid QuestionId { get; init; }

    public required string Text { get; init; }

    public required QuestionType QuestionType { get; init; }

    public required int Answers { get; init; }

    // For a rating or NPS question: how many chose each value.
    public required IReadOnlyList<ValueCountDto> Distribution { get; init; }

    // For a choice or Likert question: how many chose each option.
    public required IReadOnlyList<ValueCountDto> Options { get; init; }

    // Mean of a scale question, on its own scale rather than normalised, so
    // it reads the way the question was asked.
    public decimal? Average { get; init; }

    // Free text, and only once the survey is reportable at all. Never carries
    // a respondent — though prose can of course identify its author, which no
    // API can prevent.
    public required IReadOnlyList<string> Comments { get; init; }
}

public sealed record ValueCountDto
{
    public required string Value { get; init; }

    public required int Count { get; init; }
}

public sealed record DepartmentScoreDto
{
    public Guid? DepartmentId { get; init; }

    public required string DepartmentName { get; init; }

    public required int Responses { get; init; }

    public decimal? EngagementScore { get; init; }
}

public sealed record EngagementPointDto
{
    // "2026-04".
    public required string Month { get; init; }

    public decimal? CompanyWide { get; init; }

    public required IReadOnlyList<DepartmentScoreDto> ByDepartment { get; init; }
}

public sealed record SurveyBoardDto
{
    public required int Total { get; init; }

    public required int Active { get; init; }

    public required int Draft { get; init; }

    public required int Closed { get; init; }

    public required int Archived { get; init; }

    public required int TotalResponses { get; init; }

    // Across every survey that has been opened, from the answers rather than
    // the turnout.
    public decimal? EngagementScore { get; init; }
}

public sealed record MySurveyResponseDto
{
    public required bool HaveIAnswered { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    // Present only on a signed survey. Reading back an anonymous answer is a
    // route to the author, so it is simply not offered.
    public IReadOnlyList<SubmittedAnswerDto>? Answers { get; init; }
}

public sealed record SubmittedAnswerDto
{
    public required Guid QuestionId { get; init; }

    public required IReadOnlyList<string> Values { get; init; }
}

public sealed record SurveyQuery : PagedQuery
{
    public SurveyStatus? Status { get; init; }

    public SurveyType? SurveyType { get; init; }

    public SurveyAudience? Audience { get; init; }

    public string? Search { get; init; }
}

public sealed record SurveyRequest
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public SurveyType SurveyType { get; init; }

    public SurveyAudience Audience { get; init; }

    public IReadOnlyList<Guid>? TargetDepartmentIds { get; init; }

    public bool IsAnonymous { get; init; }

    public bool SendReminder { get; init; }

    public DateOnly? StartDate { get; init; }

    public DateOnly? EndDate { get; init; }

    public PulseFrequency? PulseFrequency { get; init; }

    public SurveyStatus Status { get; init; } = SurveyStatus.Draft;

    public required IReadOnlyList<SurveyQuestionRequest> Questions { get; init; }
}

public sealed record SurveyQuestionRequest
{
    public required string Text { get; init; }

    public QuestionType QuestionType { get; init; }

    public bool Required { get; init; }

    public IReadOnlyList<string>? Options { get; init; }

    public int? ScaleMin { get; init; }

    public int? ScaleMax { get; init; }
}

public sealed record SurveySubmissionRequest
{
    public required IReadOnlyList<SubmittedAnswerRequest> Answers { get; init; }
}

public sealed record SubmittedAnswerRequest
{
    public required Guid QuestionId { get; init; }

    public IReadOnlyList<string>? Values { get; init; }
}

public enum SurveyOutcome
{
    Succeeded,
    NotFound,
    Refused,

    // Editing a survey people have already answered.
    HasResponses,

    NoEmployeeRecord,
}

public sealed record SurveyResult
{
    public required SurveyOutcome Outcome { get; init; }

    public SurveyDto? Survey { get; init; }

    public SurveyRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == SurveyOutcome.Succeeded;

    public static SurveyResult Failed(SurveyOutcome outcome) => new() { Outcome = outcome };

    public static SurveyResult Refused(SurveyRefusal refusal) =>
        new() { Outcome = SurveyOutcome.Refused, Refusal = refusal };

    public static SurveyResult Ok(SurveyDto survey) =>
        new() { Outcome = SurveyOutcome.Succeeded, Survey = survey };

    public static SurveyResult Done() => new() { Outcome = SurveyOutcome.Succeeded };
}
