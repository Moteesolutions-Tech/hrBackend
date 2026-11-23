using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Authorization;
using Motee.Api.Contracts;
using Motee.Application.Common;
using Motee.Application.Surveys;
using Motee.Domain.Authorization;
using Motee.Domain.Surveys;

namespace Motee.Api.Controllers;

// Surveys.
//
// There is no route that returns one person's answers on an anonymous survey,
// and no field in any payload to put a respondent's name in. Below the
// reporting threshold the aggregates are withheld as well, with a reason the
// screen can show — a mean over three replies from a team of four identifies
// people as surely as a name.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/surveys")]
public class SurveysController(ISurveyService surveys) : ApiControllerBase
{
    private const string Module = "workspace.surveys";

    [HttpGet]
    [ProducesResponseType<PagedResult<SurveyDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> List(
        [FromQuery] SurveyQuery query,
        CancellationToken cancellationToken) =>
        Ok(await surveys.ListAsync(query, cancellationToken));

    [HttpGet("summary")]
    [ProducesResponseType<SurveyBoardDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await surveys.SummaryAsync(cancellationToken));

    // Engagement over time, from what people answered. The client ships
    // twelve months of hardcoded figures against five hardcoded department
    // names; this is empty until real surveys accumulate, which is the
    // truthful answer for a new tenant.
    [HttpGet("engagement-trend")]
    [ProducesResponseType<IReadOnlyList<EngagementPointDto>>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> EngagementTrend(
        [FromQuery] int months,
        CancellationToken cancellationToken) =>
        Ok(await surveys.EngagementTrendAsync(months <= 0 ? 12 : months, cancellationToken));

    // What is open to the signed-in employee and still unanswered. Authorize
    // only: answering a survey is not an HR function.
    [HttpGet("mine")]
    [Authorize]
    [ProducesResponseType<IReadOnlyList<SurveyDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken) =>
        Ok(await surveys.MineAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize]
    [ProducesResponseType<SurveyDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        SurveyDto? found = await surveys.GetAsync(id, cancellationToken);

        return found is null
            ? Failure<SurveyDto>(MoteeStatusCodes.NotFound, "Survey not found.")
            : Ok(found);
    }

    [HttpGet("{id:guid}/results")]
    [ProducesResponseType<SurveyResultsDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.View)]
    public async Task<IActionResult> Results(Guid id, CancellationToken cancellationToken)
    {
        SurveyResultsDto? found = await surveys.ResultsAsync(id, cancellationToken);

        return found is null
            ? Failure<SurveyResultsDto>(MoteeStatusCodes.NotFound, "Survey not found.")
            : Ok(found, found.IsReportable ? "Results." : found.Withheld!);
    }

    // Whether the caller has answered, and what they said — on a signed
    // survey. An anonymous one reports only that they answered, because
    // reading it back is a route to their identity.
    [HttpGet("{id:guid}/my-response")]
    [Authorize]
    [ProducesResponseType<MySurveyResponseDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MyResponse(Guid id, CancellationToken cancellationToken)
    {
        MySurveyResponseDto? found = await surveys.MyResponseAsync(id, cancellationToken);

        return found is null
            ? Failure<MySurveyResponseDto>(MoteeStatusCodes.NotFound, "Survey not found.")
            : Ok(found);
    }

    [HttpPost]
    [ProducesResponseType<SurveyDto>(StatusCodes.Status201Created)]
    [RequiresPermission(Module, PermissionAction.Create)]
    public async Task<IActionResult> Create(
        SurveyRequest request,
        CancellationToken cancellationToken)
    {
        SurveyResult result = await surveys.CreateAsync(request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Survey!, "Survey saved.")
            : Failure<SurveyDto>(StatusFor(result), MessageFor(result));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType<SurveyDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> Update(
        Guid id,
        SurveyRequest request,
        CancellationToken cancellationToken) =>
        Respond(await surveys.UpdateAsync(id, request, cancellationToken), "Survey saved.");

    [HttpPost("{id:guid}/status")]
    [ProducesResponseType<SurveyDto>(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Edit)]
    public async Task<IActionResult> SetStatus(
        Guid id,
        SurveyStatusRequest request,
        CancellationToken cancellationToken) =>
        Respond(
            await surveys.SetStatusAsync(id, request.Status, cancellationToken),
            request.Status switch
            {
                SurveyStatus.Active => "Survey opened.",
                SurveyStatus.Closed => "Survey closed.",
                SurveyStatus.Archived => "Survey archived.",
                SurveyStatus.Scheduled => "Survey scheduled.",
                _ => "Survey moved back to draft.",
            });

    [HttpPost("{id:guid}/responses")]
    [Authorize]
    [ProducesResponseType<SurveyDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Respond(
        Guid id,
        SurveySubmissionRequest request,
        CancellationToken cancellationToken)
    {
        SurveyResult result = await surveys.RespondAsync(id, request, cancellationToken);

        return result.Succeeded
            ? CreatedEnvelope(result.Survey!, "Thank you — your answers are in.")
            : Failure<SurveyDto>(StatusFor(result), MessageFor(result));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [RequiresPermission(Module, PermissionAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        SurveyResult result = await surveys.DeleteAsync(id, cancellationToken);

        return result.Succeeded
            ? Ok<object?>(null, "Survey deleted.")
            : Failure<object?>(StatusFor(result), MessageFor(result));
    }

    private IActionResult Respond(SurveyResult result, string message) =>
        result.Succeeded
            ? Ok(result.Survey!, message)
            : Failure<SurveyDto>(StatusFor(result), MessageFor(result));

    private static string StatusFor(SurveyResult result) => result.Outcome switch
    {
        SurveyOutcome.NotFound => MoteeStatusCodes.NotFound,
        SurveyOutcome.NoEmployeeRecord => MoteeStatusCodes.Forbidden,
        SurveyOutcome.HasResponses => MoteeStatusCodes.Conflict,

        SurveyOutcome.Refused when result.Refusal
            is SurveyRefusal.AlreadyAnswered
            or SurveyRefusal.NotOpen => MoteeStatusCodes.Conflict,

        SurveyOutcome.Refused when result.Refusal == SurveyRefusal.NotInAudience =>
            MoteeStatusCodes.Forbidden,

        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(SurveyResult result) => result.Outcome switch
    {
        SurveyOutcome.NotFound => "Survey not found.",
        SurveyOutcome.NoEmployeeRecord =>
            "Your account has no employee record, so it cannot build or answer surveys.",
        SurveyOutcome.HasResponses =>
            "People have already answered this survey, so its questions cannot be changed. "
            + "Close it and copy it instead.",
        _ => ReasonFor(result.Refusal),
    };

    private static string ReasonFor(SurveyRefusal refusal) => refusal switch
    {
        SurveyRefusal.TitleRequired =>
            $"Give the survey a title of at least {SurveyRules.ShortestTitle} characters.",
        SurveyRefusal.TitleTooLong =>
            $"Keep the title to {SurveyRules.LongestTitle} characters or fewer.",
        SurveyRefusal.DescriptionTooLong =>
            $"Keep the description to {SurveyRules.LongestDescription} characters or fewer.",
        SurveyRefusal.NoQuestions => "A survey needs at least one question.",
        SurveyRefusal.TooManyQuestions =>
            $"Use {SurveyRules.MostQuestions} questions at most — nobody finishes more.",
        SurveyRefusal.NoTargetDepartments =>
            "Choose which departments this goes to, or send it to all staff.",
        SurveyRefusal.WindowBackwards => "The closing date is before the opening date.",
        SurveyRefusal.QuestionTextRequired => "Every question needs wording.",
        SurveyRefusal.OptionsRequired =>
            "A multiple-choice or Likert question needs its options.",
        SurveyRefusal.ScaleBackwards =>
            "A rating question needs a scale, with the lowest value below the highest.",
        SurveyRefusal.AnswerRequired => "Please answer every required question.",
        SurveyRefusal.AnswerOutOfRange => "One of the answers is outside the scale asked.",
        SurveyRefusal.AnswerNotAnOption => "One of the answers is not one of the choices.",
        SurveyRefusal.TooManyAnswers =>
            "Only a multiple-choice question takes more than one answer.",
        SurveyRefusal.NotOpen => "This survey is not open for answers.",
        SurveyRefusal.AlreadyAnswered => "You have already answered this survey.",
        SurveyRefusal.NotInAudience => "This survey was not sent to you.",
        _ => "Could not save the survey.",
    };
}

public sealed record SurveyStatusRequest
{
    public required SurveyStatus Status { get; init; }
}
