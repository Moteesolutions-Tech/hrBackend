using Motee.Domain.Common;

namespace Motee.Domain.Learning;

public enum CourseCategory
{
    Technical,
    Leadership,
    Compliance,
    SoftSkills,
    Onboarding,
    Product,
}

public enum CourseStatus
{
    Draft,
    Active,
    Archived,
}

public enum DeliveryMode
{
    Online,
    InPerson,
    Hybrid,
}

public enum EnrollmentStatus
{
    NotAttempted,
    Enrolled,
    InProgress,
    Completed,
    Dropped,
    Failed,
}

public enum QuestionKind
{
    MultipleChoice,
    TrueFalse,
}

public enum CertStatus
{
    NoExpiry,
    Active,
    ExpiringInSixty,
    ExpiringInThirty,
    Expired,
}

// An external credential against a completion certificate the product issued
// itself.
public enum CertificationKind
{
    // PMP, ACCA, IOSH — awarded elsewhere, recorded here.
    Professional,

    // Earned by completing a course in this system.
    Course,
}

// A course in the catalogue.
//
// How many are enrolled and how many finished are counted from the enrolments
// rather than kept here: a stored count drifts the first time somebody is
// dropped from a course, and the figure is on the screen next to the list it
// disagrees with.
public class Course : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public CourseCategory Category { get; set; } = CourseCategory.Technical;

    public CourseStatus Status { get; set; } = CourseStatus.Draft;

    public DeliveryMode DeliveryMode { get; set; } = DeliveryMode.Online;

    public string? Instructor { get; set; }

    // The accrediting body — ICO, FCA, IOSH, CompTIA. What makes a completion
    // certificate credible to an auditor.
    public string? Provider { get; set; }

    // Every current employee must complete it.
    public bool IsMandatory { get; set; }

    // How long a completion certificate stays valid. Null never expires, and
    // the certificate's own expiry is worked out from this rather than typed.
    public int? ValidityMonths { get; set; }

    public string? CourseUrl { get; set; }

    public string? VideoUrl { get; set; }

    public decimal DurationHours { get; set; }

    // Places available. Null is unlimited.
    public int? Capacity { get; set; }

    public DateOnly? StartsOn { get; set; }

    public DateOnly? EndsOn { get; set; }

    public List<string> Tags { get; set; } = [];

    // The end-of-course quiz, where there is one. The questions are their own
    // rows; what lives here is how it is marked.
    public bool HasQuiz { get; set; }

    public int PassingScore { get; set; } = 70;

    // Null is unlimited.
    public int? MaxAttempts { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One question on a course's quiz.
//
// The correct answer lives here and is never put on the shape a learner is
// given — see AskedQuestion. The client sends it to the browser and marks the
// attempt there, which makes every compliance certificate it issues worthless.
public class CourseQuestion : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CourseId { get; set; }

    public int Sequence { get; set; }

    public QuestionKind QuestionKind { get; set; } = QuestionKind.MultipleChoice;

    public required string Prompt { get; set; }

    // For a true/false question these are "True" and "False".
    public List<string> Options { get; set; } = [];

    // Index into Options.
    public int CorrectIndex { get; set; }

    // What this question is worth. The score is weighted by it, so a quiz
    // where the one question that matters carries ten points cannot be passed
    // by getting the other nine right.
    public int Points { get; set; } = 1;
}

// Somebody on a course.
public class CourseEnrollment : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CourseId { get; set; }

    public Guid EmployeeId { get; set; }

    public EnrollmentStatus Status { get; set; } = EnrollmentStatus.Enrolled;

    // 0–100, and tied to the status: completed work is whole, and whole work is
    // completed.
    public int Progress { get; set; }

    // Where the learner got to in the video, so it resumes rather than
    // restarting.
    public int? ResumeSeconds { get; set; }

    public DateOnly EnrolledOn { get; set; }

    public DateOnly? DueOn { get; set; }

    public DateOnly? CompletedOn { get; set; }

    public Guid? EnrolledByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One go at a course's quiz.
