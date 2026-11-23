using Motee.Domain.Common;

namespace Motee.Domain.Relations;

// One case model covers grievance, disciplinary and everything adjacent. Two
// screens and three permission keys read it: HR's case list, the admin
// grievance view, and the employee's own "raise something" form.
public enum CaseComplaintType
{
    Grievance,
    Disciplinary,
    Harassment,
    Discrimination,
    PayDispute,
    Misconduct,
    Attendance,
    PolicyViolation,
    WorkingConditions,
    BullyingHarassment,
    AbsenceManagement,
    PerformanceImprovement,
    Capability,
    Whistleblowing,
    HealthSafety,
    EqualityDiversity,
    Appeal,
    Investigation,
    Safeguarding,
}

// The full linear order. A kind of case walks a subset of it — see CaseFlows.
public enum CaseStage
{
    Raised,
    Triage,
    Assigned,
    Investigation,
    Hearing,
    OutcomeIssued,
    Appeal,
    Closed,
}

public enum ConfidentialityLevel
{
    Standard,
    Confidential,
    HighlyConfidential,

    // Tighter than highly confidential: the people named on the case, and
    // nobody else. Enforced rather than displayed — see CaseAccess.
    Restricted,
}

public enum CaseOutcome
{
    VerbalWarning,
    WrittenWarning,
    FinalWrittenWarning,
    Suspension,
    Demotion,
    Termination,
    NoAction,
    Upheld,
    PartiallyUpheld,
    NotUpheld,
    Resolved,
    NoCaseToAnswer,
    InformalResolution,
    Mediation,
    TrainingRequired,
    PolicyUpdate,
}

public enum CasePriority
{
    Low,
    Medium,
    High,
    Urgent,
}

// Who a note reaches. A single "internal" flag could not express the middle
// ground where the case team sees a note and the person it is about does not.
public enum NoteVisibility
{
    HrOnly,
    CaseTeam,
    EmployeeVisible,
}

public enum CaseMeetingKind
{
    Investigation,
    DisciplinaryHearing,
    GrievanceMeeting,
    AppealHearing,
}

public enum SlaState
{
    None,
    OnTrack,
    DueSoon,
    Overdue,
}

// Somebody who saw it. A name and a statement rather than a link: a witness is
// often not an employee, and a case that cannot record an outside witness is
// not a case record.
public sealed record CaseWitness
{
    public required string Name { get; init; }

    public string? Statement { get; init; }
}

// An employee-relations case.
//
// Days open and where it stands against its target are derived, never stored: a
// stored "overdue" is wrong the next morning, and a stored day count keeps
// climbing on a case that closed last month.
public class ErCase : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // Minted per company rather than from the length of a list, which hands two
    // people raising a case at the same moment the same number.
    public required string CaseNumber { get; set; }

    public CaseComplaintType ComplaintType { get; set; }

    // Who the case is about.
    public Guid? SubjectEmployeeId { get; set; }

    // Who raised it. Null when HR opened it themselves.
    public Guid? RaisedByEmployeeId { get; set; }

    public DateOnly RaisedOn { get; set; }

    public DateOnly? IncidentOn { get; set; }

    public required string Description { get; set; }

    public CaseStage Stage { get; set; } = CaseStage.Raised;

    public CasePriority Priority { get; set; } = CasePriority.Medium;

    public ConfidentialityLevel ConfidentialityLevel { get; set; } =
        ConfidentialityLevel.Standard;

    // Does the digging.
    public Guid? InvestigatorEmployeeId { get; set; }

    // Accountable for the case, and deliberately a different person.
    public Guid? CaseOwnerEmployeeId { get; set; }

    // The dates the case is measured against.
    public DateOnly? TargetResolutionOn { get; set; }

    public DateOnly? InvestigationDueOn { get; set; }

    public DateOnly? OutcomeDueOn { get; set; }

    // Statements taken. jsonb: read with the case, counted by the stage gate,
    // and never queried on its own.
    public List<CaseWitness> Witnesses { get; set; } = [];

    // Evidence is rows rather than jsonb, because each piece is a file and
    // files go through the one upload path. Carried here so the stage gate can
    // ask whether there is any without a second query.
    public int EvidenceCount { get; set; }

    public DateOnly? HearingOn { get; set; }

    public List<Guid> HearingPanelEmployeeIds { get; set; } = [];

    public CaseOutcome? Outcome { get; set; }

    public DateOnly? OutcomeOn { get; set; }

    public int? SuspensionDays { get; set; }

    public bool HasAppeal { get; set; }

    public Guid? AppealReviewerEmployeeId { get; set; }

    public string? AppealGrounds { get; set; }

    // Set when the case reaches Closed, cleared if it is reopened.
    public DateOnly? ClosedOn { get; set; }

    // How long the file is kept after closure, which is a legal question
    // somebody answers rather than one this works out.
    public string? RetentionPeriod { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public class CaseNote : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CaseId { get; set; }

    public Guid? AuthorUserId { get; set; }

    public required string Content { get; set; }

    public NoteVisibility Visibility { get; set; } = NoteVisibility.HrOnly;

    public DateTimeOffset CreatedAt { get; set; }
}

