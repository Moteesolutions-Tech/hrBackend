using Motee.Application.Common;
using Motee.Domain.Authorization;
using Motee.Domain.Learning;

namespace Motee.Application.Learning;

// Courses, who is on them, and whether they passed.
//
// The quiz is marked here. The client marks it in the learner's own browser
// against the correct answer it was sent, so the routes that serve a learner
// hand back AskedQuestion — a shape with nowhere to put the answer.
public interface ILearningService
{
    Task<PagedResult<CourseRowDto>> ListAsync(
        CourseQuery query,
        CancellationToken cancellationToken = default);

    Task<CourseDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LearningSummaryDto> SummaryAsync(CancellationToken cancellationToken = default);

    // Where the company is on the training it has made compulsory.
    Task<MandatoryTally> ComplianceAsync(CancellationToken cancellationToken = default);

    Task<CourseResult> CreateAsync(
        CourseRequest request,
        CancellationToken cancellationToken = default);

    Task<CourseResult> UpdateAsync(
        Guid id,
        CourseRequest request,
        CancellationToken cancellationToken = default);

    Task<CourseResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    // The quiz as its author sees it, answers included. Behind the module
    // grant, and never served to a learner.
    Task<IReadOnlyList<QuizQuestionDto>> QuizAsync(
        Guid courseId,
        CancellationToken cancellationToken = default);

    // Replaces the quiz wholesale: a question is identified by its position
    // while it is being edited, so there is nothing to patch.
    Task<CourseResult> SetQuizAsync(
        Guid courseId,
        QuizRequest request,
        CancellationToken cancellationToken = default);

    // ── who is on a course ──────────────────────────────────────────────────

    // Who is on a course is personal: it names people and how far they have got. So
    // this takes a viewer and narrows by it, like the other registers — unlike the
    // course catalogue above, which is the company's own list of what it offers and is
    // readable by anybody who might enrol.
    Task<PagedResult<EnrollmentDto>> EnrollmentsAsync(
        EnrollmentQuery query,
        Viewer viewer,
        CancellationToken cancellationToken = default);

    // Several people at once, which is how the assign dialog works. Somebody
    // already on the course is skipped rather than failing the whole batch.
    Task<EnrollResult> EnrollAsync(
        Guid courseId,
        EnrollRequest request,
        CancellationToken cancellationToken = default);

    Task<EnrollmentResult> SetProgressAsync(
        Guid enrollmentId,
        ProgressRequest request,
        CancellationToken cancellationToken = default);

    Task<EnrollmentResult> SetStatusAsync(
        Guid enrollmentId,
        EnrollmentStatus status,
        CancellationToken cancellationToken = default);

    Task<EnrollmentResult> WithdrawAsync(
        Guid enrollmentId,
        CancellationToken cancellationToken = default);

    // ── the learner's own ───────────────────────────────────────────────────

    // What the caller is on, and what else they could take.
    Task<MyLearningDto> MineAsync(CancellationToken cancellationToken = default);

    // The quiz as a learner is given it: the questions, and nowhere to read
    // the answers.
    Task<AskedQuizDto?> AskedAsync(
        Guid enrollmentId,
        CancellationToken cancellationToken = default);

    // Marked on the way in. The score, whether it passed and the certificate
    // that follows are all worked out here.
    Task<AttemptResult> AttemptAsync(
        Guid enrollmentId,
        AttemptRequest request,
        CancellationToken cancellationToken = default);
}

public enum LearningOutcome
{
    Succeeded,
    NotFound,
    Refused,
    EmployeeNotFound,
    NoEmployeeRecord,

    // The enrolment belongs to somebody else.
    NotYours,
}

public sealed record CourseResult
{
    public required LearningOutcome Outcome { get; init; }

    public CourseDto? Course { get; init; }

    public LearningRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == LearningOutcome.Succeeded;

    public static CourseResult Failed(LearningOutcome outcome) => new() { Outcome = outcome };

    public static CourseResult Refused(LearningRefusal refusal) =>
        new() { Outcome = LearningOutcome.Refused, Refusal = refusal };

    public static CourseResult Ok(CourseDto course) =>
        new() { Outcome = LearningOutcome.Succeeded, Course = course };

    public static CourseResult Done() => new() { Outcome = LearningOutcome.Succeeded };
}

public sealed record EnrollmentResult
{
    public required LearningOutcome Outcome { get; init; }

    public EnrollmentDto? Enrollment { get; init; }

    public LearningRefusal Refusal { get; init; }

