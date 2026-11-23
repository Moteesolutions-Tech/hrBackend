using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Surveys;
using Motee.Application.Tenancy;
using Motee.Domain.Employees;
using Motee.Domain.Surveys;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Surveys;

// Surveys.
//
// Everything a reader gets back is an aggregate. The respondent id is on the
// row — one reply per person has to be enforceable, and somebody has to be
// able to see that they have answered — but on an anonymous survey it never
// reaches a DTO, and below the reporting threshold the aggregates do not
// either. Both halves matter: a mean over three replies from a team of four
// identifies people as surely as a name would.
internal sealed class SurveyService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICurrentEmployee currentEmployee,
    TimeProvider timeProvider) : ISurveyService
{
    private string? _zone;

    public async Task<PagedResult<SurveyDto>> ListAsync(
        SurveyQuery query,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Survey> surveys = dbContext.Surveys.AsNoTracking();

        if (query.Status is SurveyStatus status)
        {
            surveys = surveys.Where(survey => survey.Status == status);
        }

        if (query.SurveyType is SurveyType type)
        {
            surveys = surveys.Where(survey => survey.SurveyType == type);
        }

        if (query.Audience is SurveyAudience audience)
        {
            surveys = surveys.Where(survey => survey.Audience == audience);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = $"%{Escaped(query.Search.Trim())}%";

            surveys = surveys.Where(survey =>
                EF.Functions.ILike(survey.Title, pattern, "\\")
                || EF.Functions.ILike(survey.Description, pattern, "\\"));
        }

        int total = await surveys.CountAsync(cancellationToken);

        List<Survey> page = await surveys
            .OrderByDescending(survey => survey.CreatedAt)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<SurveyDto>
        {
            Items = await DescribeAsync(page, cancellationToken),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = total,
        };
    }

    public async Task<SurveyDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Survey? survey = await dbContext.Surveys
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        return survey is null ? null : (await DescribeAsync([survey], cancellationToken))[0];
    }

    public async Task<SurveyBoardDto> SummaryAsync(CancellationToken cancellationToken = default)
    {
        List<Survey> surveys = await dbContext.Surveys.AsNoTracking().ToListAsync(cancellationToken);

        int responses = await dbContext.SurveyResponses.CountAsync(cancellationToken);

        // Across every survey that has been opened, from the answers rather
        // than the turnout. The client averages the response rate here, which
        // scores a survey everybody hated at a hundred.
        List<Guid> opened =
        [
            .. surveys
                .Where(survey => survey.Status is not (SurveyStatus.Draft or SurveyStatus.Archived))
                .Select(survey => survey.Id),
        ];

        decimal? engagement = opened.Count == 0
            ? null
            : SurveyRules.EngagementScore(await ScoredAsync(opened, cancellationToken));

        return new SurveyBoardDto
        {
            Total = surveys.Count,
            Active = surveys.Count(survey => survey.Status == SurveyStatus.Active),
            Draft = surveys.Count(survey => survey.Status == SurveyStatus.Draft),
            Closed = surveys.Count(survey => survey.Status == SurveyStatus.Closed),
            Archived = surveys.Count(survey => survey.Status == SurveyStatus.Archived),
            TotalResponses = responses,
            EngagementScore = engagement,
        };
    }

    public async Task<SurveyResultsDto?> ResultsAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Survey? survey = await dbContext.Surveys
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (survey is null)
        {
            return null;
        }

        List<SurveyQuestion> questions = await QuestionsAsync([id], cancellationToken);

        List<SurveyResponse> responses = await dbContext.SurveyResponses
            .AsNoTracking()
            .Where(row => row.SurveyId == id)
            .ToListAsync(cancellationToken);

        decimal? rate = SurveyRules.ResponseRate(responses.Count, survey.TotalTargeted);

        // The whole report, withheld. Returning empty charts instead would
        // read as "nobody answered" rather than "too few to publish".
        if (!SurveyRules.CanReport(survey, responses.Count))
        {
            return new SurveyResultsDto
            {
                SurveyId = id,
                Responses = responses.Count,
                ResponseRate = rate,
                IsReportable = false,
                Withheld =
                    $"Results are published once at least {SurveyRules.SmallestReportableGroup} "
                    + "people have answered. With fewer, the answers would identify them.",
                Questions = [],
                ByDepartment = [],
                SuppressedDepartments = 0,
            };
        }

        List<Guid> responseIds = [.. responses.Select(row => row.Id)];

        List<SurveyAnswer> answers = responseIds.Count == 0
            ? []
            : await dbContext.SurveyAnswers
                .AsNoTracking()
                .Where(row => responseIds.Contains(row.ResponseId))
                .ToListAsync(cancellationToken);

        Dictionary<Guid, string> departments = await DepartmentsAsync(cancellationToken);

        // Per department, and only where the group is big enough to report.
        List<DepartmentScoreDto> byDepartment = [];
        int suppressed = 0;

        foreach (IGrouping<Guid?, SurveyResponse> group in responses
            .GroupBy(row => row.RespondentDepartmentId))
        {
            if (group.Count() < SurveyRules.SmallestReportableGroup && survey.IsAnonymous)
            {
                suppressed++;
                continue;
            }

            List<Guid> ids = [.. group.Select(row => row.Id)];

            byDepartment.Add(new DepartmentScoreDto
            {
                DepartmentId = group.Key,
                DepartmentName = group.Key is Guid departmentId
                    ? departments.GetValueOrDefault(departmentId) ?? "Unassigned"
                    : "Unassigned",
                Responses = group.Count(),
                EngagementScore = SurveyRules.EngagementScore(
                    Scored(questions, answers.Where(row => ids.Contains(row.ResponseId)))),
            });
        }

        return new SurveyResultsDto
        {
            SurveyId = id,
            Responses = responses.Count,
            ResponseRate = rate,
            IsReportable = true,
            EngagementScore = SurveyRules.EngagementScore(Scored(questions, answers)),
            Enps = SurveyRules.Enps(NpsScores(questions, answers)),
            Questions = [.. questions.Select(question => Aggregate(question, answers))],
            ByDepartment =
                [.. byDepartment.OrderByDescending(row => row.Responses)
                    .ThenBy(row => row.DepartmentName, StringComparer.Ordinal)],
            SuppressedDepartments = suppressed,
        };
    }

    public async Task<IReadOnlyList<EngagementPointDto>> EngagementTrendAsync(
        int months = 12,
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await TodayAsync(cancellationToken);

        DateOnly from = new DateOnly(today.Year, today.Month, 1)
            .AddMonths(-Math.Clamp(months, 1, 36) + 1);

        DateTimeOffset start = new(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        List<SurveyResponse> responses = await dbContext.SurveyResponses
            .AsNoTracking()
            .Where(row => row.SubmittedAt >= start)
            .ToListAsync(cancellationToken);

        if (responses.Count == 0)
        {
            return [];
        }

        List<Guid> surveyIds = [.. responses.Select(row => row.SurveyId).Distinct()];

        List<SurveyQuestion> questions = await QuestionsAsync(surveyIds, cancellationToken);

        List<Guid> responseIds = [.. responses.Select(row => row.Id)];

        List<SurveyAnswer> answers = await dbContext.SurveyAnswers
            .AsNoTracking()
            .Where(row => responseIds.Contains(row.ResponseId))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, string> departments = await DepartmentsAsync(cancellationToken);

        string? zone = await ZoneAsync(cancellationToken);

        List<EngagementPointDto> points = [];

        foreach (IGrouping<string, SurveyResponse> month in responses
            .GroupBy(row => TenantClock.DayOf(row.SubmittedAt, zone).ToString("yyyy-MM"))
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            List<Guid> ids = [.. month.Select(row => row.Id)];

            List<SurveyAnswer> theirs = [.. answers.Where(row => ids.Contains(row.ResponseId))];

            List<DepartmentScoreDto> byDepartment = [];

            foreach (IGrouping<Guid?, SurveyResponse> group in month
                .GroupBy(row => row.RespondentDepartmentId))
            {
                // The same threshold as the rest of the module: a month in
                // which two people from a team answered is not a data point
                // about that team.
                if (group.Count() < SurveyRules.SmallestReportableGroup)
                {
                    continue;
                }

                List<Guid> groupIds = [.. group.Select(row => row.Id)];

                byDepartment.Add(new DepartmentScoreDto
                {
                    DepartmentId = group.Key,
                    DepartmentName = group.Key is Guid departmentId
                        ? departments.GetValueOrDefault(departmentId) ?? "Unassigned"
                        : "Unassigned",
                    Responses = group.Count(),
                    EngagementScore = SurveyRules.EngagementScore(
                        Scored(questions, theirs.Where(row => groupIds.Contains(row.ResponseId)))),
                });
            }

            points.Add(new EngagementPointDto
            {
                Month = month.Key,
                CompanyWide = SurveyRules.EngagementScore(Scored(questions, theirs)),
                ByDepartment = byDepartment,
            });
        }

        return points;
    }

    public async Task<SurveyResult> CreateAsync(
        SurveyRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return SurveyResult.Failed(SurveyOutcome.NoEmployeeRecord);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        Survey survey = new()
        {
            Id = Guid.NewGuid(),
            TenantId = currentTenant.TenantId ?? Guid.Empty,
            Title = string.Empty,
            Description = string.Empty,
            CreatedByEmployeeId = me,
            CreatedAt = now,
        };

        Apply(survey, request, now);

        List<SurveyQuestion> questions = Build(survey, request.Questions);

        SurveyRefusal refusal = SurveyRules.Refusal(survey, questions);

        if (refusal != SurveyRefusal.None)
        {
            return SurveyResult.Refused(refusal);
        }

        survey.TotalTargeted = SurveyRules.TargetFor(
            survey.Status, was: null, eligibleNow: await EligibleCountAsync(survey, cancellationToken));

        dbContext.Surveys.Add(survey);
        dbContext.SurveyQuestions.AddRange(questions);

        await dbContext.SaveChangesAsync(cancellationToken);

        return SurveyResult.Ok((await DescribeAsync([survey], cancellationToken))[0]);
    }

    public async Task<SurveyResult> UpdateAsync(
        Guid id,
        SurveyRequest request,
        CancellationToken cancellationToken = default)
    {
        Survey? survey = await FindAsync(id, cancellationToken);

        if (survey is null)
        {
            return SurveyResult.Failed(SurveyOutcome.NotFound);
        }

        // Editing a question under a reply would leave an answer pointing at a
        // question that no longer exists, or worse, at one whose wording has
        // changed since it was answered.
        bool answered = await dbContext.SurveyResponses
            .AnyAsync(row => row.SurveyId == id, cancellationToken);

        if (answered)
        {
            return SurveyResult.Failed(SurveyOutcome.HasResponses);
        }

        Apply(survey, request, timeProvider.GetUtcNow());

        List<SurveyQuestion> questions = Build(survey, request.Questions);

        SurveyRefusal refusal = SurveyRules.Refusal(survey, questions);

        if (refusal != SurveyRefusal.None)
        {
            return SurveyResult.Refused(refusal);
        }

        survey.TotalTargeted = SurveyRules.TargetFor(
            survey.Status,
            survey.TotalTargeted,
            await EligibleCountAsync(survey, cancellationToken));

        dbContext.SurveyQuestions.RemoveRange(
            await dbContext.SurveyQuestions.Where(row => row.SurveyId == id)
                .ToListAsync(cancellationToken));

        dbContext.SurveyQuestions.AddRange(questions);

        await dbContext.SaveChangesAsync(cancellationToken);

        return SurveyResult.Ok((await DescribeAsync([survey], cancellationToken))[0]);
    }

    public async Task<SurveyResult> SetStatusAsync(
        Guid id,
        SurveyStatus status,
        CancellationToken cancellationToken = default)
    {
        Survey? survey = await FindAsync(id, cancellationToken);

        if (survey is null)
        {
            return SurveyResult.Failed(SurveyOutcome.NotFound);
        }

        survey.Status = status;

        // Opening it captures who was asked, and nothing moves it afterwards.
        survey.TotalTargeted = SurveyRules.TargetFor(
            status,
            survey.TotalTargeted,
            await EligibleCountAsync(survey, cancellationToken));

        survey.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return SurveyResult.Ok((await DescribeAsync([survey], cancellationToken))[0]);
    }

    public async Task<SurveyResult> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Survey? survey = await FindAsync(id, cancellationToken);

        if (survey is null)
        {
            return SurveyResult.Failed(SurveyOutcome.NotFound);
        }

        dbContext.Surveys.Remove(survey);

        await dbContext.SaveChangesAsync(cancellationToken);

        return SurveyResult.Done();
    }

    public async Task<IReadOnlyList<SurveyDto>> MineAsync(
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return [];
        }

        List<Survey> active = await dbContext.Surveys
            .AsNoTracking()
            .Where(survey => survey.Status == SurveyStatus.Active)
            .ToListAsync(cancellationToken);

        List<Guid> answered = await dbContext.SurveyResponses
            .AsNoTracking()
            .Where(row => row.RespondentEmployeeId == me)
            .Select(row => row.SurveyId)
            .ToListAsync(cancellationToken);

        Respondent? who = await RespondentAsync(me, cancellationToken);

        List<Survey> open = [];

        foreach (Survey survey in active)
        {
            if (answered.Contains(survey.Id))
            {
                continue;
            }

            if (who is not null && await IsInAudienceAsync(survey, who, cancellationToken))
            {
                open.Add(survey);
            }
        }

        return await DescribeAsync(open, cancellationToken);
    }

    public async Task<SurveyResult> RespondAsync(
        Guid id,
        SurveySubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return SurveyResult.Failed(SurveyOutcome.NoEmployeeRecord);
        }

        Survey? survey = await FindAsync(id, cancellationToken);

        if (survey is null)
        {
            return SurveyResult.Failed(SurveyOutcome.NotFound);
        }

        if (survey.Status != SurveyStatus.Active)
        {
            return SurveyResult.Refused(SurveyRefusal.NotOpen);
        }

        Respondent? who = await RespondentAsync(me, cancellationToken);

        if (who is null || !await IsInAudienceAsync(survey, who, cancellationToken))
        {
            return SurveyResult.Refused(SurveyRefusal.NotInAudience);
        }

        bool already = await dbContext.SurveyResponses
            .AnyAsync(row => row.SurveyId == id && row.RespondentEmployeeId == me, cancellationToken);

        if (already)
        {
            return SurveyResult.Refused(SurveyRefusal.AlreadyAnswered);
        }

        List<SurveyQuestion> questions = await QuestionsAsync([id], cancellationToken);

        Dictionary<Guid, IReadOnlyList<string>> given = request.Answers
            .GroupBy(answer => answer.QuestionId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)[.. group.First().Values ?? []]);

        // Every question is checked, including the ones the client left out —
        // a required question missing from the payload is the commonest way a
        // half-finished form gets submitted.
        foreach (SurveyQuestion question in questions)
        {
            SurveyRefusal refusal = SurveyRules.AnswerRefusal(
                question, given.GetValueOrDefault(question.Id, []));

            if (refusal != SurveyRefusal.None)
            {
                return SurveyResult.Refused(refusal);
            }
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        SurveyResponse response = new()
        {
            Id = Guid.NewGuid(),
            TenantId = survey.TenantId,
            SurveyId = id,
            RespondentEmployeeId = me,

            // Captured now rather than read back later: a transfer afterwards
            // must not move this answer between departments, and on an
            // anonymous survey the respondent's record should not be read at
            // report time at all.
            RespondentDepartmentId = who.DepartmentId,
            SubmittedAt = now,
        };

        dbContext.SurveyResponses.Add(response);

        foreach (SurveyQuestion question in questions)
        {
            IReadOnlyList<string> values = given.GetValueOrDefault(question.Id, []);

            if (values.Count == 0)
            {
                continue;
            }

            dbContext.SurveyAnswers.Add(new SurveyAnswer
            {
                Id = Guid.NewGuid(),
                TenantId = survey.TenantId,
                ResponseId = response.Id,
                QuestionId = question.Id,
                Values = [.. values.Select(value => value.Trim())],
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return SurveyResult.Ok((await DescribeAsync([survey], cancellationToken))[0]);
    }

    public async Task<MySurveyResponseDto?> MyResponseAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (await currentEmployee.IdAsync(cancellationToken) is not Guid me)
        {
            return null;
        }

        Survey? survey = await dbContext.Surveys
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (survey is null)
        {
            return null;
        }

        SurveyResponse? mine = await dbContext.SurveyResponses
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.SurveyId == id && row.RespondentEmployeeId == me, cancellationToken);

        if (mine is null)
        {
            return new MySurveyResponseDto { HaveIAnswered = false };
        }

        // On an anonymous survey, reading the answers back is a route to the
        // author — so it reports only that they have answered.
        if (survey.IsAnonymous)
        {
            return new MySurveyResponseDto
            {
                HaveIAnswered = true,
                SubmittedAt = mine.SubmittedAt,
            };
        }

        List<SurveyAnswer> answers = await dbContext.SurveyAnswers
            .AsNoTracking()
            .Where(row => row.ResponseId == mine.Id)
            .ToListAsync(cancellationToken);

        return new MySurveyResponseDto
        {
            HaveIAnswered = true,
            SubmittedAt = mine.SubmittedAt,
            Answers =
            [
                .. answers.Select(answer => new SubmittedAnswerDto
                {
                    QuestionId = answer.QuestionId,
                    Values = answer.Values,
                }),
            ],
        };
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private Task<Survey?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Surveys.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

    private Task<List<SurveyQuestion>> QuestionsAsync(
        IReadOnlyList<Guid> surveyIds,
        CancellationToken cancellationToken) =>
        dbContext.SurveyQuestions
            .AsNoTracking()
            .Where(row => surveyIds.Contains(row.SurveyId))
            .OrderBy(row => row.Sequence)
            .ToListAsync(cancellationToken);

    private static QuestionResultDto Aggregate(
        SurveyQuestion question,
        IReadOnlyList<SurveyAnswer> answers)
    {
        List<string> values =
        [
            .. answers
                .Where(answer => answer.QuestionId == question.Id)
                .SelectMany(answer => answer.Values),
        ];

        bool scaled = question.QuestionType is QuestionType.Rating or QuestionType.Nps;

        List<int> numbers =
        [
            .. values
                .Select(value => int.TryParse(value, out int number) ? number : (int?)null)
                .OfType<int>(),
        ];

        return new QuestionResultDto
        {
            QuestionId = question.Id,
            Text = question.Text,
            QuestionType = question.QuestionType,

            // Counted per response rather than per value, so a multiple choice
            // with three ticks is still one person answering.
            Answers = answers.Count(answer =>
                answer.QuestionId == question.Id && answer.Values.Count > 0),
            Distribution = scaled
                ?
                [
                    .. numbers
                        .GroupBy(number => number)
                        .OrderBy(group => group.Key)
                        .Select(group => new ValueCountDto
                        {
                            Value = group.Key.ToString(),
                            Count = group.Count(),
                        }),
                ]
                : [],
            Options = question.QuestionType
                is QuestionType.MultipleChoice or QuestionType.Likert or QuestionType.YesNo
                ?
                [
                    .. values
                        .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(group => group.Count())
                        .Select(group => new ValueCountDto
                        {
                            Value = group.Key,
                            Count = group.Count(),
                        }),
                ]
                : [],

            // On the question's own scale rather than normalised, so it reads
            // the way the question was asked.
            Average = scaled && numbers.Count > 0
                ? Math.Round(numbers.Average(number => (decimal)number), 1,
                    MidpointRounding.AwayFromZero)
                : null,
            Comments = question.QuestionType == QuestionType.OpenText
                ? [.. values.Where(value => value.Trim().Length > 0)]
                : [],
        };
    }

    // Every scale answer, normalised later by the domain. NPS is deliberately
    // excluded — eNPS is its own metric and folding it in would make one
    // number mean two things.
    private static IReadOnlyList<ScoredAnswer> Scored(
        IReadOnlyList<SurveyQuestion> questions,
        IEnumerable<SurveyAnswer> answers)
    {
        Dictionary<Guid, SurveyQuestion> byId = questions.ToDictionary(question => question.Id);

        List<ScoredAnswer> scored = [];

        foreach (SurveyAnswer answer in answers)
        {
            if (!byId.TryGetValue(answer.QuestionId, out SurveyQuestion? question))
            {
                continue;
            }

            if (question.QuestionType == QuestionType.Rating
                && question.ScaleMin is int min
                && question.ScaleMax is int max)
            {
                foreach (string value in answer.Values)
                {
                    if (int.TryParse(value, out int number))
                    {
                        scored.Add(new ScoredAnswer { Value = number, Min = min, Max = max });
                    }
                }
            }
            else if (question.QuestionType == QuestionType.Likert && question.Options.Count > 1)
            {
                // Position on the scale the options describe, so a five-point
                // Likert and a seven-point one land on the same hundred.
                foreach (string value in answer.Values)
                {
                    int index = question.Options.FindIndex(
                        option => string.Equals(option, value, StringComparison.OrdinalIgnoreCase));

                    if (index >= 0)
                    {
                        scored.Add(new ScoredAnswer
                        {
                            Value = index,
                            Min = 0,
                            Max = question.Options.Count - 1,
                        });
                    }
                }
            }
        }

        return scored;
    }

    private static IReadOnlyList<int> NpsScores(
        IReadOnlyList<SurveyQuestion> questions,
        IReadOnlyList<SurveyAnswer> answers)
    {
        HashSet<Guid> npsQuestions =
        [
            .. questions
                .Where(question => question.QuestionType == QuestionType.Nps)
                .Select(question => question.Id),
        ];

        return
        [
            .. answers
                .Where(answer => npsQuestions.Contains(answer.QuestionId))
                .SelectMany(answer => answer.Values)
                .Select(value => int.TryParse(value, out int number) ? number : (int?)null)
                .OfType<int>(),
        ];
    }

    private async Task<IReadOnlyList<ScoredAnswer>> ScoredAsync(
        IReadOnlyList<Guid> surveyIds,
        CancellationToken cancellationToken)
    {
        List<SurveyQuestion> questions = await QuestionsAsync(surveyIds, cancellationToken);

        List<Guid> responseIds = await dbContext.SurveyResponses
            .AsNoTracking()
            .Where(row => surveyIds.Contains(row.SurveyId))
            .Select(row => row.Id)
            .ToListAsync(cancellationToken);

        if (responseIds.Count == 0)
        {
            return [];
        }

        List<SurveyAnswer> answers = await dbContext.SurveyAnswers
            .AsNoTracking()
            .Where(row => responseIds.Contains(row.ResponseId))
            .ToListAsync(cancellationToken);

        return Scored(questions, answers);
    }

    private async Task<List<SurveyDto>> DescribeAsync(
        IReadOnlyList<Survey> surveys,
        CancellationToken cancellationToken)
    {
        if (surveys.Count == 0)
        {
            return [];
        }

        Guid me = await currentEmployee.IdAsync(cancellationToken) ?? Guid.Empty;

        List<Guid> ids = [.. surveys.Select(survey => survey.Id)];

        List<SurveyQuestion> questions = await QuestionsAsync(ids, cancellationToken);

        Dictionary<Guid, int> counts = await dbContext.SurveyResponses
            .AsNoTracking()
            .Where(row => ids.Contains(row.SurveyId))
            .GroupBy(row => row.SurveyId)
            .Select(group => new { SurveyId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.SurveyId, row => row.Count, cancellationToken);

        List<Guid> mine = await dbContext.SurveyResponses
            .AsNoTracking()
            .Where(row => ids.Contains(row.SurveyId) && row.RespondentEmployeeId == me)
            .Select(row => row.SurveyId)
            .ToListAsync(cancellationToken);

        List<Guid> authorIds = [.. surveys.Select(survey => survey.CreatedByEmployeeId).Distinct()];

        Dictionary<Guid, string> authors = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => authorIds.Contains(employee.Id))
            .ToDictionaryAsync(
                employee => employee.Id,
                employee => employee.FirstName + " " + employee.LastName,
                cancellationToken);

        return
        [
            .. surveys.Select(survey =>
            {
                int responses = counts.GetValueOrDefault(survey.Id);

                return new SurveyDto
                {
                    Id = survey.Id,
                    Title = survey.Title,
                    Description = survey.Description,
                    SurveyType = survey.SurveyType,
                    Status = survey.Status,
                    Audience = survey.Audience,
                    TargetDepartmentIds = survey.TargetDepartmentIds,
                    IsAnonymous = survey.IsAnonymous,
                    SendReminder = survey.SendReminder,
                    StartDate = survey.StartDate,
                    EndDate = survey.EndDate,
                    PulseFrequency = survey.PulseFrequency,
                    Questions =
                    [
                        .. questions
                            .Where(question => question.SurveyId == survey.Id)
                            .Select(question => new SurveyQuestionDto
                            {
                                Id = question.Id,
                                Text = question.Text,
                                QuestionType = question.QuestionType,
                                Required = question.Required,
                                Options = question.Options,
                                ScaleMin = question.ScaleMin,
                                ScaleMax = question.ScaleMax,
                                Sequence = question.Sequence,
                            }),
                    ],
                    TotalTargeted = survey.TotalTargeted,
                    Responses = responses,
                    ResponseRate = SurveyRules.ResponseRate(responses, survey.TotalTargeted),
                    HaveIAnswered = mine.Contains(survey.Id),
                    CreatedByEmployeeId = survey.CreatedByEmployeeId,
                    CreatedByName = authors.GetValueOrDefault(survey.CreatedByEmployeeId),
                    CreatedAt = survey.CreatedAt,
                    UpdatedAt = survey.UpdatedAt,
                };
            }),
        ];
    }

    private static void Apply(Survey survey, SurveyRequest request, DateTimeOffset now)
    {
        survey.Title = request.Title?.Trim() ?? string.Empty;
        survey.Description = request.Description?.Trim() ?? string.Empty;
        survey.SurveyType = request.SurveyType;
        survey.Audience = request.Audience;
        survey.IsAnonymous = request.IsAnonymous;
        survey.SendReminder = request.SendReminder;
        survey.StartDate = request.StartDate;
        survey.EndDate = request.EndDate;
        survey.PulseFrequency = request.PulseFrequency;
        survey.Status = request.Status;

        // Replaced rather than mutated: the jsonb converter compares by
        // serialising, so an in-place edit is not seen as a change.
        survey.TargetDepartmentIds = [.. (request.TargetDepartmentIds ?? []).Distinct()];
        survey.UpdatedAt = now;
    }

    private static List<SurveyQuestion> Build(
        Survey survey,
        IReadOnlyList<SurveyQuestionRequest> questions)
    {
        int sequence = 0;

        return
        [
            .. questions.Select(question => new SurveyQuestion
            {
                Id = Guid.NewGuid(),
                TenantId = survey.TenantId,
                SurveyId = survey.Id,
                Text = question.Text?.Trim() ?? string.Empty,
                QuestionType = question.QuestionType,
                Required = question.Required,
                Options = [.. (question.Options ?? []).Where(
                    option => !string.IsNullOrWhiteSpace(option)).Select(option => option.Trim())],
                ScaleMin = question.ScaleMin,
                ScaleMax = question.ScaleMax,
                Sequence = sequence++,
            }),
        ];
    }

    // Who the survey went to. All staff, one of the named departments, or
    // anybody with somebody reporting to them.
    private async Task<bool> IsInAudienceAsync(
        Survey survey,
        Respondent who,
        CancellationToken cancellationToken) =>
        survey.Audience switch
        {
            SurveyAudience.Department =>
                who.DepartmentId is Guid id && survey.TargetDepartmentIds.Contains(id),

            SurveyAudience.Managers => await dbContext.Employees.AnyAsync(
                employee => employee.ManagerId == who.Id
                    && EmployeeLifecycle.InPost.Contains(employee.Status),
                cancellationToken),

            _ => true,
        };

    private async Task<int> EligibleCountAsync(
        Survey survey,
        CancellationToken cancellationToken)
    {
        IQueryable<Employee> inPost = dbContext.Employees
            .AsNoTracking()
            .Where(employee => EmployeeLifecycle.InPost.Contains(employee.Status));

        return survey.Audience switch
        {
            SurveyAudience.Department => await inPost.CountAsync(
                employee => employee.DepartmentId != null
                    && survey.TargetDepartmentIds.Contains(employee.DepartmentId.Value),
                cancellationToken),

            SurveyAudience.Managers => await inPost
                .Where(employee => dbContext.Employees.Any(
                    report => report.ManagerId == employee.Id
                        && EmployeeLifecycle.InPost.Contains(report.Status)))
                .CountAsync(cancellationToken),

            _ => await inPost.CountAsync(cancellationToken),
        };
    }

    private Task<Respondent?> RespondentAsync(Guid employeeId, CancellationToken cancellationToken) =>
        dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId
                && EmployeeLifecycle.InPost.Contains(employee.Status))
            .Select(employee => new Respondent(employee.Id, employee.DepartmentId))
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<Dictionary<Guid, string>> DepartmentsAsync(
        CancellationToken cancellationToken) =>
        await dbContext.Departments
            .AsNoTracking()
            .ToDictionaryAsync(
                department => department.Id,
                department => department.Name,
                cancellationToken);

    private static string Escaped(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        TenantClock.DayOf(timeProvider.GetUtcNow(), await ZoneAsync(cancellationToken));

    private async Task<string?> ZoneAsync(CancellationToken cancellationToken)
    {
        if (_zone is not null)
        {
            return _zone;
        }

        if (currentTenant.TenantId is not Guid tenantId)
        {
            return null;
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        _zone = settings?.TimeZoneId;

        return _zone;
    }

    private sealed record Respondent(Guid Id, Guid? DepartmentId);
}