public class CaseMeeting : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CaseId { get; set; }

    public CaseMeetingKind MeetingKind { get; set; }

    public DateOnly HeldOn { get; set; }

    public TimeOnly? HeldAt { get; set; }

    // Names rather than links: a companion, a union representative or an
    // outside investigator is not on the payroll.
    public List<string> Attendees { get; set; } = [];

    public string? Notes { get; set; }

    public string? MeetingOutcome { get; set; }

    public Guid? RecordedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public class CaseEvidence : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CaseId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    // The bytes go through the one upload path, which already holds the file
    // name, type, size and storage key.
    public Guid StoredFileId { get; set; }

    public Guid? UploadedByUserId { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}

public enum CaseRefusal
{
    None,
    DescriptionTooShort,
    SubjectRequired,
    IncidentInTheFuture,

    // Investigating yourself is not an investigation.
    InvestigatorIsSubject,
    OwnerIsSubject,

    NoteRequired,
    AttendeesRequired,
    MeetingInTheFuture,

    // The stage cannot be left until what it exists to record is recorded.
    StageNotSatisfied,

    // The kind of case has no such stage.
    StageNotInFlow,

    NotVisible,
    UnknownCase,
}

public sealed record CaseAdvance
{
    public required bool Allowed { get; init; }

    // Why not, so a screen can say what is missing rather than greying out a
    // chip.
    public required IReadOnlyList<string> Blockers { get; init; }

    public static CaseAdvance Yes => new() { Allowed = true, Blockers = [] };

    public static CaseAdvance No(params string[] blockers) =>
        new() { Allowed = false, Blockers = blockers };
}

public static class CaseRules
{
    // A case is a record somebody will be asked to act on.
    public const int ShortestDescription = 10;

    public static CaseRefusal Refusal(ErCase row, DateOnly? today = null)
    {
        if ((row.Description ?? string.Empty).Trim().Length < ShortestDescription)
        {
            return CaseRefusal.DescriptionTooShort;
        }

        if (row.SubjectEmployeeId is not Guid subject)
        {
            return CaseRefusal.SubjectRequired;
        }

        if (today is DateOnly day
            && row.IncidentOn is DateOnly incident
            && incident > day)
        {
            return CaseRefusal.IncidentInTheFuture;
        }

        if (row.InvestigatorEmployeeId == subject)
        {
            return CaseRefusal.InvestigatorIsSubject;
        }

        return row.CaseOwnerEmployeeId == subject
            ? CaseRefusal.OwnerIsSubject
            : CaseRefusal.None;
    }

    public static CaseRefusal NoteRefusal(string? content) =>
        string.IsNullOrWhiteSpace(content) ? CaseRefusal.NoteRequired : CaseRefusal.None;

    public static CaseRefusal MeetingRefusal(CaseMeeting meeting, DateOnly today)
    {
        if (meeting.Attendees.Count == 0)
        {
            return CaseRefusal.AttendeesRequired;
        }

        // A hearing minuted for next month has not happened, so there is
        // nothing to record about it yet.
        return meeting.HeldOn > today
            ? CaseRefusal.MeetingInTheFuture
            : CaseRefusal.None;
    }

    // The two facts that follow from a stage change rather than being set
    // alongside it.
    public static void Moved(ErCase row, CaseStage stage, DateOnly today)
    {
        row.Stage = stage;

        row.ClosedOn = stage == CaseStage.Closed ? row.ClosedOn ?? today : null;

        if (stage == CaseStage.Appeal)
        {
            row.HasAppeal = true;
        }
    }
}

// Which stages a kind of case walks, and whether it may move.
public static class CaseFlows
{
    private static readonly CaseStage[] Full = [.. Enum.GetValues<CaseStage>()];