//
// Marked on the way in and kept, so the score on an enrolment is read from the
// attempts rather than stored beside them.
public class QuizAttempt : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EnrollmentId { get; set; }

    public decimal Score { get; set; }

    public bool Passed { get; set; }

    public int Correct { get; set; }

    public int Questions { get; set; }

    public DateTimeOffset AttemptedAt { get; set; }
}

// A certificate: one the product issued for a completion, or an external
// credential recorded against somebody.
public class Certification : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    // Set when this is a completion certificate.
    public Guid? CourseId { get; set; }

    public CertificationKind CertificationKind { get; set; } =
        CertificationKind.Professional;

    public required string Title { get; set; }

    public string? IssuingBody { get; set; }

    public string? Category { get; set; }

    public string? CredentialId { get; set; }

    public DateOnly IssuedOn { get; set; }

    // For a completion certificate this is worked out from the course's
    // validity, not typed. Null never expires.
    public DateOnly? ExpiresOn { get; set; }

    // The evidence, where one was uploaded.
    public Guid? StoredFileId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum LearningRefusal
{
    None,
    TitleTooShort,
    DurationNotPositive,
    CapacityNotPositive,
    EndsBeforeStart,
    ValidityNotPositive,

    QuestionPromptRequired,
    QuestionNeedsTwoOptions,
    AnswerNotAnOption,
    PointsNotPositive,
    PassMarkOutOfRange,
    NoQuestions,

    ProgressOutOfRange,
    CompletedMustBeWhole,
    WholeMustBeCompleted,

    AlreadyEnrolled,
    CourseFull,
    CourseNotOpen,

    NoQuiz,
    NoAttemptsLeft,

    CertificateTitleRequired,
    ExpiresBeforeIssued,

    UnknownCourse,
    UnknownEnrollment,
}

// A marked attempt.
public sealed record ScoredAttempt
{
    // 0–100, weighted by each question's points.
    public required decimal Score { get; init; }

    public required bool Passed { get; init; }

    public required int Correct { get; init; }

    public required int Questions { get; init; }
}

// A question as a learner is given it.
//
// There is deliberately nowhere on this shape to put the answer, so it cannot
// be leaked by somebody forgetting to strip it.
public sealed record AskedQuestion
{
    public required Guid Id { get; init; }

    public required int Sequence { get; init; }

    public required QuestionKind QuestionKind { get; init; }

    public required string Prompt { get; init; }

    public required IReadOnlyList<string> Options { get; init; }

    public required int Points { get; init; }
}

public static class CourseRules
{
    public const int ShortestTitle = 3;

    public static LearningRefusal Refusal(Course course)
    {
        if ((course.Title ?? string.Empty).Trim().Length < ShortestTitle)
        {
            return LearningRefusal.TitleTooShort;
        }

        if (course.DurationHours <= 0m)
        {
            return LearningRefusal.DurationNotPositive;
        }

        if (course.Capacity is int capacity && capacity <= 0)
        {
            return LearningRefusal.CapacityNotPositive;
        }

        if (course.ValidityMonths is int months && months <= 0)
        {
            return LearningRefusal.ValidityNotPositive;
        }

        return course.StartsOn is DateOnly starts
            && course.EndsOn is DateOnly ends
            && ends < starts
            ? LearningRefusal.EndsBeforeStart
            : LearningRefusal.None;
    }

    // Progress moved, so the status follows it. The same pair as a project
    // task: completed work is whole, and whole work is completed.
    public static void Progressed(CourseEnrollment enrollment, int percent, DateOnly today)
    {
        enrollment.Progress = Math.Clamp(percent, 0, 100);

        // Somebody taken off a course does not come back because a video
        // reported its position.
        if (enrollment.Status == EnrollmentStatus.Dropped)
        {
            return;
        }

        if (enrollment.Progress == 100)
        {
            enrollment.Status = EnrollmentStatus.Completed;
            enrollment.CompletedOn ??= today;
            return;
        }

        if (enrollment.Status == EnrollmentStatus.Completed)
        {
            enrollment.Status = EnrollmentStatus.InProgress;
            enrollment.CompletedOn = null;
            return;
        }

        if (enrollment.Progress > 0
            && enrollment.Status is EnrollmentStatus.Enrolled
                or EnrollmentStatus.NotAttempted)
        {
            enrollment.Status = EnrollmentStatus.InProgress;
        }
    }