    public bool Succeeded => Outcome == LearningOutcome.Succeeded;

    public static EnrollmentResult Failed(LearningOutcome outcome) =>
        new() { Outcome = outcome };

    public static EnrollmentResult Refused(LearningRefusal refusal) =>
        new() { Outcome = LearningOutcome.Refused, Refusal = refusal };

    public static EnrollmentResult Ok(EnrollmentDto enrollment) =>
        new() { Outcome = LearningOutcome.Succeeded, Enrollment = enrollment };

    public static EnrollmentResult Done() => new() { Outcome = LearningOutcome.Succeeded };
}

public sealed record EnrollResult
{
    public required LearningOutcome Outcome { get; init; }

    public LearningRefusal Refusal { get; init; }

    public required IReadOnlyList<EnrollmentDto> Enrolled { get; init; } = [];

    // Already on the course, so nothing was done for them.
    public required IReadOnlyList<Guid> Skipped { get; init; } = [];

    public bool Succeeded => Outcome == LearningOutcome.Succeeded;

    public static EnrollResult Failed(LearningOutcome outcome) =>
        new() { Outcome = outcome, Enrolled = [], Skipped = [] };

    public static EnrollResult Refused(LearningRefusal refusal) =>
        new()
        {
            Outcome = LearningOutcome.Refused,
            Refusal = refusal,
            Enrolled = [],
            Skipped = [],
        };

    public static EnrollResult Ok(
        IReadOnlyList<EnrollmentDto> enrolled,
        IReadOnlyList<Guid> skipped) =>
        new()
        {
            Outcome = LearningOutcome.Succeeded,
            Enrolled = enrolled,
            Skipped = skipped,
        };
}

public sealed record AttemptResult
{
    public required LearningOutcome Outcome { get; init; }

    public LearningRefusal Refusal { get; init; }

    public ScoredAttempt? Scored { get; init; }

    public EnrollmentDto? Enrollment { get; init; }

    // Null when the attempt did not pass, or the course issues no certificate.
    public CertificationDto? Certificate { get; init; }

    public int? AttemptsLeft { get; init; }

    public bool Succeeded => Outcome == LearningOutcome.Succeeded;

    public static AttemptResult Failed(LearningOutcome outcome) => new() { Outcome = outcome };

    public static AttemptResult Refused(LearningRefusal refusal, int? attemptsLeft = null) =>
        new()
        {
            Outcome = LearningOutcome.Refused,
            Refusal = refusal,
            AttemptsLeft = attemptsLeft,
        };
}

public sealed record CourseQuery : PagedQuery
{
    public CourseCategory? Category { get; init; }

    public CourseStatus? Status { get; init; }

    public DeliveryMode? DeliveryMode { get; init; }

    public bool? MandatoryOnly { get; init; }

    public bool? WithQuizOnly { get; init; }

    public string? Search { get; init; }
}