    // A performance case has no formal hearing and an appeal starts at appeal.
    // Forcing every kind through the disciplinary flow is what made the stage
    // tracker meaningless.
    private static readonly Dictionary<CaseComplaintType, CaseStage[]> Flows = new()
    {
        [CaseComplaintType.AbsenceManagement] =
            [CaseStage.Raised, CaseStage.Triage, CaseStage.Assigned, CaseStage.Investigation,
                CaseStage.OutcomeIssued, CaseStage.Closed],

        [CaseComplaintType.PerformanceImprovement] =
            [CaseStage.Raised, CaseStage.Triage, CaseStage.Assigned, CaseStage.OutcomeIssued,
                CaseStage.Closed],

        [CaseComplaintType.Capability] =
            [CaseStage.Raised, CaseStage.Triage, CaseStage.Assigned, CaseStage.Investigation,
                CaseStage.Hearing, CaseStage.OutcomeIssued, CaseStage.Closed],

        [CaseComplaintType.Appeal] =
            [CaseStage.Raised, CaseStage.Assigned, CaseStage.Appeal, CaseStage.OutcomeIssued,
                CaseStage.Closed],

        [CaseComplaintType.Investigation] =
            [CaseStage.Raised, CaseStage.Triage, CaseStage.Assigned, CaseStage.Investigation,
                CaseStage.OutcomeIssued, CaseStage.Closed],

        [CaseComplaintType.Whistleblowing] =
            [CaseStage.Raised, CaseStage.Triage, CaseStage.Assigned, CaseStage.Investigation,
                CaseStage.OutcomeIssued, CaseStage.Closed],

        [CaseComplaintType.HealthSafety] =
            [CaseStage.Raised, CaseStage.Triage, CaseStage.Assigned, CaseStage.Investigation,
                CaseStage.OutcomeIssued, CaseStage.Closed],
    };

    public static IReadOnlyList<CaseStage> For(CaseComplaintType type) =>
        Flows.GetValueOrDefault(type, Full);

    public static CaseAdvance CanAdvanceTo(ErCase row, CaseStage target)
    {
        IReadOnlyList<CaseStage> flow = For(row.ComplaintType);

        int to = PositionOf(flow, target);

        if (to < 0)
        {
            return CaseAdvance.No($"This kind of case has no {Spoken(target)} stage");
        }

        int from = PositionOf(flow, row.Stage);

        // A case whose own stage is off its flow — the kind was changed after it
        // was raised. Measured from the start rather than refused, so it can
        // still be moved somewhere valid instead of being stuck.
        if (from < 0)
        {
            from = 0;
        }

        // Moving back is always allowed: correcting a mis-click should not mean
        // satisfying gates already passed.
        if (to <= from)
        {
            return CaseAdvance.Yes;
        }

        // Every stage between here and the target, so nothing is skipped.
        List<string> blockers = [];

        for (int index = from; index < to; index++)
        {
            blockers.AddRange(CaseGates.Blockers(row, flow[index]));
        }

        return blockers.Count == 0
            ? CaseAdvance.Yes
            : new CaseAdvance { Allowed = false, Blockers = blockers };
    }

    private static int PositionOf(IReadOnlyList<CaseStage> flow, CaseStage stage)
    {
        for (int index = 0; index < flow.Count; index++)
        {
            if (flow[index] == stage)
            {
                return index;
            }
        }

        return -1;
    }

    private static string Spoken(CaseStage stage) => stage switch
    {
        CaseStage.OutcomeIssued => "outcome",
        CaseStage.Raised => "raised",
        _ => stage.ToString().ToLowerInvariant(),
    };
}

// What a stage exists to record.
//
// Returning the reasons rather than a bare refusal is what lets a screen say
// what is actually missing.
public static class CaseGates
{
    public static IReadOnlyList<string> Blockers(ErCase row, CaseStage stage) =>
        stage switch
        {
            CaseStage.Raised => string.IsNullOrWhiteSpace(row.Description)
                ? ["Add a case description"]
                : [],

            CaseStage.Triage => row.TargetResolutionOn is null
                ? ["Set a target resolution date"]
                : [],

            CaseStage.Assigned =>
            [
                .. row.InvestigatorEmployeeId is null
                    ? (string[])["Assign an investigator"]
                    : [],
                .. row.CaseOwnerEmployeeId is null
                    ? (string[])["Name a case owner"]
                    : [],
            ],

            CaseStage.Investigation =>
                row.Witnesses.Count == 0 && row.EvidenceCount == 0
                    ? ["Record at least one witness statement or piece of evidence"]
                    : [],

            CaseStage.Hearing =>
            [
                .. row.HearingOn is null ? (string[])["Record the hearing date"] : [],
                .. row.HearingPanelEmployeeIds.Count == 0
                    ? (string[])["Record who sat on the hearing panel"]
                    : [],
            ],

            CaseStage.OutcomeIssued =>
            [
                .. row.Outcome is null ? (string[])["Record the outcome"] : [],
                .. row.OutcomeOn is null ? (string[])["Record the outcome date"] : [],
            ],

            CaseStage.Appeal => row.HasAppeal && string.IsNullOrWhiteSpace(row.AppealGrounds)
                ? ["Record the grounds of appeal"]
                : [],

            _ => [],
        };
}

