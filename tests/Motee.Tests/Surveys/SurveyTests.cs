using System.Text.Json;
using System.Text.Json.Serialization;
using Motee.Domain.Diversity;
using Motee.Domain.Surveys;

namespace Motee.Tests.Surveys;

// Surveys.
//
// Three problems in the client's model that these pin down. Responses carry
// respondentName, respondentInitials and respondentDept whether or not the
// survey is anonymous. The engagement score falls back to the response rate
// for any survey without an NPS question, so a survey everybody answered
// "terrible" scores 100 because everybody answered. And the trend chart is
// twelve months of hardcoded figures against five hardcoded department names.
public class SurveyTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private const string Title = "Spring engagement survey";

    private const string Description = "Ten minutes on how the last quarter went.";

    private static Survey Survey(
        string title = Title,
        string description = Description,
        SurveyType type = SurveyType.Engagement,
        SurveyStatus status = SurveyStatus.Draft,
        SurveyAudience audience = SurveyAudience.AllStaff,
        bool anonymous = false,
        IEnumerable<Guid>? departments = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            SurveyType = type,
            Status = status,
            Audience = audience,
            TargetDepartmentIds = [.. departments ?? []],
            IsAnonymous = anonymous,
            CreatedByEmployeeId = Guid.NewGuid(),
        };

    private static SurveyQuestion Question(
        QuestionType type = QuestionType.Rating,
        string text = "How supported do you feel by your manager?",
        bool required = true,
        IEnumerable<string>? options = null,
        int? scaleMin = 1,
        int? scaleMax = 5) =>
        new()
        {
            Id = Guid.NewGuid(),
            Text = text,
            QuestionType = type,
            Required = required,
            Options = [.. options ?? []],
            ScaleMin = scaleMin,
            ScaleMax = scaleMax,
        };

    // ── the wire shape ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(SurveyType.ManagerLeadership, "\"managerLeadership\"")]
    [InlineData(SurveyType.Enps, "\"enps\"")]
    [InlineData(QuestionType.MultipleChoice, "\"multipleChoice\"")]
    [InlineData(QuestionType.OpenText, "\"openText\"")]
    [InlineData(SurveyAudience.AllStaff, "\"allStaff\"")]
    [InlineData(PulseFrequency.BiWeekly, "\"biWeekly\"")]
    public void SerialisesAsCamelCase(object value, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(value, Web));
    }

    [Fact]
    public void CarriesEveryTypeTheClientOffers()
    {
        Assert.Equal(6, Enum.GetValues<SurveyType>().Length);
        Assert.Equal(6, Enum.GetValues<QuestionType>().Length);
        Assert.Equal(5, Enum.GetValues<SurveyStatus>().Length);
    }

    // The client keeps `isArchived` beside a status that already has an
    // archived value, so the two can disagree.
    [Fact]
    public void ArchivedIsAStatusRatherThanASecondFlag()
    {
        Assert.True(SurveyRules.IsArchived(Survey(status: SurveyStatus.Archived)));
        Assert.False(SurveyRules.IsArchived(Survey(status: SurveyStatus.Closed)));
    }

    // ── the survey itself ───────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Q1")]
    public void RefusesATitleTooShortToRecognise(string title)
    {
        Assert.Equal(
            SurveyRefusal.TitleRequired,
            SurveyRules.Refusal(Survey(title: title), [Question()]));
    }

    [Fact]
    public void RefusesASurveyWithNoQuestions()
    {
        Assert.Equal(SurveyRefusal.NoQuestions, SurveyRules.Refusal(Survey(), []));
    }

    // Targeting departments without naming any is a survey nobody receives.
    [Fact]
    public void RefusesADepartmentSurveyWithNoDepartments()
    {
        Assert.Equal(
            SurveyRefusal.NoTargetDepartments,
            SurveyRules.Refusal(
                Survey(audience: SurveyAudience.Department, departments: []),
                [Question()]));
    }

    [Fact]
    public void AcceptsADepartmentSurveyThatNamesOne()
    {
        Assert.Equal(
            SurveyRefusal.None,
            SurveyRules.Refusal(
                Survey(audience: SurveyAudience.Department, departments: [Guid.NewGuid()]),
                [Question()]));
    }

    [Fact]
    public void RefusesAWindowThatEndsBeforeItStarts()
    {
        Survey survey = Survey();

        survey.StartDate = new DateOnly(2026, 6, 1);
        survey.EndDate = new DateOnly(2026, 5, 1);

        Assert.Equal(SurveyRefusal.WindowBackwards, SurveyRules.Refusal(survey, [Question()]));
    }

    // ── questions ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(QuestionType.MultipleChoice)]
    [InlineData(QuestionType.Likert)]
    public void RefusesAChoiceQuestionWithNoOptions(QuestionType type)
    {
        Assert.Equal(
            SurveyRefusal.OptionsRequired,
            SurveyRules.QuestionRefusal(Question(type: type, options: [])));
    }

    [Fact]
    public void AcceptsAChoiceQuestionWithOptions()
    {
        Assert.Equal(
            SurveyRefusal.None,
            SurveyRules.QuestionRefusal(
                Question(type: QuestionType.MultipleChoice, options: ["Yes", "No", "Unsure"])));
    }

    [Fact]
    public void RefusesARatingWithABackwardsScale()
    {
        Assert.Equal(
            SurveyRefusal.ScaleBackwards,
            SurveyRules.QuestionRefusal(Question(scaleMin: 5, scaleMax: 1)));
    }

    [Fact]
    public void RefusesAQuestionWithNoText()
    {
        Assert.Equal(
            SurveyRefusal.QuestionTextRequired,
            SurveyRules.QuestionRefusal(Question(text: "  ")));
    }

    // Open text and yes/no need neither options nor a scale.
    [Theory]
    [InlineData(QuestionType.OpenText)]
    [InlineData(QuestionType.YesNo)]
    public void AcceptsAQuestionThatNeedsNeitherOptionsNorAScale(QuestionType type)
    {
        Assert.Equal(
            SurveyRefusal.None,
            SurveyRules.QuestionRefusal(
                Question(type: type, options: [], scaleMin: null, scaleMax: null)));
    }

    // ── answers ─────────────────────────────────────────────────────────────

    [Fact]
    public void RefusesAnEmptyAnswerToARequiredQuestion()
    {
        Assert.Equal(
            SurveyRefusal.AnswerRequired,
            SurveyRules.AnswerRefusal(Question(required: true), []));
    }

    [Fact]
    public void AcceptsAnEmptyAnswerToAnOptionalQuestion()
    {
        Assert.Equal(
            SurveyRefusal.None,
            SurveyRules.AnswerRefusal(Question(required: false), []));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("not a number")]
    public void RefusesARatingOutsideItsScale(string value)
    {
        Assert.Equal(
            SurveyRefusal.AnswerOutOfRange,
            SurveyRules.AnswerRefusal(Question(scaleMin: 1, scaleMax: 5), [value]));
    }

    [Fact]
    public void AcceptsARatingInsideItsScale()
    {
        Assert.Equal(
            SurveyRefusal.None,
            SurveyRules.AnswerRefusal(Question(scaleMin: 1, scaleMax: 5), ["4"]));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("11")]
    public void RefusesAnNpsAnswerOutsideZeroToTen(string value)
    {
        Assert.Equal(
            SurveyRefusal.AnswerOutOfRange,
            SurveyRules.AnswerRefusal(
                Question(type: QuestionType.Nps, scaleMin: null, scaleMax: null), [value]));
    }

    [Fact]
    public void RefusesAChoiceThatIsNotOnTheList()
    {
        Assert.Equal(
            SurveyRefusal.AnswerNotAnOption,
            SurveyRules.AnswerRefusal(
                Question(type: QuestionType.MultipleChoice, options: ["Yes", "No"]),
                ["Maybe"]));
    }

    [Fact]
    public void AcceptsAChoiceOnTheList()
    {
        Assert.Equal(
            SurveyRefusal.None,
            SurveyRules.AnswerRefusal(
                Question(type: QuestionType.MultipleChoice, options: ["Yes", "No"]),
                ["No"]));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("No")]
    [InlineData("TRUE")]
    public void AcceptsTheWaysPeopleWriteYesAndNo(string value)
    {
        Assert.Equal(
            SurveyRefusal.None,
            SurveyRules.AnswerRefusal(Question(type: QuestionType.YesNo), [value]));
    }

    [Fact]
    public void RefusesSomethingThatIsNeitherYesNorNo()
    {
        Assert.Equal(
            SurveyRefusal.AnswerNotAnOption,
            SurveyRules.AnswerRefusal(Question(type: QuestionType.YesNo), ["sometimes"]));
    }

    // A single-answer question given two answers is a client bug, and taking
    // the first would hide it.
    [Fact]
    public void RefusesTwoAnswersToASingleAnswerQuestion()
    {
        Assert.Equal(
            SurveyRefusal.TooManyAnswers,
            SurveyRules.AnswerRefusal(Question(scaleMin: 1, scaleMax: 5), ["3", "4"]));
    }

    // ── who was asked ───────────────────────────────────────────────────────

    // Captured when the survey opens, not derived on every read. A closed
    // survey's response rate must not drift as people join and leave — the
    // denominator is who was actually invited.
    [Fact]
    public void TheTargetIsCapturedWhenTheSurveyOpens()
    {
        Assert.Equal(
            40,
            SurveyRules.TargetFor(SurveyStatus.Active, was: null, eligibleNow: 40));
    }

    [Fact]
    public void ReopeningDoesNotMoveTheTargetThatWasCaptured()
    {
        Assert.Equal(
            40,
            SurveyRules.TargetFor(SurveyStatus.Active, was: 40, eligibleNow: 55));
    }

    [Fact]
    public void ADraftHasNoCapturedTargetYet()
    {
        Assert.Null(SurveyRules.TargetFor(SurveyStatus.Draft, was: null, eligibleNow: 40));
    }

    // ── the response rate ───────────────────────────────────────────────────

    [Fact]
    public void TheResponseRateIsRepliesOverThoseAsked()
    {
        Assert.Equal(25m, SurveyRules.ResponseRate(responses: 10, targeted: 40));
    }

    // Not nought per cent, which reads as "nobody replied". Nobody was asked.
    [Fact]
    public void ASurveyNobodyWasAskedHasNoResponseRate()
    {
        Assert.Null(SurveyRules.ResponseRate(responses: 0, targeted: null));
        Assert.Null(SurveyRules.ResponseRate(responses: 0, targeted: 0));
    }

    [Fact]
    public void RoundsTheResponseRateToOneDecimal()
    {
        Assert.Equal(33.3m, SurveyRules.ResponseRate(responses: 1, targeted: 3));
    }

    // ── eNPS ────────────────────────────────────────────────────────────────

    // Promoters nine and ten, detractors nought to six, score the difference
    // of the two percentages. The client's own breakdown, which is also the
    // standard one.
    [Fact]
    public void EnpsIsTheShareOfPromotersLessTheShareOfDetractors()
    {
        EnpsTally tally = SurveyRules.Enps([10, 9, 8, 7, 6, 0])!;

        Assert.Equal(2, tally.Promoters);
        Assert.Equal(2, tally.Passives);
        Assert.Equal(2, tally.Detractors);
        Assert.Equal(0, tally.Score);
    }

    [Fact]
    public void EnpsIsAHundredWhenEverybodyIsAPromoter()
    {
        Assert.Equal(100, SurveyRules.Enps([9, 10, 10])!.Score);
    }

    [Fact]
    public void EnpsIsMinusAHundredWhenEverybodyIsADetractor()
    {
        Assert.Equal(-100, SurveyRules.Enps([0, 3, 6])!.Score);
    }

    // The client returns nought here, which is a real score — equal promoters
    // and detractors — and so cannot also mean "nobody answered".
    [Fact]
    public void ASurveyWithNoNpsAnswersHasNoEnpsRatherThanZero()
    {
        Assert.Null(SurveyRules.Enps([]));
    }

    // ── the engagement score ────────────────────────────────────────────────

    // The client falls back to the response rate for any survey without an NPS
    // question, so a survey everybody answered "terrible" scores 100 because
    // everybody answered. Participation is not sentiment.
    [Fact]
    public void TheEngagementScoreComesFromTheAnswersNotTheTurnout()
    {
        // Bottom of a one-to-five scale, from everybody who was asked.
        IReadOnlyList<ScoredAnswer> awful =
            [.. Enumerable.Repeat(new ScoredAnswer { Value = 1, Min = 1, Max = 5 }, 10)];

        Assert.Equal(0m, SurveyRules.EngagementScore(awful));
    }

    [Fact]
    public void TheEngagementScoreNormalisesEveryScaleToAHundred()
    {
        Assert.Equal(
            100m,
            SurveyRules.EngagementScore([new ScoredAnswer { Value = 5, Min = 1, Max = 5 }]));

        Assert.Equal(
            50m,
            SurveyRules.EngagementScore([new ScoredAnswer { Value = 3, Min = 1, Max = 5 }]));

        // A seven-point scale answered in the middle is the same fifty.
        Assert.Equal(
            50m,
            SurveyRules.EngagementScore([new ScoredAnswer { Value = 4, Min = 1, Max = 7 }]));
    }

    [Fact]
    public void ASurveyWithNothingScorableHasNoEngagementScore()
    {
        Assert.Null(SurveyRules.EngagementScore([]));
    }

    // A scale with no width cannot be normalised, and dividing by it would
    // throw rather than report nothing.
    [Fact]
    public void AScaleOfOneValueIsNotScored()
    {
        Assert.Null(
            SurveyRules.EngagementScore([new ScoredAnswer { Value = 3, Min = 3, Max = 3 }]));
    }

    // ── anonymity ───────────────────────────────────────────────────────────

    [Fact]
    public void ASignedSurveyNamesItsRespondents()
    {
        Assert.True(SurveyRules.NamesRespondents(Survey(anonymous: false)));
    }

    // Nobody is exempt. Survey answers are the most candid thing an employee
    // gives an employer, and a box that can be opened by whoever is senior
    // enough is not anonymous.
    [Fact]
    public void AnAnonymousSurveyNamesNobody()
    {
        Assert.False(SurveyRules.NamesRespondents(Survey(anonymous: true)));
    }

    // ── small groups ────────────────────────────────────────────────────────

    // Withholding the name is not enough on an anonymous survey: with three
    // replies from a team of four, the averages identify people. The threshold
    // is the one the diversity module already uses, so the product has one
    // answer to "too small to report".
    [Fact]
    public void AGroupTooSmallToReportIsWithheldOnAnAnonymousSurvey()
    {
        Assert.False(SurveyRules.CanReport(
            Survey(anonymous: true), responses: Suppression.Threshold - 1));

        Assert.True(SurveyRules.CanReport(
            Survey(anonymous: true), responses: Suppression.Threshold));
    }

    // A signed survey has nothing to protect: the respondent put their name to
    // it, so two replies are reportable.
    [Fact]
    public void ASignedSurveyReportsHoweverFewRepliedToIt()
    {
        Assert.True(SurveyRules.CanReport(Survey(anonymous: false), responses: 2));
    }

    [Fact]
    public void TheThresholdIsSharedWithTheDiversityModule()
    {
        Assert.Equal(Suppression.Threshold, SurveyRules.SmallestReportableGroup);
    }
}
