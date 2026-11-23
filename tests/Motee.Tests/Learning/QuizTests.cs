using Motee.Domain.Learning;

namespace Motee.Tests.Learning;

// Marking an end-of-course quiz.
//
// This is graded here because it cannot be graded anywhere else. The client
// scores the attempt in the learner's own browser, comparing each answer
// against the question's correctIndex — which means the answers to every
// mandatory compliance quiz are in the payload the learner receives. A DPIA or
// health-and-safety certificate earned that way is evidence of nothing.
//
// Written before the marking.
public class QuizTests
{
    private static CourseQuestion Question(
        string prompt = "Who do you report a data breach to?",
        int correctIndex = 0,
        int points = 1,
        int sequence = 1,
        QuestionKind kind = QuestionKind.MultipleChoice) => new()
        {
            Id = Guid.NewGuid(),
            Sequence = sequence,
            QuestionKind = kind,
            Prompt = prompt,
            Options = kind == QuestionKind.TrueFalse
                ? ["True", "False"]
                : ["The DPO", "Your line manager", "Nobody", "The client"],
            CorrectIndex = correctIndex,
            Points = points,
        };

    // ── marking ─────────────────────────────────────────────────────────────

    [Fact]
    public void EveryAnswerRightIsFullMarks()
    {
        CourseQuestion first = Question(correctIndex: 0);
        CourseQuestion second = Question(correctIndex: 2, sequence: 2);

        ScoredAttempt scored = Quizzes.Grade(
            [first, second],
            new Dictionary<Guid, int> { [first.Id] = 0, [second.Id] = 2 },
            passingScore: 80);

        Assert.Equal(100m, scored.Score);
        Assert.True(scored.Passed);
        Assert.Equal(2, scored.Correct);
        Assert.Equal(2, scored.Questions);
    }

    [Fact]
    public void EveryAnswerWrongIsNothing()
    {
        CourseQuestion only = Question(correctIndex: 0);

        ScoredAttempt scored = Quizzes.Grade(
            [only],
            new Dictionary<Guid, int> { [only.Id] = 1 },
            passingScore: 50);

        Assert.Equal(0m, scored.Score);
        Assert.False(scored.Passed);
    }

    // Weighted by the points on each question, not by how many there are: a
    // quiz where the one question that matters is worth ten cannot be passed by
    // getting the other nine right.
    [Fact]
    public void TheScoreIsWeightedByPointsRatherThanByCount()
    {
        CourseQuestion heavy = Question(correctIndex: 0, points: 9);
        CourseQuestion light = Question(correctIndex: 0, points: 1, sequence: 2);

        ScoredAttempt missedTheHeavyOne = Quizzes.Grade(
            [heavy, light],
            new Dictionary<Guid, int> { [heavy.Id] = 1, [light.Id] = 0 },
            passingScore: 50);

        Assert.Equal(10m, missedTheHeavyOne.Score);
        Assert.False(missedTheHeavyOne.Passed);

        ScoredAttempt gotTheHeavyOne = Quizzes.Grade(
            [heavy, light],
            new Dictionary<Guid, int> { [heavy.Id] = 0, [light.Id] = 1 },
            passingScore: 50);

        Assert.Equal(90m, gotTheHeavyOne.Score);
        Assert.True(gotTheHeavyOne.Passed);
    }

    // A question left blank is wrong, not skipped: the alternative is passing a
    // quiz by answering only what you know.
    [Fact]
    public void AnUnansweredQuestionScoresNothing()
    {
        CourseQuestion answered = Question(correctIndex: 0);
        CourseQuestion blank = Question(correctIndex: 0, sequence: 2);

        ScoredAttempt scored = Quizzes.Grade(
            [answered, blank],
            new Dictionary<Guid, int> { [answered.Id] = 0 },
            passingScore: 80);

        Assert.Equal(50m, scored.Score);
        Assert.False(scored.Passed);
    }

    // An answer naming a question that is not on this quiz is ignored rather
    // than counted — a stale attempt against an edited quiz must not score.
    [Fact]
    public void AnAnswerToAQuestionThatIsNotOnTheQuizCountsForNothing()
    {
        CourseQuestion only = Question(correctIndex: 0);

        ScoredAttempt scored = Quizzes.Grade(
            [only],
            new Dictionary<Guid, int> { [Guid.NewGuid()] = 0 },
            passingScore: 50);

        Assert.Equal(0m, scored.Score);
    }

    // An option index outside the question's own options is wrong, not an
    // error: a hand-built request should fail the quiz, not the server.
    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void AnOptionThatDoesNotExistIsSimplyWrong(int chosen)
    {
        CourseQuestion only = Question(correctIndex: 0);

        ScoredAttempt scored = Quizzes.Grade(
            [only],
            new Dictionary<Guid, int> { [only.Id] = chosen },
            passingScore: 50);

        Assert.Equal(0m, scored.Score);
    }

    [Fact]
    public void ExactlyThePassMarkPasses()
    {
        CourseQuestion first = Question(correctIndex: 0);
        CourseQuestion second = Question(correctIndex: 0, sequence: 2);

        ScoredAttempt scored = Quizzes.Grade(
            [first, second],
            new Dictionary<Guid, int> { [first.Id] = 0 },
            passingScore: 50);

        Assert.Equal(50m, scored.Score);
        Assert.True(scored.Passed);
    }