public static class CaseSla
{
    // Inside this many days of the target, a case is worth chasing.
    public const int DueSoonDays = 3;

    // Where a case stands against its target.
    //
    // A closed case is judged on whether it actually met the date rather than
    // being reported as on track whatever happened: a case that closed forty
    // days late was not on track, and which ones ran over is the question HR
    // asks of this figure.
    public static SlaState StateOf(ErCase row, DateOnly today)
    {
        if (row.TargetResolutionOn is not DateOnly target)
        {
            return SlaState.None;
        }

        if (row.Stage == CaseStage.Closed)
        {
            DateOnly closed = row.ClosedOn ?? today;

            return closed > target ? SlaState.Overdue : SlaState.OnTrack;
        }

        if (target < today)
        {
            return SlaState.Overdue;
        }

        return target.DayNumber - today.DayNumber <= DueSoonDays
            ? SlaState.DueSoon
            : SlaState.OnTrack;
    }

    // The clock stops when the case closes, so the figure does not keep
    // climbing on something nobody is working on.
    public static int DaysOpen(ErCase row, DateOnly today)
    {
        DateOnly until = row.ClosedOn ?? today;

        return Math.Max(0, until.DayNumber - row.RaisedOn.DayNumber);
    }
}

// Who can read a case, and who can read a note on it.
//
// Confidentiality has to be enforced rather than displayed. The client shows
// the level as a badge and then lists every case to anybody holding the module.
public static class CaseAccess
{
    public static bool CanSee(ErCase row, Guid? viewerEmployeeId, bool holdsModule)
    {
        if (!holdsModule)
        {
            return false;
        }

        if (row.ConfidentialityLevel != ConfidentialityLevel.Restricted)
        {
            return true;
        }

        // Named individuals only. Not the subject: a restricted case is
        // typically about the person rather than for them.
        return viewerEmployeeId is Guid viewer
            && (row.InvestigatorEmployeeId == viewer
                || row.CaseOwnerEmployeeId == viewer
                || row.AppealReviewerEmployeeId == viewer);
    }

    public static bool CanSeeNote(
        NoteVisibility visibility,
        bool isSubject,
        bool isCaseTeam) =>
        visibility switch
        {
            NoteVisibility.EmployeeVisible => isSubject || isCaseTeam,
            NoteVisibility.CaseTeam => isCaseTeam,

            // HR only: neither the person it is about nor the wider case team.
            _ => false,
        };
}

// Which side of the register a kind of case sits on.
//
// The employee record has a grievances tab and a disciplinaries tab, each under
// its own permission key, so the kinds have to be split — a pay dispute belongs
// on one and a capability case on the other.
public enum CaseGroup
{
    // Something raised by or on behalf of the employee.
    Grievance,

    // Something being put to the employee.
    Disciplinary,
}

public static class CaseGroups
{
    private static readonly CaseComplaintType[] Raised =
    [
        CaseComplaintType.Grievance,
        CaseComplaintType.Harassment,
        CaseComplaintType.Discrimination,
        CaseComplaintType.PayDispute,
        CaseComplaintType.WorkingConditions,
        CaseComplaintType.BullyingHarassment,
        CaseComplaintType.Whistleblowing,
        CaseComplaintType.HealthSafety,
        CaseComplaintType.EqualityDiversity,
        CaseComplaintType.Safeguarding,
    ];

    private static readonly CaseComplaintType[] Put =
    [
        CaseComplaintType.Disciplinary,
        CaseComplaintType.Misconduct,
        CaseComplaintType.Attendance,
        CaseComplaintType.PolicyViolation,
        CaseComplaintType.AbsenceManagement,
        CaseComplaintType.PerformanceImprovement,
        CaseComplaintType.Capability,
    ];

    // An appeal or an investigation can be either, so both tabs show them
    // rather than one of them hiding a case that plainly concerns the person.
    public static IReadOnlyList<CaseComplaintType> Of(CaseGroup group) =>
    [
        .. group == CaseGroup.Grievance ? Raised : Put,
        CaseComplaintType.Appeal,
        CaseComplaintType.Investigation,
    ];
}

public static class CaseNumbers
{
    public const string Prefix = "ERC-";

    // The next number after the highest so far. Minted here rather than from
    // the length of a list, which hands two people raising a case at the same
    // moment the same number — and a unique index then refuses one of them.
    public static string Next(string? highest)
    {
        int last = 0;

        if (!string.IsNullOrWhiteSpace(highest)
            && highest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(highest[Prefix.Length..], out int parsed))
        {
            last = parsed;
        }

        // Padded to three so the common case sorts, and allowed to grow past
        // it rather than wrapping at a thousand.
        return $"{Prefix}{last + 1:D3}";
    }
}