    // The status moved, so progress follows it.
    public static void Moved(
        CourseEnrollment enrollment,
        EnrollmentStatus status,
        DateOnly today)
    {
        EnrollmentStatus was = enrollment.Status;

        enrollment.Status = status;

        if (status == EnrollmentStatus.Completed)
        {
            enrollment.Progress = 100;
            enrollment.CompletedOn ??= today;
            return;
        }

        enrollment.CompletedOn = null;

        // Reopened. How much is left is unknown, so it reads as nearly done
        // rather than whole — which is the pair this exists to prevent.
        if (was == EnrollmentStatus.Completed)
        {
            enrollment.Progress = Math.Min(enrollment.Progress, 99);
        }
    }
}

public static class Quizzes
{
    public static AskedQuestion AsAsked(CourseQuestion question) => new()
    {
        Id = question.Id,
        Sequence = question.Sequence,
        QuestionKind = question.QuestionKind,
        Prompt = question.Prompt,
        Options = [.. question.Options],
        Points = question.Points,
    };

    // Marked here because it cannot be marked anywhere else. The client scores
    // the attempt in the learner's own browser against the correct index it was
    // sent, which makes a compliance certificate earned that way evidence of
    // nothing.
    public static ScoredAttempt Grade(
        IReadOnlyList<CourseQuestion> questions,
        IReadOnlyDictionary<Guid, int> answers,
        int passingScore)
    {
        int possible = questions.Sum(question => Math.Max(0, question.Points));

        int earned = 0;
        int correct = 0;

        foreach (CourseQuestion question in questions)
        {
            // Unanswered is wrong, not skipped: the alternative is passing by
            // answering only what you know.
            if (!answers.TryGetValue(question.Id, out int chosen))
            {
                continue;
            }

            if (chosen != question.CorrectIndex
                || chosen < 0
                || chosen >= question.Options.Count)
            {
                continue;
            }

            correct++;
            earned += Math.Max(0, question.Points);
        }

        decimal score = possible == 0
            ? 0m
            : Math.Round(earned / (decimal)possible * 100m, 1, MidpointRounding.AwayFromZero);

        return new ScoredAttempt
        {
            Score = score,
            Passed = questions.Count > 0 && possible > 0 && score >= passingScore,
            Correct = correct,
            Questions = questions.Count,
        };
    }

    public static bool CanAttempt(int? maxAttempts, int used) =>
        maxAttempts is not int limit || used < limit;

    // Null when there is no limit, so a screen can say "unlimited" rather than
    // showing a number it made up.
    public static int? AttemptsLeft(int? maxAttempts, int used) =>
        maxAttempts is int limit ? Math.Max(0, limit - used) : null;

    public static LearningRefusal Refusal(
        IReadOnlyList<CourseQuestion> questions,
        int passingScore)
    {
        if (passingScore is < 0 or > 100)
        {
            return LearningRefusal.PassMarkOutOfRange;
        }

        if (questions.Count == 0)
        {
            return LearningRefusal.NoQuestions;
        }

        foreach (CourseQuestion question in questions)
        {
            if (string.IsNullOrWhiteSpace(question.Prompt))
            {
                return LearningRefusal.QuestionPromptRequired;
            }

            if (question.Options.Count < 2)
            {
                return LearningRefusal.QuestionNeedsTwoOptions;
            }

            // An answer pointing past the options is a question nobody can
            // ever get right.
            if (question.CorrectIndex < 0 || question.CorrectIndex >= question.Options.Count)
            {
                return LearningRefusal.AnswerNotAnOption;
            }

            if (question.Points <= 0)
            {
                return LearningRefusal.PointsNotPositive;
            }
        }

        return LearningRefusal.None;
    }

