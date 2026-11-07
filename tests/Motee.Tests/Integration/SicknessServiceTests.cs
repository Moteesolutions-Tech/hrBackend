using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Organisation;
using Motee.Application.Sickness;
using Motee.Domain.Employees;
using Motee.Domain.Leave;
using Motee.Domain.Organisation;
using Motee.Domain.Sickness;
using Motee.Infrastructure.Persistence;
using Motee.Tests.Alerts;

namespace Motee.Tests.Integration;

// Absence records end to end.
//
// Three things here are worth more than the rest. The fit-note clock has to
// move on its own, because nothing in the product will go back and edit a
// record that has simply gone on longer than expected. The medical redaction
// has to hold, because the consequence of getting it wrong is a manager
// reading a colleague's mental-health disclosure. And the Bradford score has
// to come out of the database the same as it comes out of the arithmetic —
// the unit tests prove the formula, these prove the plumbing feeds it the
// right work pattern and the right holidays.
//
// Written before the service.
[Collection(PostgresCollection.Name)]
public class SicknessServiceTests(PostgresFixture fixture)
{
    // A Tuesday, chosen so the week either side is ordinary.
    private static readonly DateOnly Today = new(2026, 6, 30);

    private readonly MovableClock _clock = new(
        new DateTimeOffset(Today.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero));

    private Guid _tenantId;
    private Guid _engineeringId;
    private Guid _salesId;

    private Guid _caraId;
    private Guid _bolaId;
    private Guid _deleId;

    private async Task<ServiceProvider> ArrangeAsync()
    {
        await fixture.ResetAsync();

        RegisterTenantResult registration = await fixture.RegisterAsync(new RegisterTenantRequest
        {
            FirstName = "Ada",
            LastName = "Okafor",
            Email = "ada@acme.com",
            CompanyName = "Acme Corporation",
            Password = "correct-horse",
            CountryCode = "NG",
        });

        Assert.True(registration.Succeeded, string.Join("; ", registration.Errors));

        _tenantId = registration.TenantId;

        ServiceProvider provider = fixture.BuildProvider(
            services => services.AddSingleton<TimeProvider>(_clock));

        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = _tenantId;

        IDepartmentService departments = Resolve<IDepartmentService>(provider);

        _engineeringId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Engineering", Code = "ENG" }))
            .Department!.Id;

        _salesId = (await departments
            .CreateAsync(new DepartmentRequest { Name = "Sales", Code = "SAL" }))
            .Department!.Id;

        _caraId = await HireAsync(provider, "Cara", _engineeringId);
        _bolaId = await HireAsync(provider, "Bola", _engineeringId, managerId: _caraId);
        _deleId = await HireAsync(provider, "Dele", _salesId);

        return provider;
    }

    private static async Task<Guid> HireAsync(
        ServiceProvider provider,
        string first,
        Guid departmentId,
        Guid? managerId = null) =>
        (await Resolve<IEmployeeService>(provider).CreateAsync(new EmployeeRequest
        {
            FirstName = first,
            LastName = "Okafor",
            Email = $"{first.ToLowerInvariant()}@acme.com",
            Phone = "08012345678",
            JobTitle = "Engineer",
            DepartmentId = departmentId,
            ManagerId = managerId,
            EmploymentType = EmploymentType.FullTime,
            Status = EmployeeStatus.Active,
            StartDate = new DateOnly(2022, 1, 10),
        })).Employee!.Id;

    private static T Resolve<T>(ServiceProvider provider) where T : notnull =>
        provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static ISicknessService Sickness(ServiceProvider provider) =>
        Resolve<ISicknessService>(provider);

    private SicknessRequest Illness(
        Guid? employeeId = null,
        string start = "2026-06-29",
        string? end = null,
        SicknessKind kind = SicknessKind.Sickness,
        string? detail = "Streaming cold, lost my voice") => new()
        {
            EmployeeId = employeeId ?? _bolaId,
            Kind = kind,
            StartDate = DateOnly.Parse(start),
            EndDate = end is null ? null : DateOnly.Parse(end),
            Reason = SicknessReason.ColdOrFlu,
            ReasonDetail = detail,
            Source = SicknessSource.Self,
        };

