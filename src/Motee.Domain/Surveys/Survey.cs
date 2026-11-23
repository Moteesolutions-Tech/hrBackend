using Motee.Domain.Common;
using Motee.Domain.Diversity;

namespace Motee.Domain.Surveys;

public enum SurveyType
{
    Engagement,
    Pulse,
    Enps,
    Onboarding,
    Exit,
    ManagerLeadership,
}

public enum SurveyStatus
{
    Draft,
    Scheduled,
    Active,
    Closed,

    // Archived is a status, not a second flag. The client keeps `isArchived`
    // beside a status that already has this value, so the two can disagree.
    Archived,
}

public enum SurveyAudience
{
    AllStaff,
    Department,
    Managers,
}

public enum QuestionType
{
    MultipleChoice,
    Rating,
    Likert,
    OpenText,
    Nps,
    YesNo,
}

public enum PulseFrequency
{
    Weekly,
    BiWeekly,
    Monthly,
    Quarterly,
}

// A survey.
//
// Anonymity here is stronger than a flag on a payload. A respondent is always
// recorded — one reply per person has to be enforceable, and somebody has to
// be able to see their own — but on an anonymous survey the identity never
// leaves the service, and results are withheld entirely until enough people
// have answered to hide them in. The client attaches respondentName,
// respondentInitials and respondentDept to every response regardless.
public class Survey : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Title { get; set; }

    public required string Description { get; set; }

    public SurveyType SurveyType { get; set; }

    public SurveyStatus Status { get; set; } = SurveyStatus.Draft;

    public SurveyAudience Audience { get; set; }

    // Only meaningful under SurveyAudience.Department, and required there.
    public List<Guid> TargetDepartmentIds { get; set; } = [];

    public bool IsAnonymous { get; set; }

    public bool SendReminder { get; set; }

    // How many people were asked, captured when the survey opens rather than
    // counted on every read. A closed survey's response rate must not drift as
    // the company grows: the denominator is who was actually invited.
    public int? TotalTargeted { get; set; }

    public DateOnly? StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    // Only for a recurring pulse. Each occurrence is its own survey, so one
    // reply per person stays enforceable.
    public PulseFrequency? PulseFrequency { get; set; }

    public Guid CreatedByEmployeeId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public class SurveyQuestion : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SurveyId { get; set; }

    public required string Text { get; set; }

    public QuestionType QuestionType { get; set; }

    public bool Required { get; set; }

    // The choices, for a multiple-choice or Likert question. Read with the row
    // and never queried across.
    public List<string> Options { get; set; } = [];

    public int? ScaleMin { get; set; }

    public int? ScaleMax { get; set; }

    public int Sequence { get; set; }
}

public class SurveyResponse : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SurveyId { get; set; }

    // Always recorded, so one reply per person is enforceable and somebody can
    // find their own. Never disclosed on an anonymous survey.
    public Guid RespondentEmployeeId { get; set; }

    // Captured at submission rather than read back from the employee. Two
    // reasons: a transfer afterwards should not move an answer between
    // departments, and on an anonymous survey the respondent's record is not
    // something this module should be reading at all.
    public Guid? RespondentDepartmentId { get; set; }

    public DateTimeOffset SubmittedAt { get; set; }
}

public class SurveyAnswer : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ResponseId { get; set; }

    public Guid QuestionId { get; set; }

    // A list because a multiple-choice question can take several. Everything
    // else is validated down to one.
    public List<string> Values { get; set; } = [];
}

public enum SurveyRefusal
{
    None,
    TitleRequired,
    TitleTooLong,
    DescriptionTooLong,
    NoQuestions,
    TooManyQuestions,
    NoTargetDepartments,
    WindowBackwards,

    QuestionTextRequired,
    OptionsRequired,
    ScaleBackwards,

    AnswerRequired,
    AnswerOutOfRange,
    AnswerNotAnOption,
    TooManyAnswers,

    // Answering something that is not open, or answering twice.
    NotOpen,
    AlreadyAnswered,

    // Not in the audience the survey was sent to.
    NotInAudience,
}

public sealed record EnpsTally
{
    public required int Promoters { get; init; }

    public required int Passives { get; init; }