    // Trimmed, folded and de-duplicated: two options worded the same make a
    // question with two right answers.
    public static IReadOnlyList<string> NormaliseOptions(IEnumerable<string?> options)
    {
        List<string> kept = [];

        foreach (string? option in options)
        {
            string trimmed = (option ?? string.Empty).Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            string folded = trimmed.ToLowerInvariant();

            if (!kept.Contains(folded, StringComparer.Ordinal))
            {
                kept.Add(folded);
            }
        }

        return kept;
    }
}

public sealed record CertTally
{
    public required int Total { get; init; }

    // Not expired, including those with no expiry and those expiring soon.
    public required int Active { get; init; }

    public required int ExpiringInThirty { get; init; }

    public required int ExpiringInSixty { get; init; }

    public required int Expired { get; init; }

    public required int CompliancePercent { get; init; }
}

public static class Certifications
{
    public const int UrgentDays = 30;

    public const int SoonDays = 60;

    public static CertStatus StatusOf(DateOnly? expiresOn, DateOnly today)
    {
        if (expiresOn is not DateOnly expiry)
        {
            return CertStatus.NoExpiry;
        }

        int days = expiry.DayNumber - today.DayNumber;

        // Expiring today has not expired: somebody can still renew it this
        // afternoon.
        if (days < 0)
        {
            return CertStatus.Expired;
        }

        if (days <= UrgentDays)
        {
            return CertStatus.ExpiringInThirty;
        }

        return days <= SoonDays ? CertStatus.ExpiringInSixty : CertStatus.Active;
    }

    public static bool IsCompliant(CertStatus status) => status != CertStatus.Expired;

    // A completion certificate's expiry, from the course's own validity rather
    // than typed: on compliance training the expiry is the record, and a
    // hand-entered one is a record somebody can get wrong.
    public static DateOnly? ExpiryFor(DateOnly completedOn, int? validityMonths) =>
        validityMonths is int months && months > 0
            ? completedOn.AddMonths(months)
            : null;

    // Counted on the same windows the status uses. The client reports a status
    // on thirty and sixty days and then counts the register on thirty and
    // ninety, so a certificate reads active in one place and sits in the
    // expiring column in another.
    public static CertTally Tally(IReadOnlyList<DateOnly?> expiries, DateOnly today)
    {
        List<CertStatus> statuses = [.. expiries.Select(expiry => StatusOf(expiry, today))];

        int expired = statuses.Count(status => status == CertStatus.Expired);

        return new CertTally
        {
            Total = expiries.Count,
            Active = statuses.Count - expired,
            ExpiringInThirty = statuses.Count(status => status == CertStatus.ExpiringInThirty),
            ExpiringInSixty = statuses.Count(status =>
                status is CertStatus.ExpiringInThirty or CertStatus.ExpiringInSixty),
            Expired = expired,
            CompliancePercent = expiries.Count == 0
                ? 100
                : (int)Math.Round(
                    (expiries.Count - expired) / (decimal)expiries.Count * 100m,
                    MidpointRounding.AwayFromZero),
        };
    }
}

public sealed record Learner
{
    public required Guid EmployeeId { get; init; }

    public required string Name { get; init; }

    public string? DepartmentName { get; init; }
}

public sealed record MandatoryCourse
{
    public required Guid CourseId { get; init; }

    public required string Title { get; init; }
}

public sealed record Assignment
{
    public required Guid EmployeeId { get; init; }

    public required Guid CourseId { get; init; }

    public required EnrollmentStatus Status { get; init; }

    public DateOnly? DueOn { get; init; }
}

public sealed record Overdue
{
    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? DepartmentName { get; init; }

    public required Guid CourseId { get; init; }

    public required string CourseTitle { get; init; }

    // Null when the person was never enrolled at all.
    public DateOnly? DueOn { get; init; }
}

public sealed record DepartmentTally
{
    public required string DepartmentName { get; init; }

    public required int Required { get; init; }

    public required int Completed { get; init; }

    public required int CompletionPercent { get; init; }

    public required int Overdue { get; init; }

