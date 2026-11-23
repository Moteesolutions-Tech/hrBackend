using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Learning;
using Motee.Application.Tenancy;
using Motee.Domain.Employees;
using Motee.Domain.Learning;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Learning;

// Courses, who is on them, and whether they passed.
//
// The quiz is marked here, because it cannot be marked anywhere else: the
// client sends the correct answers to the learner's browser and scores the
// attempt there, which makes a compliance certificate earned that way evidence
// of nothing. The routes a learner can reach hand back AskedQuestion, a shape
// with nowhere to put the answer.
internal sealed class LearningService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    IRequestContext requestContext,
    TimeProvider timeProvider) : ILearningService
{
    private readonly TenantCalendar _calendar = new(dbContext, currentTenant, timeProvider);

    public async Task<PagedResult<CourseRowDto>> ListAsync(
        CourseQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Course> courses = dbContext.Courses.AsNoTracking();

        if (query.Category is CourseCategory category)
        {
            courses = courses.Where(course => course.Category == category);
        }

        if (query.Status is CourseStatus status)
        {
            courses = courses.Where(course => course.Status == status);
        }

        if (query.DeliveryMode is DeliveryMode mode)
        {
            courses = courses.Where(course => course.DeliveryMode == mode);
        }

        if (query.MandatoryOnly == true)
        {
            courses = courses.Where(course => course.IsMandatory);
        }

        if (query.WithQuizOnly == true)
        {
            courses = courses.Where(course => course.HasQuiz);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            courses = courses.Where(course =>
                EF.Functions.ILike(course.Title, pattern, "\\")
                || (course.Provider != null
                    && EF.Functions.ILike(course.Provider, pattern, "\\"))
                || (course.Instructor != null
                    && EF.Functions.ILike(course.Instructor, pattern, "\\")));
        }

        int total = await courses.CountAsync(cancellationToken);

        List<Course> page = await courses
            // Compulsory first: those are the ones somebody is accountable for.
            .OrderByDescending(course => course.IsMandatory)
            .ThenBy(course => course.Title)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<CourseRowDto>
        {
            Items = await RowsAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<CourseDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Course? course = await dbContext.Courses
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        return course is null ? null : await DescribeAsync(course, cancellationToken);
    }

    public async Task<LearningSummaryDto> SummaryAsync(
        CancellationToken cancellationToken = default)
    {
        List<Course> courses = await dbContext.Courses
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        List<CourseEnrollment> enrollments = await dbContext.CourseEnrollments
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        int completed = enrollments.Count(row => row.Status == EnrollmentStatus.Completed);

        Dictionary<Guid, decimal> hours = [];

        foreach (Course course in courses)
        {
            hours[course.Id] = course.DurationHours;
        }

        return new LearningSummaryDto
        {
            Courses = courses.Count,
            ActiveCourses = courses.Count(course => course.Status == CourseStatus.Active),
            MandatoryCourses = courses.Count(course => course.IsMandatory),
            Enrollments = enrollments.Count,
            Completed = completed,
            InProgress = enrollments.Count(row =>
                row.Status is EnrollmentStatus.Enrolled or EnrollmentStatus.InProgress),
            Overdue = enrollments.Count(row => IsOverdue(row, today)),
            CompletionPercent = enrollments.Count == 0
                ? 0
                : (int)Math.Round(
                    completed / (decimal)enrollments.Count * 100m,
                    MidpointRounding.AwayFromZero),

            // Hours that were actually delivered: a course nobody finished
            // delivered nothing.
            HoursDelivered = enrollments
                .Where(row => row.Status == EnrollmentStatus.Completed)
                .Sum(row => hours.GetValueOrDefault(row.CourseId)),
        };
    }

    public async Task<MandatoryTally> ComplianceAsync(
        CancellationToken cancellationToken = default)
    {
        List<MandatoryCourse> compulsory = await dbContext.Courses
            .AsNoTracking()
            .Where(course => course.IsMandatory && course.Status != CourseStatus.Archived)
            .Select(course => new MandatoryCourse
            {
                CourseId = course.Id,
                Title = course.Title,
            })
            .ToListAsync(cancellationToken);

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        List<Learner> learners = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => EmployeeLifecycle.InPost.Contains(employee.Status))
            .Select(employee => new
            {
                employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                employee.DepartmentId,
            })
            .Select(row => new Learner
            {
                EmployeeId = row.Id,
                Name = row.Name,
                DepartmentName = null,
            })
            .ToListAsync(cancellationToken);

        // The department name is attached here rather than in the query,
        // because the lookup is a dictionary the database cannot see.
        Dictionary<Guid, Guid?> byEmployee = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => EmployeeLifecycle.InPost.Contains(employee.Status))
            .ToDictionaryAsync(
                employee => employee.Id, employee => employee.DepartmentId, cancellationToken);

        learners =
        [
            .. learners.Select(learner => learner with
            {
                DepartmentName = byEmployee.GetValueOrDefault(learner.EmployeeId)
                    is Guid departmentId
                    ? departments.GetValueOrDefault(departmentId)
                    : null,
            }),
        ];

        List<Assignment> assignments = await dbContext.CourseEnrollments
            .AsNoTracking()
            .Select(row => new Assignment
            {
                EmployeeId = row.EmployeeId,
                CourseId = row.CourseId,
                Status = row.Status,
                DueOn = row.DueOn,
            })
            .ToListAsync(cancellationToken);

        return MandatoryTraining.Of(
            learners,
            compulsory,
            assignments,
            await _calendar.TodayAsync(cancellationToken));
    }

    public async Task<CourseResult> CreateAsync(
        CourseRequest request,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        Course course = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Title = string.Empty,
            CreatedByUserId = UserId(),
            CreatedAt = now,
        };

        Apply(course, request, now);

        LearningRefusal refusal = CourseRules.Refusal(course);

        if (refusal != LearningRefusal.None)
        {
            return CourseResult.Refused(refusal);
        }

        dbContext.Courses.Add(course);

        await dbContext.SaveChangesAsync(cancellationToken);

        return CourseResult.Ok(await DescribeAsync(course, cancellationToken));
    }

    public async Task<CourseResult> UpdateAsync(
        Guid id,
        CourseRequest request,
        CancellationToken cancellationToken = default)
    {
        Course? course = await dbContext.Courses
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (course is null)
        {
            return CourseResult.Failed(LearningOutcome.NotFound);
        }

        Apply(course, request, timeProvider.GetUtcNow());

        LearningRefusal refusal = CourseRules.Refusal(course);

        if (refusal != LearningRefusal.None)
        {
            return CourseResult.Refused(refusal);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return CourseResult.Ok(await DescribeAsync(course, cancellationToken));
    }

    public async Task<CourseResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Course? course = await dbContext.Courses
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (course is null)
        {
            return CourseResult.Failed(LearningOutcome.NotFound);
        }

        // Questions, enrolments and attempts go with it. The certificates it
        // issued do not: a certificate is a record of something that happened,
        // and withdrawing the course does not unhappen it.
        dbContext.Courses.Remove(course);

        await dbContext.SaveChangesAsync(cancellationToken);

        return CourseResult.Done();
    }

    // ── the quiz ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<QuizQuestionDto>> QuizAsync(
        Guid courseId,
        CancellationToken cancellationToken = default) =>
        await dbContext.CourseQuestions
            .AsNoTracking()
            .Where(question => question.CourseId == courseId)
            .OrderBy(question => question.Sequence)
            .Select(question => new QuizQuestionDto
            {
                Id = question.Id,
                Sequence = question.Sequence,
                QuestionKind = question.QuestionKind,
                Prompt = question.Prompt,
                Options = question.Options,
                CorrectIndex = question.CorrectIndex,
                Points = question.Points,
            })
            .ToListAsync(cancellationToken);

    public async Task<CourseResult> SetQuizAsync(
        Guid courseId,
        QuizRequest request,
        CancellationToken cancellationToken = default)
    {
        Course? course = await dbContext.Courses
            .FirstOrDefaultAsync(row => row.Id == courseId, cancellationToken);

        if (course is null)
        {
            return CourseResult.Failed(LearningOutcome.NotFound);
        }

        List<CourseQuestion> replacement =
        [
            .. request.Questions.Select((question, index) => new CourseQuestion
            {
                Id = Guid.NewGuid(),
                TenantId = course.TenantId,
                CourseId = courseId,
                Sequence = index + 1,
                QuestionKind = question.QuestionKind,
                Prompt = question.Prompt?.Trim() ?? string.Empty,

                // A true/false question is always these two, whatever was
                // sent.
                Options = question.QuestionKind == QuestionKind.TrueFalse
                    ? ["True", "False"]
                    : [.. (question.Options ?? [])
                        .Select(option => option?.Trim() ?? string.Empty)
                        .Where(option => option.Length > 0)],

                CorrectIndex = question.CorrectIndex,
                Points = question.Points,
            }),
        ];

        LearningRefusal refusal = Quizzes.Refusal(replacement, request.PassingScore);

        if (refusal != LearningRefusal.None)
        {
            return CourseResult.Refused(refusal);
        }

        List<CourseQuestion> existing = await dbContext.CourseQuestions
            .Where(question => question.CourseId == courseId)
            .ToListAsync(cancellationToken);

        dbContext.CourseQuestions.RemoveRange(existing);
        dbContext.CourseQuestions.AddRange(replacement);

        course.HasQuiz = true;
        course.PassingScore = request.PassingScore;
        course.MaxAttempts = request.MaxAttempts;
        course.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return CourseResult.Ok(await DescribeAsync(course, cancellationToken));
    }

    // ── who is on a course ──────────────────────────────────────────────────

    public async Task<PagedResult<EnrollmentDto>> EnrollmentsAsync(
        EnrollmentQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<CourseEnrollment> enrollments =
            dbContext.CourseEnrollments.AsNoTracking();

        if (query.CourseId is Guid courseId)
        {
            enrollments = enrollments.Where(row => row.CourseId == courseId);
        }

        if (query.EmployeeId is Guid employeeId)
        {
            enrollments = enrollments.Where(row => row.EmployeeId == employeeId);
        }

        if (query.Status is EnrollmentStatus status)
        {
            enrollments = enrollments.Where(row => row.Status == status);
        }

        if (query.DepartmentId is Guid departmentId)
        {
            enrollments = enrollments.Where(row => dbContext.Employees.Any(employee =>
                employee.Id == row.EmployeeId && employee.DepartmentId == departmentId));
        }

        if (query.OverdueOnly == true)
        {
            DateOnly today = await _calendar.TodayAsync(cancellationToken);

            enrollments = enrollments.Where(row =>
                row.Status != EnrollmentStatus.Completed
                && row.Status != EnrollmentStatus.Dropped
                && row.DueOn != null
                && row.DueOn < today);
        }

        int total = await enrollments.CountAsync(cancellationToken);

        List<CourseEnrollment> page = await enrollments
            .OrderByDescending(row => row.EnrolledOn)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<EnrollmentDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<EnrollResult> EnrollAsync(
        Guid courseId,
        EnrollRequest request,
        CancellationToken cancellationToken = default)
    {
        Course? course = await dbContext.Courses
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == courseId, cancellationToken);

        if (course is null)
        {
            return EnrollResult.Failed(LearningOutcome.NotFound);
        }

        // A draft course is one nobody has finished writing, and an archived
        // one is withdrawn. Neither is something to put people on.
        if (course.Status != CourseStatus.Active)
        {
            return EnrollResult.Refused(LearningRefusal.CourseNotOpen);
        }

        List<Guid> wanted = [.. request.EmployeeIds.Distinct()];

        int employed = await dbContext.Employees.CountAsync(
            employee => wanted.Contains(employee.Id)
                && employee.Status != EmployeeStatus.Deleted,
            cancellationToken);

        if (employed != wanted.Count)
        {
            return EnrollResult.Failed(LearningOutcome.EmployeeNotFound);
        }

        List<Guid> already = await dbContext.CourseEnrollments
            .AsNoTracking()
            .Where(row => row.CourseId == courseId && wanted.Contains(row.EmployeeId))
            .Select(row => row.EmployeeId)
            .ToListAsync(cancellationToken);

        List<Guid> fresh = [.. wanted.Where(id => !already.Contains(id))];

        // Capacity counts the people on the course, not the people being added
        // to it: a course with two places left does not take three.
        if (course.Capacity is int capacity)
        {
            int taken = await dbContext.CourseEnrollments
                .CountAsync(row => row.CourseId == courseId, cancellationToken);

            if (taken + fresh.Count > capacity)
            {
                return EnrollResult.Refused(LearningRefusal.CourseFull);
            }
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();

        List<CourseEnrollment> added =
        [
            .. fresh.Select(employeeId => new CourseEnrollment
            {
                Id = Guid.NewGuid(),
                TenantId = course.TenantId,
                CourseId = courseId,
                EmployeeId = employeeId,
                Status = EnrollmentStatus.Enrolled,
                Progress = 0,
                EnrolledOn = today,
                DueOn = request.DueOn,
                EnrolledByUserId = UserId(),
                CreatedAt = now,
                UpdatedAt = now,
            }),
        ];

        dbContext.CourseEnrollments.AddRange(added);

        await dbContext.SaveChangesAsync(cancellationToken);

        return EnrollResult.Ok(await DescribeAsync(added, cancellationToken), already);
    }

    public async Task<EnrollmentResult> SetProgressAsync(
        Guid enrollmentId,
        ProgressRequest request,
        CancellationToken cancellationToken = default)
    {
        CourseEnrollment? enrollment = await dbContext.CourseEnrollments
            .FirstOrDefaultAsync(row => row.Id == enrollmentId, cancellationToken);

        if (enrollment is null)
        {
            return EnrollmentResult.Failed(LearningOutcome.NotFound);
        }

        if (request.Progress is < 0 or > 100)
        {
            return EnrollmentResult.Refused(LearningRefusal.ProgressOutOfRange);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        CourseRules.Progressed(enrollment, request.Progress, today);

        enrollment.ResumeSeconds = request.ResumeSeconds ?? enrollment.ResumeSeconds;
        enrollment.UpdatedAt = timeProvider.GetUtcNow();

        await IssueIfCompleteAsync(enrollment, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return EnrollmentResult.Ok(
            (await DescribeAsync([enrollment], cancellationToken))[0]);
    }

    public async Task<EnrollmentResult> SetStatusAsync(
        Guid enrollmentId,
        EnrollmentStatus status,
        CancellationToken cancellationToken = default)
    {
        CourseEnrollment? enrollment = await dbContext.CourseEnrollments
            .FirstOrDefaultAsync(row => row.Id == enrollmentId, cancellationToken);

        if (enrollment is null)
        {
            return EnrollmentResult.Failed(LearningOutcome.NotFound);
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        CourseRules.Moved(enrollment, status, today);

        enrollment.UpdatedAt = timeProvider.GetUtcNow();

        await IssueIfCompleteAsync(enrollment, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return EnrollmentResult.Ok(
            (await DescribeAsync([enrollment], cancellationToken))[0]);
    }

    public async Task<EnrollmentResult> WithdrawAsync(
        Guid enrollmentId,
        CancellationToken cancellationToken = default)
    {
        CourseEnrollment? enrollment = await dbContext.CourseEnrollments
            .FirstOrDefaultAsync(row => row.Id == enrollmentId, cancellationToken);

        if (enrollment is null)
        {
            return EnrollmentResult.Failed(LearningOutcome.NotFound);
        }

        dbContext.CourseEnrollments.Remove(enrollment);

        await dbContext.SaveChangesAsync(cancellationToken);

        return EnrollmentResult.Done();
    }

    // ── the learner's own ───────────────────────────────────────────────────

    public async Task<MyLearningDto> MineAsync(CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return new MyLearningDto
            {
                Enrolled = [],
                Library = [],
                Completed = 0,
                InProgress = 0,
                Overdue = 0,
                HoursCompleted = 0m,
            };
        }

        List<CourseEnrollment> enrollments = await dbContext.CourseEnrollments
            .AsNoTracking()
            .Where(row => row.EmployeeId == me)
            .ToListAsync(cancellationToken);

        List<Guid> onIds = [.. enrollments.Select(row => row.CourseId)];

        List<Course> library = await dbContext.Courses
            .AsNoTracking()
            .Where(course =>
                course.Status == CourseStatus.Active && !onIds.Contains(course.Id))
            .OrderByDescending(course => course.IsMandatory)
            .ThenBy(course => course.Title)
            .ToListAsync(cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        Dictionary<Guid, decimal> hours = await dbContext.Courses
            .AsNoTracking()
            .Where(course => onIds.Contains(course.Id))
            .ToDictionaryAsync(
                course => course.Id, course => course.DurationHours, cancellationToken);

        return new MyLearningDto
        {
            EmployeeId = me,
            Enrolled = await DescribeAsync(enrollments, cancellationToken),
            Library = await RowsAsync(library, cancellationToken),
            Completed = enrollments.Count(row => row.Status == EnrollmentStatus.Completed),
            InProgress = enrollments.Count(row =>
                row.Status is EnrollmentStatus.Enrolled or EnrollmentStatus.InProgress),
            Overdue = enrollments.Count(row => IsOverdue(row, today)),
            HoursCompleted = enrollments
                .Where(row => row.Status == EnrollmentStatus.Completed)
                .Sum(row => hours.GetValueOrDefault(row.CourseId)),
        };
    }

    public async Task<AskedQuizDto?> AskedAsync(
        Guid enrollmentId,
        CancellationToken cancellationToken = default)
    {
        (CourseEnrollment? enrollment, Course? course, _) =
            await MineOrRefusedAsync(enrollmentId, cancellationToken);

        if (enrollment is null || course is null || !course.HasQuiz)
        {
            return null;
        }

        List<CourseQuestion> questions = await dbContext.CourseQuestions
            .AsNoTracking()
            .Where(question => question.CourseId == course.Id)
            .OrderBy(question => question.Sequence)
            .ToListAsync(cancellationToken);

        int used = await dbContext.QuizAttempts
            .CountAsync(attempt => attempt.EnrollmentId == enrollmentId, cancellationToken);

        return new AskedQuizDto
        {
            EnrollmentId = enrollmentId,
            CourseId = course.Id,
            CourseTitle = course.Title,
            PassingScore = course.PassingScore,
            AttemptsLeft = Quizzes.AttemptsLeft(course.MaxAttempts, used),

            // The answers do not come out of this method, because there is
            // nowhere on the shape to put them.
            Questions = [.. questions.Select(Quizzes.AsAsked)],
        };
    }

    public async Task<AttemptResult> AttemptAsync(
        Guid enrollmentId,
        AttemptRequest request,
        CancellationToken cancellationToken = default)
    {
        (CourseEnrollment? enrollment, Course? course, LearningOutcome outcome) =
            await MineOrRefusedAsync(enrollmentId, cancellationToken);

        if (enrollment is null || course is null)
        {
            return AttemptResult.Failed(outcome);
        }

        if (!course.HasQuiz)
        {
            return AttemptResult.Refused(LearningRefusal.NoQuiz);
        }

        int used = await dbContext.QuizAttempts
            .CountAsync(attempt => attempt.EnrollmentId == enrollmentId, cancellationToken);

        if (!Quizzes.CanAttempt(course.MaxAttempts, used))
        {
            return AttemptResult.Refused(LearningRefusal.NoAttemptsLeft, 0);
        }

        List<CourseQuestion> questions = await dbContext.CourseQuestions
            .AsNoTracking()
            .Where(question => question.CourseId == course.Id)
            .OrderBy(question => question.Sequence)
            .ToListAsync(cancellationToken);

        // Marked against the stored questions, never against anything the
        // caller sent.
        ScoredAttempt scored = Quizzes.Grade(
            questions, request.Answers, course.PassingScore);

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        dbContext.QuizAttempts.Add(new QuizAttempt
        {
            Id = Guid.NewGuid(),
            TenantId = enrollment.TenantId,
            EnrollmentId = enrollmentId,
            Score = scored.Score,
            Passed = scored.Passed,
            Correct = scored.Correct,
            Questions = scored.Questions,
            AttemptedAt = now,
        });

        if (scored.Passed)
        {
            CourseRules.Moved(enrollment, EnrollmentStatus.Completed, today);
        }
        else if (!Quizzes.CanAttempt(course.MaxAttempts, used + 1))
        {
            // Out of goes without passing. Recorded as failed rather than left
            // in progress, because nothing more is going to happen to it.
            CourseRules.Moved(enrollment, EnrollmentStatus.Failed, today);
        }

        enrollment.UpdatedAt = now;

        Certification? certificate = scored.Passed
            ? await IssueIfCompleteAsync(enrollment, cancellationToken)
            : null;

        await dbContext.SaveChangesAsync(cancellationToken);

        return new AttemptResult
        {
            Outcome = LearningOutcome.Succeeded,
            Scored = scored,
            Enrollment = (await DescribeAsync([enrollment], cancellationToken))[0],
            Certificate = certificate is null
                ? null
                : await DescribeAsync(certificate, course, cancellationToken),
            AttemptsLeft = Quizzes.AttemptsLeft(course.MaxAttempts, used + 1),
        };
    }

    // ── shared ──────────────────────────────────────────────────────────────

    // Completing a course issues its certificate, whose expiry follows the
    // course's own validity rather than being typed. One per enrolment, so
    // finishing twice does not produce two.
    private async Task<Certification?> IssueIfCompleteAsync(
        CourseEnrollment enrollment,
        CancellationToken cancellationToken)
    {
        if (enrollment.Status != EnrollmentStatus.Completed
            || enrollment.CompletedOn is not DateOnly completedOn)
        {
            return null;
        }

        Course? course = await dbContext.Courses
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == enrollment.CourseId, cancellationToken);

        if (course is null)
        {
            return null;
        }

        bool already = await dbContext.Certifications.AnyAsync(
            row => row.EmployeeId == enrollment.EmployeeId
                && row.CourseId == enrollment.CourseId,
            cancellationToken);

        if (already)
        {
            return null;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        Certification certificate = new()
        {
            Id = Guid.NewGuid(),
            TenantId = enrollment.TenantId,
            EmployeeId = enrollment.EmployeeId,
            CourseId = enrollment.CourseId,
            CertificationKind = CertificationKind.Course,
            Title = course.Title,
            IssuingBody = course.Provider,
            Category = course.Category.ToString(),
            IssuedOn = completedOn,
            ExpiresOn = Certifications.ExpiryFor(completedOn, course.ValidityMonths),
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.Certifications.Add(certificate);

        return certificate;
    }

    private async Task<(CourseEnrollment? Enrollment, Course? Course, LearningOutcome Outcome)>
        MineOrRefusedAsync(Guid enrollmentId, CancellationToken cancellationToken)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return (null, null, LearningOutcome.NoEmployeeRecord);
        }

        CourseEnrollment? enrollment = await dbContext.CourseEnrollments
            .FirstOrDefaultAsync(row => row.Id == enrollmentId, cancellationToken);

        if (enrollment is null)
        {
            return (null, null, LearningOutcome.NotFound);
        }

        // Somebody else's enrolment is not theirs to sit.
        if (enrollment.EmployeeId != me)
        {
            return (null, null, LearningOutcome.NotYours);
        }

        Course? course = await dbContext.Courses
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == enrollment.CourseId, cancellationToken);

        return (enrollment, course, LearningOutcome.Succeeded);
    }

    private static bool IsOverdue(CourseEnrollment enrollment, DateOnly today) =>
        enrollment.Status is not (EnrollmentStatus.Completed or EnrollmentStatus.Dropped)
            && enrollment.DueOn is DateOnly due
            && due < today;

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private static void Apply(Course course, CourseRequest request, DateTimeOffset now)
    {
        course.Title = request.Title?.Trim() ?? string.Empty;
        course.Description = Blank(request.Description);
        course.Category = request.Category;
        course.Status = request.Status;
        course.DeliveryMode = request.DeliveryMode;
        course.Instructor = Blank(request.Instructor);
        course.Provider = Blank(request.Provider);
        course.IsMandatory = request.IsMandatory;
        course.ValidityMonths = request.ValidityMonths;
        course.CourseUrl = Blank(request.CourseUrl);
        course.VideoUrl = Blank(request.VideoUrl);
        course.DurationHours = request.DurationHours;
        course.Capacity = request.Capacity;
        course.StartsOn = request.StartsOn;
        course.EndsOn = request.EndsOn;

        // Replaced rather than mutated: the jsonb converter compares by
        // serialising.
        course.Tags =
        [
            .. (request.Tags ?? [])
                .Select(tag => tag?.Trim() ?? string.Empty)
                .Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        course.UpdatedAt = now;
    }

    private async Task<List<CourseRowDto>> RowsAsync(
        IReadOnlyList<Course> courses,
        CancellationToken cancellationToken)
    {
        if (courses.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. courses.Select(course => course.Id)];

        List<CourseEnrollment> enrollments = await dbContext.CourseEnrollments
            .AsNoTracking()
            .Where(row => ids.Contains(row.CourseId))
            .ToListAsync(cancellationToken);

        return [.. courses.Select(course => Row(course, enrollments))];
    }

    private static CourseRowDto Row(
        Course course,
        IReadOnlyList<CourseEnrollment> enrollments)
    {
        int enrolled = enrollments.Count(row => row.CourseId == course.Id);

        int completed = enrollments.Count(row =>
            row.CourseId == course.Id && row.Status == EnrollmentStatus.Completed);

        return new CourseRowDto
        {
            Id = course.Id,
            Title = course.Title,
            Description = course.Description,
            Category = course.Category,
            Status = course.Status,
            DeliveryMode = course.DeliveryMode,
            Instructor = course.Instructor,
            Provider = course.Provider,
            IsMandatory = course.IsMandatory,
            ValidityMonths = course.ValidityMonths,
            CourseUrl = course.CourseUrl,
            VideoUrl = course.VideoUrl,
            DurationHours = course.DurationHours,
            Capacity = course.Capacity,
            StartsOn = course.StartsOn,
            EndsOn = course.EndsOn,
            Tags = [.. course.Tags],
            HasQuiz = course.HasQuiz,
            PassingScore = course.PassingScore,
            MaxAttempts = course.MaxAttempts,
            EnrolledCount = enrolled,
            CompletedCount = completed,
            PlacesLeft = course.Capacity is int capacity
                ? Math.Max(0, capacity - enrolled)
                : null,
            CompletionPercent = enrolled == 0
                ? 0
                : (int)Math.Round(
                    completed / (decimal)enrolled * 100m, MidpointRounding.AwayFromZero),
            CreatedAt = course.CreatedAt,
        };
    }

    private async Task<CourseDto> DescribeAsync(
        Course course,
        CancellationToken cancellationToken) => new()
        {
            Course = (await RowsAsync([course], cancellationToken))[0],

            // The count, not the questions: this route is read by screens a
            // learner can reach.
            QuestionCount = await dbContext.CourseQuestions
                .CountAsync(question => question.CourseId == course.Id, cancellationToken),
        };

    private async Task<List<EnrollmentDto>> DescribeAsync(
        IReadOnlyList<CourseEnrollment> enrollments,
        CancellationToken cancellationToken)
    {
        if (enrollments.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. enrollments.Select(row => row.Id)];
        List<Guid> courseIds = [.. enrollments.Select(row => row.CourseId).Distinct()];
        List<Guid> employeeIds = [.. enrollments.Select(row => row.EmployeeId).Distinct()];

        Dictionary<Guid, Course> courses = await dbContext.Courses
            .AsNoTracking()
            .Where(course => courseIds.Contains(course.Id))
            .ToDictionaryAsync(course => course.Id, cancellationToken);

        List<QuizAttempt> attempts = await dbContext.QuizAttempts
            .AsNoTracking()
            .Where(attempt => ids.Contains(attempt.EnrollmentId))
            .OrderBy(attempt => attempt.AttemptedAt)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, string> departments = await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id, department => department.Name, cancellationToken);

        Dictionary<Guid, Person> people = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new
            {
                employee.Id,
                Name = employee.FirstName + " " + employee.LastName,
                employee.DepartmentId,
            })
            .ToDictionaryAsync(
                row => row.Id,
                row => new Person(
                    row.Name,
                    row.DepartmentId is Guid id ? departments.GetValueOrDefault(id) : null),
                cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return
        [
            .. enrollments.Select(row =>
            {
                Course? course = courses.GetValueOrDefault(row.CourseId);

                IReadOnlyList<QuizAttempt> own =
                    [.. attempts.Where(attempt => attempt.EnrollmentId == row.Id)];

                return new EnrollmentDto
                {
                    Id = row.Id,
                    CourseId = row.CourseId,
                    CourseTitle = course?.Title ?? "Withdrawn course",
                    IsMandatory = course?.IsMandatory ?? false,
                    EmployeeId = row.EmployeeId,
                    EmployeeName = people.GetValueOrDefault(row.EmployeeId)?.Name,
                    DepartmentName =
                        people.GetValueOrDefault(row.EmployeeId)?.DepartmentName,
                    Status = row.Status,
                    Progress = row.Progress,
                    ResumeSeconds = row.ResumeSeconds,
                    EnrolledOn = row.EnrolledOn,
                    DueOn = row.DueOn,
                    CompletedOn = row.CompletedOn,
                    IsOverdue = IsOverdue(row, today),

                    // Read from the attempts rather than stored beside them.
                    BestScore = own.Count == 0 ? null : own.Max(a => a.Score),
                    QuizPassed = own.Any(attempt => attempt.Passed),
                    AttemptsUsed = own.Count,
                    AttemptsLeft = course is null
                        ? null
                        : Quizzes.AttemptsLeft(course.MaxAttempts, own.Count),
                    Attempts =
                    [
                        .. own.Select(attempt => new AttemptDto
                        {
                            Score = attempt.Score,
                            Passed = attempt.Passed,
                            Correct = attempt.Correct,
                            Questions = attempt.Questions,
                            AttemptedAt = attempt.AttemptedAt,
                        }),
                    ],
                };
            }),
        ];
    }

    private async Task<CertificationDto> DescribeAsync(
        Certification certificate,
        Course course,
        CancellationToken cancellationToken)
    {
        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return new CertificationDto
        {
            Id = certificate.Id,
            EmployeeId = certificate.EmployeeId,
            CourseId = certificate.CourseId,
            CourseTitle = course.Title,
            CertificationKind = certificate.CertificationKind,
            Title = certificate.Title,
            IssuingBody = certificate.IssuingBody,
            Category = certificate.Category,
            CredentialId = certificate.CredentialId,
            IssuedOn = certificate.IssuedOn,
            ExpiresOn = certificate.ExpiresOn,
            Status = Certifications.StatusOf(certificate.ExpiresOn, today),
            DaysToExpiry = certificate.ExpiresOn is DateOnly expiry
                ? expiry.DayNumber - today.DayNumber
                : null,
            StoredFileId = certificate.StoredFileId,
            ExpiryIsDerived = true,
        };
    }

    private static string Escaped(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record Person(string Name, string? DepartmentName);
}