    [Fact]
    public void AQuizWithNoQuestionsCannotBeMarked()
    {
        ScoredAttempt scored = Quizzes.Grade([], new Dictionary<Guid, int>(), 50);

        Assert.Equal(0m, scored.Score);
        Assert.False(scored.Passed);
        Assert.Equal(0, scored.Questions);
    }

    [Fact]
    public void AQuizOfWorthlessQuestionsIsNotDividedByNothing()
    {
        // Every question worth nought. Not a quiz anybody should build, and
        // not a reason to divide by zero.
        CourseQuestion only = Question(correctIndex: 0, points: 0);

        ScoredAttempt scored = Quizzes.Grade(
            [only], new Dictionary<Guid, int> { [only.Id] = 0 }, 50);

        Assert.Equal(0m, scored.Score);
    }

    [Fact]
    public void RoundsToOneDecimalSoAThirdIsNotReportedAsAThird()
    {
        IReadOnlyList<CourseQuestion> three =
        [
            Question(correctIndex: 0, sequence: 1),
            Question(correctIndex: 0, sequence: 2),
            Question(correctIndex: 0, sequence: 3),
        ];

        ScoredAttempt scored = Quizzes.Grade(
            three, new Dictionary<Guid, int> { [three[0].Id] = 0 }, 50);

        Assert.Equal(33.3m, scored.Score);
    }

    // ── how many goes ───────────────────────────────────────────────────────

    [Fact]
    public void AQuizWithNoLimitCanBeRetakenForever()
    {
        Assert.True(Quizzes.CanAttempt(maxAttempts: null, used: 40));
    }

    [Fact]
    public void AQuizWithALimitRunsOut()
    {
        Assert.True(Quizzes.CanAttempt(maxAttempts: 3, used: 2));
        Assert.False(Quizzes.CanAttempt(maxAttempts: 3, used: 3));
        Assert.False(Quizzes.CanAttempt(maxAttempts: 3, used: 4));
    }

    [Fact]
    public void AttemptsLeftIsReportedSoTheLearnerKnows()
    {
        Assert.Equal(1, Quizzes.AttemptsLeft(maxAttempts: 3, used: 2));
        Assert.Equal(0, Quizzes.AttemptsLeft(maxAttempts: 3, used: 5));
        Assert.Null(Quizzes.AttemptsLeft(maxAttempts: null, used: 5));
    }

    // ── building one ────────────────────────────────────────────────────────

    [Fact]
    public void AcceptsAQuiz()
    {
        Assert.Equal(
            LearningRefusal.None,
            Quizzes.Refusal([Question()], passingScore: 70));
    }

    [Fact]
    public void RefusesAQuestionWithNoPrompt()
    {
        Assert.Equal(
            LearningRefusal.QuestionPromptRequired,
            Quizzes.Refusal([Question(prompt: "  ")], passingScore: 70));
    }

    [Fact]
    public void RefusesAQuestionWithNothingToChooseBetween()
    {
        CourseQuestion lonely = Question();
        lonely.Options = ["The DPO"];

        Assert.Equal(
            LearningRefusal.QuestionNeedsTwoOptions,
            Quizzes.Refusal([lonely], passingScore: 70));
    }

    // A correct answer pointing past the options is a question nobody can
    // ever get right.
    [Fact]
    public void RefusesAQuestionWhoseAnswerIsNotOneOfItsOptions()
    {
        Assert.Equal(
            LearningRefusal.AnswerNotAnOption,
            Quizzes.Refusal([Question(correctIndex: 9)], passingScore: 70));
    }

    [Fact]
    public void RefusesAQuestionWorthNothing()
    {
        Assert.Equal(
            LearningRefusal.PointsNotPositive,
            Quizzes.Refusal([Question(points: 0)], passingScore: 70));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void RefusesAPassMarkThatIsNotAPercentage(int passingScore)
    {
        Assert.Equal(
            LearningRefusal.PassMarkOutOfRange,
            Quizzes.Refusal([Question()], passingScore));
    }

    [Fact]
    public void ATrueFalseQuestionHasTwoOptions()
    {
        Assert.Equal(
            LearningRefusal.None,
            Quizzes.Refusal(
                [Question(kind: QuestionKind.TrueFalse, correctIndex: 1)],
                passingScore: 100));
    }

    [Fact]
    public void FoldsOptionsThatDifferOnlyByCaseOrSpacing()
    {
        // Two options worded the same make a question with two right answers.
        Assert.Equal(
            ["the dpo", "your line manager"],
            Quizzes.NormaliseOptions(["The DPO", " the dpo ", "Your line manager", "  "]));
    }

    // ── what the learner is shown ───────────────────────────────────────────

    // The one thing that must never leave the server. The client puts it in the
    // learner's payload and then marks the attempt against it in the browser.
    [Fact]
    public void TheAnswersAreNeverPartOfWhatALearnerIsGiven()
    {
        CourseQuestion question = Question(correctIndex: 2);

        AskedQuestion asked = Quizzes.AsAsked(question);

        Assert.Equal(question.Id, asked.Id);
        Assert.Equal(question.Prompt, asked.Prompt);
        Assert.Equal(question.Options, asked.Options);
        Assert.Equal(question.Points, asked.Points);

        // There is nowhere on this shape to put the answer, which is the
        // point: it cannot be leaked by forgetting to strip it.
        Assert.DoesNotContain(
            typeof(AskedQuestion).GetProperties(),
            property => property.Name.Contains("Correct", StringComparison.Ordinal));
    }
}