    public required bool AtRisk { get; init; }
}

public sealed record MandatoryTally
{
    // Current employees times compulsory courses.
    public required int Required { get; init; }

    public required int Completed { get; init; }

    // 100 when nothing is required: a company with no compulsory training is
    // not failing at it.
    public required int CompletionPercent { get; init; }

    public required IReadOnlyList<Overdue> Overdue { get; init; }

    public required int PeopleOverdue { get; init; }

    public required IReadOnlyList<DepartmentTally> Departments { get; init; }

    public required int DepartmentsAtRisk { get; init; }
}

// Whether a company is where it should be on the training it has made
// compulsory.
//
// Every current employee must complete every compulsory course. Somebody never
// enrolled is already overdue rather than merely missing — the requirement does
// not wait for an administrator to create a row.
public static class MandatoryTraining
{
    public const int AtRiskBelowPercent = 80;

    private const string Unassigned = "No department";

    public static MandatoryTally Of(
        IReadOnlyList<Learner> learners,
        IReadOnlyList<MandatoryCourse> courses,
        IReadOnlyList<Assignment> assignments,
        DateOnly today)
    {
        // A completion wins over any other attempt at the same course: somebody
        // who failed and then passed is compliant.
        Dictionary<(Guid, Guid), Assignment> best = [];

        foreach (Assignment assignment in assignments)
        {
            (Guid, Guid) key = (assignment.EmployeeId, assignment.CourseId);

            if (!best.TryGetValue(key, out Assignment? held)
                || (assignment.Status == EnrollmentStatus.Completed
                    && held.Status != EnrollmentStatus.Completed))
            {
                best[key] = assignment;
            }
        }

        List<Overdue> overdue = [];
        Dictionary<string, (int Required, int Completed, int Overdue)> departments = [];

        int completed = 0;

        foreach (Learner learner in learners)
        {
            string department = learner.DepartmentName ?? Unassigned;

            departments.TryAdd(department, (0, 0, 0));

            foreach (MandatoryCourse course in courses)
            {
                (int required, int done, int late) = departments[department];

                required++;

                Assignment? assignment =
                    best.GetValueOrDefault((learner.EmployeeId, course.CourseId));

                if (assignment?.Status == EnrollmentStatus.Completed)
                {
                    completed++;
                    done++;
                }
                else if (assignment is null
                    || (assignment.DueOn is DateOnly due && due < today))
                {
                    late++;

                    overdue.Add(new Overdue
                    {
                        EmployeeId = learner.EmployeeId,
                        EmployeeName = learner.Name,
                        DepartmentName = learner.DepartmentName,
                        CourseId = course.CourseId,
                        CourseTitle = course.Title,
                        DueOn = assignment?.DueOn,
                    });
                }

                departments[department] = (required, done, late);
            }
        }

        int totalRequired = learners.Count * courses.Count;

        return new MandatoryTally
        {
            Required = totalRequired,
            Completed = completed,
            CompletionPercent = Share(completed, totalRequired),
            Overdue = overdue,
            PeopleOverdue = overdue.Select(row => row.EmployeeId).Distinct().Count(),
            Departments =
            [
                .. departments.Select(pair =>
                {
                    int percent = Share(pair.Value.Completed, pair.Value.Required);

                    return new DepartmentTally
                    {
                        DepartmentName = pair.Key,
                        Required = pair.Value.Required,
                        Completed = pair.Value.Completed,
                        CompletionPercent = percent,
                        Overdue = pair.Value.Overdue,
                        AtRisk = percent < AtRiskBelowPercent,
                    };
                })
                .OrderBy(row => row.DepartmentName, StringComparer.Ordinal),
            ],
            DepartmentsAtRisk = departments.Count(pair =>
                Share(pair.Value.Completed, pair.Value.Required) < AtRiskBelowPercent),
        };
    }

    // 100 when nothing is required.
    private static int Share(int done, int total) =>
        total == 0
            ? 100
            : (int)Math.Round(done / (decimal)total * 100m, MidpointRounding.AwayFromZero);
}