    public required int Detractors { get; init; }

    // Percentage of promoters less percentage of detractors, rounded. −100 to
    // +100.
    public required int Score { get; init; }

    public required int Answers { get; init; }
}

// One scored answer, normalised by whatever scale it was asked on.
public sealed record ScoredAnswer
{
    public required decimal Value { get; init; }

    public required decimal Min { get; init; }

    public required decimal Max { get; init; }
}

public static class SurveyRules
{
    public const int ShortestTitle = 3;

    public const int LongestTitle = 200;

    public const int LongestDescription = 2000;

    public const int LongestQuestionText = 500;

    // Enough for the longest engagement survey anybody will finish, and few
    // enough to catch a runaway import.
    public const int MostQuestions = 100;

    public const int NpsMin = 0;

    public const int NpsMax = 10;

    // Shared with the diversity module, so the product has one answer to "too
    // small a group to report". Five is the ONS threshold.
    public const int SmallestReportableGroup = Suppression.Threshold;

    public static bool IsArchived(Survey survey) => survey.Status == SurveyStatus.Archived;

    public static SurveyRefusal Refusal(
        Survey survey,
        IReadOnlyList<SurveyQuestion> questions)
    {
        string title = survey.Title?.Trim() ?? string.Empty;

        if (title.Length < ShortestTitle)
        {
            return SurveyRefusal.TitleRequired;
        }

        if (title.Length > LongestTitle)
        {
            return SurveyRefusal.TitleTooLong;
        }

        if ((survey.Description?.Trim().Length ?? 0) > LongestDescription)
        {
            return SurveyRefusal.DescriptionTooLong;
        }

        if (questions.Count == 0)
        {
            return SurveyRefusal.NoQuestions;
        }

        if (questions.Count > MostQuestions)
        {
            return SurveyRefusal.TooManyQuestions;
        }

        // A survey sent to departments without naming any reaches nobody.
        if (survey.Audience == SurveyAudience.Department
            && survey.TargetDepartmentIds.Count == 0)
        {
            return SurveyRefusal.NoTargetDepartments;
        }

        if (survey.StartDate is DateOnly start
            && survey.EndDate is DateOnly end
            && end < start)
        {
            return SurveyRefusal.WindowBackwards;
        }

        foreach (SurveyQuestion question in questions)
        {
            SurveyRefusal refusal = QuestionRefusal(question);

            if (refusal != SurveyRefusal.None)
            {
                return refusal;
            }
        }

        return SurveyRefusal.None;
    }

    public static SurveyRefusal QuestionRefusal(SurveyQuestion question)
    {
        string text = question.Text?.Trim() ?? string.Empty;

        if (text.Length == 0 || text.Length > LongestQuestionText)
        {
            return SurveyRefusal.QuestionTextRequired;
        }

        // A choice question with no choices cannot be answered.
        if (question.QuestionType is QuestionType.MultipleChoice or QuestionType.Likert
            && question.Options.Count == 0)
        {
            return SurveyRefusal.OptionsRequired;
        }

        if (question.QuestionType == QuestionType.Rating)
        {
            if (question.ScaleMin is not int min || question.ScaleMax is not int max)
            {
                return SurveyRefusal.ScaleBackwards;
            }

            if (min >= max)
            {
                return SurveyRefusal.ScaleBackwards;
            }
        }

        return SurveyRefusal.None;
    }

    // What the answer to one question may be. Checked here rather than trusted
    // from the form, because a survey is the one place where a malformed
    // answer survives as a number somebody later reports on.
    public static SurveyRefusal AnswerRefusal(
        SurveyQuestion question,
        IReadOnlyList<string> values)
    {
        List<string> given = [.. values.Where(value => !string.IsNullOrWhiteSpace(value))];

        if (given.Count == 0)
        {
            return question.Required ? SurveyRefusal.AnswerRequired : SurveyRefusal.None;
        }

        // Only a multiple choice may carry more than one.
        if (given.Count > 1 && question.QuestionType != QuestionType.MultipleChoice)
        {
            return SurveyRefusal.TooManyAnswers;
        }

        switch (question.QuestionType)
        {
            case QuestionType.Rating:
                return WithinScale(
                    given[0], question.ScaleMin ?? NpsMin, question.ScaleMax ?? NpsMax);

            case QuestionType.Nps:
                return WithinScale(given[0], NpsMin, NpsMax);

            case QuestionType.MultipleChoice:
            case QuestionType.Likert:
                return given.All(value => question.Options.Contains(value, StringComparer.Ordinal))
                    ? SurveyRefusal.None
                    : SurveyRefusal.AnswerNotAnOption;

            case QuestionType.YesNo:
                return IsYesOrNo(given[0])
                    ? SurveyRefusal.None
                    : SurveyRefusal.AnswerNotAnOption;

            default:
                return SurveyRefusal.None;
        }
    }