    private static async Task<SicknessRecordDto> ReportedAsync(
        ServiceProvider provider,
        SicknessRequest request)
    {
        SicknessResult result = await Sickness(provider).ReportAsync(request);

        Assert.True(result.Succeeded, result.Outcome.ToString());

        return result.Record!;
    }

    private static Task<SicknessRecordDto?> ReadAsync(
        ServiceProvider provider,
        Guid id,
        bool medical = true) =>
        Sickness(provider).GetAsync(id, medical);

    // ── reporting ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AReportedAbsenceWaitsForApprovalAndIsOngoing()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(provider, Illness());

        Assert.Equal(SicknessApprovalStatus.AwaitingApproval, record.ApprovalStatus);
        Assert.Equal(AbsenceState.Ongoing, record.AbsenceState);
        Assert.Equal(SicknessSource.Self, record.Source);
        Assert.Equal("Bola Okafor", record.EmployeeName);
        Assert.Equal("Engineering", record.DepartmentName);
        Assert.Equal(_caraId, record.ManagerId);
    }

    // Logged after the fact — somebody off on Monday and back on Wednesday,
    // keyed in on Thursday. The return-to-work opens straight away.
    [SkippableFact]
    public async Task AnAbsenceReportedAfterTheyAreBackOpensTheReturnToWork()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(
            provider, Illness(start: "2026-06-22", end: "2026-06-24"));

        Assert.Equal(AbsenceState.Returned, record.AbsenceState);
        Assert.Equal(RtwStatus.Pending, record.ReturnToWork.Status);
        Assert.Equal(new DateOnly(2026, 6, 24), record.ReturnToWork.LastSickDay);
    }

    [SkippableFact]
    public async Task AnAbsenceEndingBeforeItStartsIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            SicknessOutcome.EndBeforeStart,
            (await Sickness(provider).ReportAsync(
                Illness(start: "2026-06-24", end: "2026-06-22"))).Outcome);
    }

    [SkippableFact]
    public async Task AnAbsenceForSomebodyWhoDoesNotWorkHereIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        Assert.Equal(
            SicknessOutcome.EmployeeNotFound,
            (await Sickness(provider).ReportAsync(Illness(employeeId: Guid.NewGuid()))).Outcome);
    }

    // Two records over the same days double every figure and, worse, square
    // the spell count into the Bradford score.
    [SkippableFact]
    public async Task ASecondAbsenceOverTheSameDaysIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-06-22", end: "2026-06-26"));

        Assert.Equal(
            SicknessOutcome.OverlapsAnother,
            (await Sickness(provider).ReportAsync(
                Illness(start: "2026-06-24", end: "2026-06-29"))).Outcome);
    }

    [SkippableFact]
    public async Task AnAbsenceButtingUpAgainstAnotherIsFine()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-06-22", end: "2026-06-24"));

        Assert.True((await Sickness(provider).ReportAsync(
            Illness(start: "2026-06-25", end: "2026-06-26"))).Succeeded);
    }

    // A cancelled record is not an absence, so it cannot block a corrected one
    // being entered over the same days.
    [SkippableFact]
    public async Task ACancelledAbsenceDoesNotBlockItsReplacement()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto wrong = await ReportedAsync(
            provider, Illness(start: "2026-06-22", end: "2026-06-26"));

        Assert.True((await Sickness(provider).CancelAsync(wrong.Id, "Keyed against the wrong person"))
            .Succeeded);

        Assert.True((await Sickness(provider).ReportAsync(
            Illness(start: "2026-06-22", end: "2026-06-26"))).Succeeded);
    }

    // ── approving ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnAbsenceIsApproved()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(provider, Illness());
        SicknessResult approved = await Sickness(provider).ApproveAsync(record.Id);

        Assert.Equal(SicknessApprovalStatus.Approved, approved.Record!.ApprovalStatus);
    }

    [SkippableFact]
    public async Task AnAbsenceAlreadyApprovedIsNotApprovedTwice()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(provider, Illness());
        await Sickness(provider).ApproveAsync(record.Id);

        SicknessResult again = await Sickness(provider).ApproveAsync(record.Id);

        Assert.Equal(SicknessOutcome.Refused, again.Outcome);
        Assert.Equal(SicknessRefusal.NotAwaitingApproval, again.Refusal);
    }

    [SkippableFact]
    public async Task RejectingAnAbsenceNeedsAReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(provider, Illness());

        Assert.Equal(
            SicknessOutcome.ReasonRequired,
            (await Sickness(provider).RejectAsync(record.Id, "  ")).Outcome);
    }

    // ── coming back ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task RecordingAReturnClosesTheAbsenceAndOpensTheInterview()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(provider, Illness(start: "2026-06-22"));

        SicknessResult back = await Sickness(provider).ReturnAsync(record.Id, new ReturnRequest
        {
            LastSickDay = new DateOnly(2026, 6, 26),
            ReturnDate = new DateOnly(2026, 6, 29),
        });

        Assert.True(back.Succeeded, back.Outcome.ToString());
        Assert.Equal(AbsenceState.Returned, back.Record!.AbsenceState);
        Assert.Equal(new DateOnly(2026, 6, 26), back.Record.EndDate);
        Assert.Equal(RtwStatus.Pending, back.Record.ReturnToWork.Status);
        Assert.Equal(new DateOnly(2026, 6, 29), back.Record.ReturnToWork.ReturnDate);
    }

    [SkippableFact]
    public async Task AReturnBeforeTheAbsenceStartedIsRefused()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(provider, Illness(start: "2026-06-22"));

        SicknessResult back = await Sickness(provider).ReturnAsync(
            record.Id, new ReturnRequest { LastSickDay = new DateOnly(2026, 6, 20) });

        Assert.Equal(SicknessRefusal.LastDayBeforeStart, back.Refusal);
    }

    [SkippableFact]
    public async Task AReturnToWorkIsSignedOff()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(
            provider, Illness(start: "2026-06-22", end: "2026-06-26"));

        SicknessResult done = await Sickness(provider).CompleteReturnToWorkAsync(
            record.Id,
            new ReturnToWorkRequest
            {
                InterviewHeld = true,
                InterviewDate = new DateOnly(2026, 6, 29),
                InterviewerEmployeeId = _caraId,
                SafeToReturn = true,
                Notes = "Phased start agreed for the first week.",
                AdjustmentsRecommended = true,
            });

        Assert.True(done.Succeeded, done.Outcome.ToString());
        Assert.Equal(RtwStatus.Completed, done.Record!.ReturnToWork.Status);
        Assert.True(done.Record.ReturnToWork.AdjustmentsRecommended);
        Assert.Equal("Cara Okafor", done.Record.ReturnToWork.InterviewerName);
        Assert.NotNull(done.Record.ReturnToWork.CompletedAt);
    }

    [SkippableFact]
    public async Task AReturnToWorkIsNotSignedOffWithoutTheSafetyCheck()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(
            provider, Illness(start: "2026-06-22", end: "2026-06-26"));

        SicknessResult done = await Sickness(provider).CompleteReturnToWorkAsync(
            record.Id, new ReturnToWorkRequest { InterviewHeld = true, SafeToReturn = false });

        Assert.Equal(SicknessRefusal.SafetyNotConfirmed, done.Refusal);
    }

    // ── medical confidentiality ─────────────────────────────────────────────

    // A manager is entitled to know somebody is off. They are not entitled to
    // know it is a mental-health absence, and they are certainly not entitled
    // to the sentence the person typed.
    [SkippableFact]
    public async Task WithoutTheMedicalGrantTheClinicalDetailIsWithheld()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(provider, Illness());

        SicknessRecordDto redacted = (await ReadAsync(provider, record.Id, medical: false))!;

        Assert.Null(redacted.Reason);
        Assert.Null(redacted.ReasonDetail);
        Assert.True(redacted.MedicalDetailWithheld);

        // Everything a manager needs to run a team is still there.
        Assert.Equal("Bola Okafor", redacted.EmployeeName);
        Assert.Equal(new DateOnly(2026, 6, 29), redacted.StartDate);
        Assert.Equal(AbsenceState.Ongoing, redacted.AbsenceState);
    }

    [SkippableFact]
    public async Task WithTheMedicalGrantTheClinicalDetailIsThere()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(provider, Illness());
        SicknessRecordDto full = (await ReadAsync(provider, record.Id))!;

        Assert.Equal(SicknessReason.ColdOrFlu, full.Reason);
        Assert.Equal("Streaming cold, lost my voice", full.ReasonDetail);
        Assert.False(full.MedicalDetailWithheld);
    }

    [SkippableFact]
    public async Task TheListRedactsTheSameWayTheRecordDoes()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness());

        SicknessRecordDto listed = Assert.Single(
            (await Sickness(provider).ListAsync(new SicknessQuery())).Items);

        Assert.Null(listed.Reason);
        Assert.True(listed.MedicalDetailWithheld);
    }

    // ── fit notes ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AShortAbsenceIsSelfCertified()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(
            provider, Illness(start: "2026-06-29", end: "2026-06-30"));

        Assert.Equal(FitNoteStatus.SelfCertified, record.FitNoteStatus);
    }

    // The clock the frontend does not have: nobody edits the record, the
    // absence simply runs past the self-certification limit.
    [SkippableFact]
    public async Task AnOngoingAbsenceStartsAskingForAFitNoteOnItsOwn()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(provider, Illness(start: "2026-06-29"));

        Assert.Equal(FitNoteStatus.SelfCertified, record.FitNoteStatus);

        _clock.Advance(TimeSpan.FromDays(10));

        Assert.Equal(
            FitNoteStatus.Awaiting, (await ReadAsync(provider, record.Id))!.FitNoteStatus);
    }

    [SkippableFact]
    public async Task AFitNoteOnTheRecordSettlesIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(
            provider, Illness(start: "2026-06-01", end: "2026-06-20"));

        Assert.Equal(FitNoteStatus.Awaiting, record.FitNoteStatus);

        SicknessResult withNote = await Sickness(provider).AddDocumentAsync(
            record.Id,
            new SicknessDocumentRequest
            {
                Kind = SicknessDocumentKind.FitNote,
                Name = "fit-note-june.pdf",
                ValidFrom = new DateOnly(2026, 6, 1),
                ValidTo = new DateOnly(2026, 6, 20),
            });

        Assert.True(withNote.Succeeded, withNote.Outcome.ToString());
        Assert.Equal(FitNoteStatus.Received, withNote.Record!.FitNoteStatus);
        Assert.Single(withNote.Record.Documents);
    }

    [SkippableFact]
    public async Task AFitNoteThatRanOutIsExpired()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(
            provider, Illness(start: "2026-06-01", end: "2026-06-20"));

        SicknessResult withNote = await Sickness(provider).AddDocumentAsync(
            record.Id,
            new SicknessDocumentRequest
            {
                Kind = SicknessDocumentKind.FitNote,
                Name = "fit-note-june.pdf",
                ValidTo = new DateOnly(2026, 6, 10),
            });

        Assert.Equal(FitNoteStatus.Expired, withNote.Record!.FitNoteStatus);
    }

    // ── exclusions ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnAbsenceIsExcludedFromScoringWithAReasonAndAName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(
            provider, Illness(start: "2026-06-01", end: "2026-06-05"));

        SicknessResult excluded = await Sickness(provider).ExcludeAsync(
            record.Id,
            new ExclusionRequest
            {
                Category = ProtectedCategory.Disability,
                Reason = "Flare-up of a condition covered by a reasonable adjustment",
            });

        Assert.True(excluded.Succeeded, excluded.Outcome.ToString());
        Assert.Equal(ProtectedCategory.Disability, excluded.Record!.ExclusionCategory);

        BradfordDto score = await Sickness(provider).BradfordAsync(_bolaId);

        Assert.Equal(0m, score.Score);
        Assert.Contains("adjustment", Assert.Single(score.Excluded).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AnExclusionNeedsAReason()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(provider, Illness());

        Assert.Equal(
            SicknessOutcome.ReasonRequired,
            (await Sickness(provider).ExcludeAsync(
                record.Id,
                new ExclusionRequest { Category = ProtectedCategory.Other, Reason = " " }))
                .Outcome);
    }

    [SkippableFact]
    public async Task AnExclusionIsLifted()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(
            provider, Illness(start: "2026-06-01", end: "2026-06-05"));

        await Sickness(provider).ExcludeAsync(
            record.Id,
            new ExclusionRequest { Category = ProtectedCategory.Other, Reason = "Entered in error" });

        SicknessResult cleared = await Sickness(provider).ClearExclusionAsync(record.Id);

        Assert.Null(cleared.Record!.ExclusionCategory);
        Assert.True((await Sickness(provider).BradfordAsync(_bolaId)).Score > 0m);
    }

    // ── Bradford, from the database ─────────────────────────────────────────

    [SkippableFact]
    public async Task TheScoreComesOutOfTheRecordsAsTheFormulaSays()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        // Two spells of five working days each: S=2, D=10, B=40.
        await ReportedAsync(provider, Illness(start: "2026-03-02", end: "2026-03-06"));
        await ReportedAsync(provider, Illness(start: "2026-04-13", end: "2026-04-17"));

        BradfordDto score = await Sickness(provider).BradfordAsync(_bolaId);

        Assert.Equal(2, score.Spells);
        Assert.Equal(10m, score.Days);
        Assert.Equal(40m, score.Score);
        Assert.Equal(BradfordAction.Monitor, score.Action);
        Assert.Equal("Bola Okafor", score.EmployeeName);
    }

    // The plumbing test: the engine is only right if it is handed the right
    // closure days. A holiday inside an absence is not a day off sick.
    [SkippableFact]
    public async Task PublicHolidaysComeOutOfTheScore()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await SeedAsync(new PublicHoliday
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            Date = new DateOnly(2026, 3, 4),
            Name = "Mid-week closure",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await ReportedAsync(provider, Illness(start: "2026-03-02", end: "2026-03-06"));

        Assert.Equal(4m, (await Sickness(provider).BradfordAsync(_bolaId)).Days);
    }

    [SkippableFact]
    public async Task OneAnothersAbsencesDoNotMix()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-03-02", end: "2026-03-06"));
        await ReportedAsync(
            provider, Illness(employeeId: _deleId, start: "2026-03-02", end: "2026-03-06"));

        Assert.Equal(1, (await Sickness(provider).BradfordAsync(_bolaId)).Spells);
    }

    [SkippableFact]
    public async Task TheBoardRanksPeopleWorstFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        // Bola: four short spells — S=4, D=8, B=128.
        await ReportedAsync(provider, Illness(start: "2026-01-12", end: "2026-01-13"));
        await ReportedAsync(provider, Illness(start: "2026-02-09", end: "2026-02-10"));
        await ReportedAsync(provider, Illness(start: "2026-03-09", end: "2026-03-10"));
        await ReportedAsync(provider, Illness(start: "2026-04-13", end: "2026-04-14"));

        // Dele: one long spell — S=1, D=10, B=10.
        await ReportedAsync(
            provider, Illness(employeeId: _deleId, start: "2026-03-02", end: "2026-03-13"));

        IReadOnlyList<BradfordRowDto> board =
            await Sickness(provider).BradfordBoardAsync(new BradfordBoardQuery());

        Assert.Equal(_bolaId, board[0].EmployeeId);
        Assert.Equal(128m, board[0].Score);
        Assert.Equal(BradfordAction.ManagerReview, board[0].Action);
        Assert.Equal(_deleId, board[1].EmployeeId);
    }

    [SkippableFact]
    public async Task TheBoardLeavesOutPeopleWithNothingToReport()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-03-02", end: "2026-03-06"));

        Assert.DoesNotContain(
            await Sickness(provider).BradfordBoardAsync(new BradfordBoardQuery()),
            row => row.EmployeeId == _caraId);
    }

    [SkippableFact]
    public async Task TheBoardNarrowsToABandAndADepartment()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-01-12", end: "2026-01-13"));
        await ReportedAsync(provider, Illness(start: "2026-02-09", end: "2026-02-10"));
        await ReportedAsync(provider, Illness(start: "2026-03-09", end: "2026-03-10"));
        await ReportedAsync(provider, Illness(start: "2026-04-13", end: "2026-04-14"));

        await ReportedAsync(
            provider, Illness(employeeId: _deleId, start: "2026-03-02", end: "2026-03-13"));

        Assert.Equal(
            _bolaId,
            Assert.Single(await Sickness(provider).BradfordBoardAsync(
                new BradfordBoardQuery { AtLeast = BradfordAction.ManagerReview })).EmployeeId);

        Assert.Equal(
            _deleId,
            Assert.Single(await Sickness(provider).BradfordBoardAsync(
                new BradfordBoardQuery { DepartmentId = _salesId })).EmployeeId);
    }

    // ── the figures every screen shares ─────────────────────────────────────

    [SkippableFact]
    public async Task TheSummaryCountsEpisodesPeopleAndDaysLost()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-06-01", end: "2026-06-05"));
        await ReportedAsync(
            provider, Illness(employeeId: _deleId, start: "2026-06-08", end: "2026-06-09"));

        SicknessSummaryDto summary = await Sickness(provider).SummaryAsync(
            new DateOnly(2026, 6, 1));

        Assert.Equal(2, summary.Episodes);
        Assert.Equal(2, summary.People);
        Assert.Equal(7m, summary.DaysLost);
        Assert.Equal(3.5m, summary.AverageSpellDays);
        Assert.Equal(Today, summary.AsOf);
    }

    [SkippableFact]
    public async Task ARejectedAbsenceIsInNoFigures()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        SicknessRecordDto record = await ReportedAsync(
            provider, Illness(start: "2026-06-01", end: "2026-06-05"));

        await Sickness(provider).RejectAsync(record.Id, "No contact, no evidence");

        SicknessSummaryDto summary = await Sickness(provider).SummaryAsync(
            new DateOnly(2026, 6, 1));

        Assert.Equal(0, summary.Episodes);
        Assert.Equal(0m, summary.DaysLost);
    }

    [SkippableFact]
    public async Task LongAndShortTermAbsenceAreCountedApart()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-04-01", end: "2026-05-20"));
        await ReportedAsync(
            provider, Illness(employeeId: _deleId, start: "2026-06-08", end: "2026-06-09"));

        SicknessSummaryDto summary = await Sickness(provider).SummaryAsync(
            new DateOnly(2026, 4, 1));

        Assert.Equal(1, summary.LongTerm.Count);
        Assert.Equal(1, summary.ShortTerm.Count);
    }

    // ── what needs chasing ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnOutstandingFitNoteRaisesAnAlert()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-06-01"));

        SicknessAlertDto alert = Assert.Single(
            await Sickness(provider).AlertsAsync(),
            candidate => candidate.Kind == SicknessAlertKind.FitNoteOutstanding);

        Assert.Equal(_bolaId, alert.EmployeeId);

        // Still off and still unevidenced is the urgent one.
        Assert.True(alert.Urgent);
        Assert.Contains("Bola Okafor", alert.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AReturnToWorkLeftUndoneRaisesAnAlert()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-06-01", end: "2026-06-03"));

        Assert.Contains(
            await Sickness(provider).AlertsAsync(),
            alert => alert.Kind == SicknessAlertKind.ReturnToWorkOverdue);
    }

    [SkippableFact]
    public async Task AReturnToWorkOnlyJustDueIsNotYetOverdue()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-06-26", end: "2026-06-29"));

        Assert.DoesNotContain(
            await Sickness(provider).AlertsAsync(),
            alert => alert.Kind == SicknessAlertKind.ReturnToWorkOverdue);
    }

    // ── listing ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheListNarrowsToOnePersonsAbsences()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-06-01", end: "2026-06-05"));
        await ReportedAsync(
            provider, Illness(employeeId: _deleId, start: "2026-06-08", end: "2026-06-09"));

        PagedResult<SicknessRecordDto> mine = await Sickness(provider).ListAsync(
            new SicknessQuery { EmployeeId = _bolaId }, canViewMedical: true);

        Assert.Equal(_bolaId, Assert.Single(mine.Items).EmployeeId);
    }

    // An absence already under way belongs in this month's list, not only the
    // ones that began in it.
    [SkippableFact]
    public async Task TheListFindsAbsencesStraddlingTheWindow()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-05-25", end: "2026-06-03"));

        PagedResult<SicknessRecordDto> june = await Sickness(provider).ListAsync(
            new SicknessQuery { From = new DateOnly(2026, 6, 1), To = new DateOnly(2026, 6, 30) });

        Assert.Single(june.Items);
    }

    [SkippableFact]
    public async Task TheListFindsWhatIsWaitingForAReturnToWork()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-06-01", end: "2026-06-03"));
        await ReportedAsync(provider, Illness(start: "2026-06-22"));

        PagedResult<SicknessRecordDto> due = await Sickness(provider).ListAsync(
            new SicknessQuery { ReturnToWorkPending = true });

        Assert.Equal(new DateOnly(2026, 6, 1), Assert.Single(due.Items).StartDate);
    }

    [SkippableFact]
    public async Task TheListFindsOutstandingFitNotes()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-06-01", end: "2026-06-20"));
        await ReportedAsync(provider, Illness(start: "2026-06-28", end: "2026-06-29"));

        PagedResult<SicknessRecordDto> chase = await Sickness(provider).ListAsync(
            new SicknessQuery { FitNoteOutstanding = true });

        Assert.Equal(new DateOnly(2026, 6, 1), Assert.Single(chase.Items).StartDate);
    }

    [SkippableFact]
    public async Task TheListSearchesByName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-06-01", end: "2026-06-05"));
        await ReportedAsync(
            provider, Illness(employeeId: _deleId, start: "2026-06-08", end: "2026-06-09"));

        PagedResult<SicknessRecordDto> found = await Sickness(provider).ListAsync(
            new SicknessQuery { Search = "dele" });

        Assert.Equal(_deleId, Assert.Single(found.Items).EmployeeId);
    }

    [SkippableFact]
    public async Task TheNewestAbsenceIsListedFirst()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        await using ServiceProvider provider = await ArrangeAsync();

        await ReportedAsync(provider, Illness(start: "2026-05-04", end: "2026-05-06"));
        await ReportedAsync(provider, Illness(start: "2026-06-01", end: "2026-06-03"));

        PagedResult<SicknessRecordDto> all = await Sickness(provider).ListAsync(new SicknessQuery());

        Assert.Equal(new DateOnly(2026, 6, 1), all.Items[0].StartDate);
    }

    private async Task SeedAsync<T>(T row) where T : class
    {
        await using MoteeDbContext seed = fixture.CreateContext(_tenantId);

        seed.Add(row);

        await seed.SaveChangesAsync();
    }
}