public sealed record CourseRowDto
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required CourseCategory Category { get; init; }

    public required CourseStatus Status { get; init; }

    public required DeliveryMode DeliveryMode { get; init; }

    public string? Instructor { get; init; }

    public string? Provider { get; init; }

    public required bool IsMandatory { get; init; }

    public int? ValidityMonths { get; init; }

    public string? CourseUrl { get; init; }

    public string? VideoUrl { get; init; }

    public required decimal DurationHours { get; init; }

    public int? Capacity { get; init; }

    public DateOnly? StartsOn { get; init; }

    public DateOnly? EndsOn { get; init; }

    public required IReadOnlyList<string> Tags { get; init; }

    public required bool HasQuiz { get; init; }

    public required int PassingScore { get; init; }

    public int? MaxAttempts { get; init; }

    // Counted from the enrolments, not kept on the course: a stored count
    // drifts the first time somebody is dropped.
    public required int EnrolledCount { get; init; }

    public required int CompletedCount { get; init; }

    // Null when the course has no capacity set.
    public int? PlacesLeft { get; init; }

    public required int CompletionPercent { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record CourseDto
{
    public required CourseRowDto Course { get; init; }

    // How many questions, not the questions themselves: this route is read by
    // screens a learner can reach.
    public required int QuestionCount { get; init; }
}

public sealed record QuizQuestionDto
{
    public required Guid Id { get; init; }

    public required int Sequence { get; init; }

    public required QuestionKind QuestionKind { get; init; }

    public required string Prompt { get; init; }

    public required IReadOnlyList<string> Options { get; init; }

    // Only on the author's view, behind the module grant.
    public required int CorrectIndex { get; init; }

    public required int Points { get; init; }
}

public sealed record AskedQuizDto
{
    public required Guid EnrollmentId { get; init; }

    public required Guid CourseId { get; init; }

    public required string CourseTitle { get; init; }

    public required int PassingScore { get; init; }

    public int? AttemptsLeft { get; init; }

    // Nowhere on this shape to read the answers.
    public required IReadOnlyList<AskedQuestion> Questions { get; init; }
}

public sealed record EnrollmentQuery : PagedQuery
{
    public Guid? CourseId { get; init; }

    public Guid? EmployeeId { get; init; }

    public Guid? DepartmentId { get; init; }

    public EnrollmentStatus? Status { get; init; }

    public bool? OverdueOnly { get; init; }

    public string? Search { get; init; }
}

public sealed record EnrollmentDto
{
    public required Guid Id { get; init; }

    public required Guid CourseId { get; init; }

    public required string CourseTitle { get; init; }

    public required bool IsMandatory { get; init; }

    public required Guid EmployeeId { get; init; }

    public string? EmployeeName { get; init; }

    public string? DepartmentName { get; init; }

    public required EnrollmentStatus Status { get; init; }

    public required int Progress { get; init; }

    public int? ResumeSeconds { get; init; }

    public required DateOnly EnrolledOn { get; init; }

    public DateOnly? DueOn { get; init; }

    public DateOnly? CompletedOn { get; init; }

    public required bool IsOverdue { get; init; }

    // Read from the attempts rather than stored beside them.
    public decimal? BestScore { get; init; }

    public required bool QuizPassed { get; init; }

    public required int AttemptsUsed { get; init; }

    public int? AttemptsLeft { get; init; }

    public required IReadOnlyList<AttemptDto> Attempts { get; init; }
}

public sealed record AttemptDto
{
    public required decimal Score { get; init; }

    public required bool Passed { get; init; }

    public required int Correct { get; init; }

    public required int Questions { get; init; }

    public required DateTimeOffset AttemptedAt { get; init; }
}

public sealed record MyLearningDto
{
    public Guid? EmployeeId { get; init; }

    // What the caller is on.
    public required IReadOnlyList<EnrollmentDto> Enrolled { get; init; }

    // Active courses they are not on yet.
    public required IReadOnlyList<CourseRowDto> Library { get; init; }

    public required int Completed { get; init; }

    public required int InProgress { get; init; }

    public required int Overdue { get; init; }

    public required decimal HoursCompleted { get; init; }
}

public sealed record LearningSummaryDto
{
    public required int Courses { get; init; }

    public required int ActiveCourses { get; init; }

    public required int MandatoryCourses { get; init; }

    public required int Enrollments { get; init; }

    public required int Completed { get; init; }

    public required int InProgress { get; init; }

    public required int Overdue { get; init; }

    public required int CompletionPercent { get; init; }

    public required decimal HoursDelivered { get; init; }
}

public sealed record CourseRequest
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public CourseCategory Category { get; init; }

    public CourseStatus Status { get; init; }

    public DeliveryMode DeliveryMode { get; init; }

    public string? Instructor { get; init; }

    public string? Provider { get; init; }

    public bool IsMandatory { get; init; }

    public int? ValidityMonths { get; init; }

    public string? CourseUrl { get; init; }

    public string? VideoUrl { get; init; }

    public decimal DurationHours { get; init; }

    public int? Capacity { get; init; }

    public DateOnly? StartsOn { get; init; }

    public DateOnly? EndsOn { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }
}

public sealed record QuizRequest
{
    public int PassingScore { get; init; } = 70;

    // Null is unlimited.
    public int? MaxAttempts { get; init; }

    public required IReadOnlyList<QuizQuestionRequest> Questions { get; init; }
}

public sealed record QuizQuestionRequest
{
    public QuestionKind QuestionKind { get; init; }

    public required string Prompt { get; init; }

    public required IReadOnlyList<string> Options { get; init; }

    public int CorrectIndex { get; init; }

    public int Points { get; init; } = 1;
}

public sealed record EnrollRequest
{
    public required IReadOnlyList<Guid> EmployeeIds { get; init; }

    public DateOnly? DueOn { get; init; }
}

public sealed record ProgressRequest
{
    public required int Progress { get; init; }

    public int? ResumeSeconds { get; init; }
}

public sealed record AttemptRequest
{
    // Question id to the option chosen. Anything missing is wrong, not
    // skipped.
    public required IReadOnlyDictionary<Guid, int> Answers { get; init; }
}