    private static SurveyRefusal WithinScale(string value, int min, int max) =>
        int.TryParse(value, out int number) && number >= min && number <= max
            ? SurveyRefusal.None
            : SurveyRefusal.AnswerOutOfRange;

    // People write these several ways and all of them mean the same thing.
    private static bool IsYesOrNo(string value) =>
        value.Trim().ToLowerInvariant() is "yes" or "no" or "true" or "false" or "y" or "n";

    // Captured when the survey opens and never moved afterwards.
    public static int? TargetFor(SurveyStatus status, int? was, int eligibleNow)
    {
        if (status is SurveyStatus.Draft or SurveyStatus.Scheduled)
        {
            return was;
        }

        return was ?? eligibleNow;
    }

    // Null rather than nought when nobody was asked — nought per cent reads as
    // "nobody replied", which is a different thing.
    public static decimal? ResponseRate(int responses, int? targeted)
    {
        if (targeted is not int asked || asked <= 0)
        {
            return null;
        }

        return Math.Round(responses / (decimal)asked * 100m, 1, MidpointRounding.AwayFromZero);
    }

    // Promoters nine and ten, detractors nought to six, score the difference
    // of the two percentages. Null with no answers, because nought is a real
    // score — as many promoters as detractors — and cannot also mean silence.
    public static EnpsTally? Enps(IReadOnlyList<int> scores)
    {
        List<int> valid = [.. scores.Where(score => score is >= NpsMin and <= NpsMax)];

        if (valid.Count == 0)
        {
            return null;
        }

        int promoters = valid.Count(score => score >= 9);
        int detractors = valid.Count(score => score <= 6);
        int passives = valid.Count - promoters - detractors;

        decimal promoterShare = promoters / (decimal)valid.Count * 100m;
        decimal detractorShare = detractors / (decimal)valid.Count * 100m;

        return new EnpsTally
        {
            Promoters = promoters,
            Passives = passives,
            Detractors = detractors,
            Score = (int)Math.Round(
                promoterShare - detractorShare, MidpointRounding.AwayFromZero),
            Answers = valid.Count,
        };
    }

    // From what people said, normalised to nought-to-a-hundred across whatever
    // scale each question used.
    //
    // The client falls back to the response rate for any survey without an NPS
    // question, so a survey everybody answered "terrible" scores a hundred
    // because everybody answered. Participation is not sentiment, and this is
    // the number leadership reads as "how engaged are our people".
    //
    // NPS answers are deliberately left out: eNPS is its own metric on its own
    // scale, and folding it in would make one number mean two things.
    public static decimal? EngagementScore(IReadOnlyList<ScoredAnswer> answers)
    {
        List<decimal> normalised =
        [
            .. answers
                .Where(answer => answer.Max > answer.Min)
                .Select(answer =>
                    (answer.Value - answer.Min) / (answer.Max - answer.Min) * 100m),
        ];

        return normalised.Count == 0
            ? null
            : Math.Round(normalised.Average(), 1, MidpointRounding.AwayFromZero);
    }

    public static bool NamesRespondents(Survey survey) => !survey.IsAnonymous;

    // Withholding the name is not enough on an anonymous survey. With three
    // replies out of a team of four, an average identifies people as surely as
    // a name would — so results are withheld entirely until enough have
    // answered to hide in.
    //
    // A signed survey has nothing to protect: the respondent put their name to
    // it on purpose.
    public static bool CanReport(Survey survey, int responses) =>
        !survey.IsAnonymous || responses >= SmallestReportableGroup;
}
