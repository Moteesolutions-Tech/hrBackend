using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Learning;
using Motee.Domain.Authorization;
using Motee.Domain.Learning;

namespace Motee.Api.Controllers;

// Learning and development.
//
// The split that matters is between the two quiz routes. The author's view
// carries each question's correct answer and sits behind the module grant; the
// learner's carries the questions and nothing else, because there is nowhere on
// that shape to put an answer. The client sends the answers to the browser and
// marks the attempt there, which makes every compliance certificate it issues
// evidence of nothing.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/learning")]
public class LearningController(ILearningService learning) : ApiControllerBase
{
    private const string Module = "talent.training";

    // ── the catalogue ───────────────────────────────────────────────────────

    [HttpGet("courses")]
    [ProducesResponseType<PagedResult<CourseRowDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Courses(
        [FromQuery] CourseQuery query,
        CancellationToken cancellationToken) =>
        Ok(await learning.ListAsync(query, cancellationToken));

    [HttpGet("courses/{id:guid}")]
    [ProducesResponseType<CourseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Course(Guid id, CancellationToken cancellationToken)
    {
        CourseDto? found = await learning.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<CourseDto>(MoteeStatusCodes.NotFound, "Course not found.")
            : Ok(found);
    }

    [HttpGet("summary")]
    [ProducesResponseType<LearningSummaryDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await learning.SummaryAsync(cancellationToken));

    // Where the company is on the training it has made compulsory.
    [HttpGet("compliance")]
    [ProducesResponseType<MandatoryTally>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Compliance(CancellationToken cancellationToken) =>
        Ok(await learning.ComplianceAsync(cancellationToken));

    [HttpPost("courses")]
    [ProducesResponseType<CourseDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        CourseRequest request,
        CancellationToken cancellationToken)
    {
        CourseResult result = await learning.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Course!, "Course created.")
            : Failure<CourseDto>(StatusFor(result.Outcome), MessageFor(result));
    }

    [HttpPut("courses/{id:guid}")]
    [ProducesResponseType<CourseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        CourseRequest request,
        CancellationToken cancellationToken) =>
        Respond(await learning.UpdateAsync(id, request, cancellationToken), "Course updated.");

    [HttpDelete("courses/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        CourseResult result = await learning.DeleteAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Course deleted.")
            : Failure<object?>(StatusFor(result.Outcome), MessageFor(result));
    }

    // ── the quiz, as its author sees it ─────────────────────────────────────

    // Carries each question's correct answer, which is why it is behind the
    // module grant and why the learner's route is a different shape entirely.
    [HttpGet("courses/{id:guid}/quiz")]
    [ProducesResponseType<IReadOnlyList<QuizQuestionDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Quiz(Guid id, CancellationToken cancellationToken) =>
        Ok(await learning.QuizAsync(id, cancellationToken));

    // Replaced wholesale: while a quiz is being written a question is known by
    // its position, so there is nothing to patch.
    [HttpPut("courses/{id:guid}/quiz")]
    [ProducesResponseType<CourseDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetQuiz(
        Guid id,
        QuizRequest request,
        CancellationToken cancellationToken) =>
        Respond(await learning.SetQuizAsync(id, request, cancellationToken), "Quiz saved.");

    // ── who is on a course ──────────────────────────────────────────────────

    [HttpGet("enrollments")]
    [ProducesResponseType<PagedResult<EnrollmentDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Enrollments(
        [FromQuery] EnrollmentQuery query,
        CancellationToken cancellationToken) =>
        Ok(await learning.EnrollmentsAsync(query, cancellationToken));

    // Several people at once, which is how the assign dialog works. Somebody
    // already on the course is skipped rather than failing the whole batch.
    [HttpPost("courses/{id:guid}/enrollments")]
    [ProducesResponseType<EnrollResult>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Enroll(
        Guid id,
        EnrollRequest request,
        CancellationToken cancellationToken)
    {
        EnrollResult result = await learning.EnrollAsync(id, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result, MessageFor(result))
            : Failure<EnrollResult>(
                StatusFor(result.Outcome), ReasonFor(result.Refusal));
    }

    [HttpPatch("enrollments/{enrollmentId:guid}/progress")]
    [ProducesResponseType<EnrollmentDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetProgress(
        Guid enrollmentId,
        ProgressRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await learning.SetProgressAsync(enrollmentId, request, cancellationToken),
            "Progress saved.");

    [HttpPatch("enrollments/{enrollmentId:guid}/status")]
    [ProducesResponseType<EnrollmentDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetStatus(
        Guid enrollmentId,
        EnrollmentStatusRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await learning.SetStatusAsync(enrollmentId, request.Status, cancellationToken),
            "Enrolment updated.");

    [HttpDelete("enrollments/{enrollmentId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Withdraw(
        Guid enrollmentId,
        CancellationToken cancellationToken)
    {
        EnrollmentResult result = await learning.WithdrawAsync(
            enrollmentId, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Taken off the course.")
            : Failure<object?>(StatusFor(result.Outcome), ReasonFor(result.Refusal));
    }

    // ── the learner's own ───────────────────────────────────────────────────

    // No module grant: somebody has to be able to take the training they were
    // assigned without being given the catalogue.
    [HttpGet("mine")]
    [Authorize]
    [ProducesResponseType<MyLearningDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await learning.MineAsync(cancellationToken));

    // The questions, and nowhere to read the answers.
    [HttpGet("mine/{enrollmentId:guid}/quiz")]
    [Authorize]
    [ProducesResponseType<AskedQuizDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Asked(
        Guid enrollmentId,
        CancellationToken cancellationToken)
    {
        AskedQuizDto? asked = await learning.AskedAsync(enrollmentId, cancellationToken);

        return asked is null
            ? Failure<AskedQuizDto>(MoteeStatusCodes.NotFound, "No quiz to sit here.")
            : Ok(asked);
    }

    // Marked here. The score, whether it passed and the certificate that
    // follows are all worked out on the server.
    [HttpPost("mine/{enrollmentId:guid}/attempts")]
    [Authorize]
    [ProducesResponseType<AttemptResult>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Attempt(
        Guid enrollmentId,
        AttemptRequest request,
        CancellationToken cancellationToken)
    {
        AttemptResult result = await learning.AttemptAsync(
            enrollmentId, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(
                result,
                result.Scored!.Passed
                    ? $"Passed with {result.Scored.Score:0.#}%."
                    : $"Scored {result.Scored.Score:0.#}%, which is below the pass mark.")
            : Failure<AttemptResult>(
                StatusFor(result.Outcome), ReasonFor(result.Refusal));
    }

    private IActionResult Respond(CourseResult result, string message) =>
        result.Succeeded
            ? Ok(result.Course!, message)
            : Failure<CourseDto>(StatusFor(result.Outcome), MessageFor(result));

    private IActionResult Respond(EnrollmentResult result, string message) =>
        result.Succeeded
            ? Ok(result.Enrollment!, message)
            : Failure<EnrollmentDto>(
                StatusFor(result.Outcome), ReasonFor(result.Refusal));

    private static string MessageFor(EnrollResult result) =>
        result.Skipped.Count == 0
            ? $"{result.Enrolled.Count} enrolled."
            : $"{result.Enrolled.Count} enrolled; {result.Skipped.Count} were already on it.";

    private static string MessageFor(CourseResult result) =>
        result.Outcome switch
        {
            LearningOutcome.NotFound => "Course not found.",
            _ => ReasonFor(result.Refusal),
        };

    internal static string StatusFor(LearningOutcome outcome) => outcome switch
    {
        LearningOutcome.NotFound or LearningOutcome.EmployeeNotFound =>
            MoteeStatusCodes.NotFound,

        LearningOutcome.NotYours or LearningOutcome.NoEmployeeRecord =>
            MoteeStatusCodes.Forbidden,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    internal static string ReasonFor(LearningRefusal refusal) => refusal switch
    {
        LearningRefusal.TitleTooShort =>
            $"Give the course a title of at least {CourseRules.ShortestTitle} characters.",
        LearningRefusal.DurationNotPositive => "A course has to last longer than nothing.",
        LearningRefusal.CapacityNotPositive =>
            "A capacity of nought is a course nobody can join. Leave it empty for no limit.",
        LearningRefusal.ValidityNotPositive =>
            "Leave the validity empty for a certificate that never expires.",
        LearningRefusal.EndsBeforeStart => "The course ends before it starts.",

        LearningRefusal.QuestionPromptRequired => "Every question needs something to ask.",
        LearningRefusal.QuestionNeedsTwoOptions =>
            "Every question needs at least two answers to choose between.",
        LearningRefusal.AnswerNotAnOption =>
            "The correct answer has to be one of the options — otherwise nobody can get it "
            + "right.",
        LearningRefusal.PointsNotPositive => "A question has to be worth something.",
        LearningRefusal.PassMarkOutOfRange => "The pass mark has to be between 0 and 100.",
        LearningRefusal.NoQuestions => "Add at least one question.",

        LearningRefusal.ProgressOutOfRange => "Progress has to be between 0 and 100 per cent.",
        LearningRefusal.CourseNotOpen =>
            "Only an active course takes enrolments. Publish it first.",
        LearningRefusal.CourseFull => "That course has no places left.",
        LearningRefusal.AlreadyEnrolled => "They are already on this course.",

        LearningRefusal.NoQuiz => "This course has no quiz.",
        LearningRefusal.NoAttemptsLeft => "You have used all your attempts at this quiz.",

        LearningRefusal.CertificateTitleRequired => "Give the certificate a title.",
        LearningRefusal.ExpiresBeforeIssued =>
            "The certificate expires before it was issued.",

        _ => "Could not save that.",
    };
}

public sealed record EnrollmentStatusRequest
{
    public required EnrollmentStatus Status { get; init; }
}
