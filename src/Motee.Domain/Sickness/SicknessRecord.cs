using Motee.Domain.Common;

namespace Motee.Domain.Sickness;

// Sickness, or a planned procedure. Kept apart because a booked operation is
// not unplanned absence and must never score against somebody.
public enum SicknessKind
{
    Sickness,
    MedicalProcedure,
}

public enum SicknessApprovalStatus
{
    AwaitingApproval,
    Approved,
    Rejected,

    // Logged in error, or the person turned out not to be off. Distinct from
    // rejected: nobody refused it.
    Cancelled,
}

public enum AbsenceState
{
    Ongoing,
    Returned,
}

public enum SicknessSource
{
    Self,
    Manager,
    Hr,
    TimeAndAttendance,
}

public enum DayPortion
{
    Full,
    Am,
    Pm,
}

public enum FitNoteStatus
{
    NotRequired,
    SelfCertified,
    Awaiting,
    Received,
    Expired,
}

// Clinical grouping, not a diagnosis. The client asked for sickness to stop
// being filed as "Personal", because a company that cannot tell flu from a
// mental-health absence cannot see what is happening to its staff.
public enum SicknessReason
{
    ColdOrFlu,
    Migraine,
    Musculoskeletal,
    MentalHealth,
    MedicalAppointment,
    SurgeryRecovery,
    Other,
}

// Why an absence is treated separately. The Equality Act 2010 requires
// disability- and pregnancy-related absence to be considered apart from the
// rest; scoring it alongside everything else is how somebody reaches a formal
// review for being disabled.
public enum ProtectedCategory
{
    Disability,
    Pregnancy,
    IndustrialInjury,
    AdjustmentRelated,
    Other,
}

public enum RtwStatus
{
    NotDue,
    Pending,
    Completed,
}

public enum RtwInterviewRule
{
    Always,
    AfterDays,
    Never,
}

public enum SicknessDocumentKind
{
    FitNote,
    SelfCertificate,
    HospitalLetter,
    Other,
}

// An upload attached to an absence. Special-category data: a fit note names a
// condition, so these are reachable only through the record, and the record is
// redacted for anybody without the medical grant.
public sealed record SicknessDocument
{
    public required Guid Id { get; init; }

    public required SicknessDocumentKind Kind { get; init; }

    public required string Name { get; init; }

    // In stored_files with every other upload. Null for a document recorded as
    // received without the file itself — a note handed over on paper.
    public Guid? FileId { get; init; }

    public DateOnly? ValidFrom { get; init; }

    public DateOnly? ValidTo { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }

    public Guid? UploadedByEmployeeId { get; init; }
}

// One absence.
//
// The return-to-work block is flat columns rather than a nested object: the
// return-to-work screen filters on its status, and a jsonb column cannot be
// asked which absences are waiting for an interview.
public class SicknessRecord : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public SicknessKind Kind { get; set; }

    public DateOnly StartDate { get; set; }

    public DayPortion StartPortion { get; set; }

    // When they went home, for somebody who started the day and left part-way
    // through.
    public TimeOnly? LeftWorkAt { get; set; }

    // Null while they are still off.
    public DateOnly? EndDate { get; set; }

    public DayPortion EndPortion { get; set; }

    // A night shift belongs to the day it started, or one shift off reads as
    // two days absent.
    public bool OvernightShift { get; set; }

    public DateOnly? ExpectedReturnDate { get; set; }

    public SicknessReason Reason { get; set; } = SicknessReason.Other;

    // What the employee actually said. Special-category data under UK GDPR —
    // never returned without the medical grant, and every reveal is audited.
    public string? ReasonDetail { get; set; }

    public bool Hospitalised { get; set; }

    // When the business first heard. Distinct from ReportedAt, which is when
    // the record was keyed in: a Monday-morning phone call logged on Wednesday
    // was still notified on Monday.
    public DateTimeOffset NotifiedAt { get; set; }

    public SicknessSource Source { get; set; }

    public Guid? ReportedByEmployeeId { get; set; }

    // Kept beside the id so a record still says who logged it after that
    // person has left and their employee row is gone.
    public string? ReportedByName { get; set; }

    public DateTimeOffset ReportedAt { get; set; }

    public SicknessApprovalStatus ApprovalStatus { get; set; } =
        SicknessApprovalStatus.AwaitingApproval;

    public AbsenceState AbsenceState { get; set; } = AbsenceState.Ongoing;

    public List<SicknessDocument> Documents { get; set; } = [];

    // The same illness returning inside the scheme's linking window — one
    // period of incapacity, which is what decides whether sick pay restarts.
    public Guid? LinkedRecordId { get; set; }

    // ── Bradford exclusion ──────────────────────────────────────────────────

    public ProtectedCategory? ExclusionCategory { get; set; }

    public string? ExclusionReason { get; set; }

    public Guid? ExclusionApprovedByEmployeeId { get; set; }

    public DateTimeOffset? ExclusionApprovedAt { get; set; }

    // ── return to work ──────────────────────────────────────────────────────

    public RtwStatus RtwStatus { get; set; } = RtwStatus.NotDue;

    // When they actually came back, which is not always the day after the last
    // sick day — a Friday finish means a Monday return.
    public DateOnly? RtwReturnDate { get; set; }

    public DateOnly? RtwLastSickDay { get; set; }

    public bool RtwInterviewRequired { get; set; }

    public bool RtwInterviewHeld { get; set; }

    public DateOnly? RtwInterviewDate { get; set; }

    public Guid? RtwInterviewerEmployeeId { get; set; }

    public bool RtwSafeToReturn { get; set; }

    public DateTimeOffset? RtwEmployeeConfirmedAt { get; set; }

    public string? RtwNotes { get; set; }

    public bool RtwAdjustmentsRecommended { get; set; }

    public bool RtwOhReferralRecommended { get; set; }

    public Guid? RtwCompletedByEmployeeId { get; set; }

    public DateTimeOffset? RtwCompletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum SicknessRefusal
{
    None,

    // Approving or rejecting something that is not waiting for a decision.
    NotAwaitingApproval,

    // Rejected or cancelled already — there is nothing left to act on.
    AlreadyClosed,

    AlreadyReturned,

    // A last sick day before the absence started.
    LastDayBeforeStart,

    // Back at work on or before the last day they were off.
    ReturnBeforeLastDay,

    // Signing off a return for somebody who has not returned.
    StillOngoing,

    AlreadyCompleted,

    InterviewNotHeld,

    // Nobody has said the person is fit to be back. It is the one question a
    // return-to-work exists to answer.
    SafetyNotConfirmed,

    StartInTheFuture,

    ReasonDetailRequired,
}

public static class SicknessLifecycle
{
    // Absence that happened. Waiting for approval still counts — the person
    // was off whether or not a manager has clicked yet, and leaving it out
    // makes every figure on the dashboard lag the queue.
    public static bool Counts(SicknessApprovalStatus status) =>
        status is SicknessApprovalStatus.AwaitingApproval or SicknessApprovalStatus.Approved;

    public static SicknessRefusal CanApprove(SicknessRecord record) =>
        record.ApprovalStatus == SicknessApprovalStatus.AwaitingApproval
            ? SicknessRefusal.None
            : SicknessRefusal.NotAwaitingApproval;

    public static SicknessRefusal CanReject(SicknessRecord record) => CanApprove(record);

    // Approved absence can still be cancelled. Refusing that would leave days
    // on somebody's Bradford score with no way to take them off.
    public static SicknessRefusal CanCancel(SicknessRecord record) =>
        Counts(record.ApprovalStatus) ? SicknessRefusal.None : SicknessRefusal.AlreadyClosed;

    public static SicknessRefusal CanReturn(
        SicknessRecord record,
        DateOnly lastSickDay,
        DateOnly? returnDate)
    {
        if (!Counts(record.ApprovalStatus))
        {
            return SicknessRefusal.AlreadyClosed;
        }

        if (record.AbsenceState == AbsenceState.Returned)
        {
            return SicknessRefusal.AlreadyReturned;
        }

        if (lastSickDay < record.StartDate)
        {
            return SicknessRefusal.LastDayBeforeStart;
        }

        return returnDate is DateOnly back && back <= lastSickDay
            ? SicknessRefusal.ReturnBeforeLastDay
            : SicknessRefusal.None;
    }

    public static SicknessRefusal CanCompleteReturnToWork(
        SicknessRecord record,
        bool interviewHeld,
        bool safeToReturn)
    {
        if (record.AbsenceState != AbsenceState.Returned)
        {
            return SicknessRefusal.StillOngoing;
        }

        if (record.RtwStatus == RtwStatus.Completed)
        {
            return SicknessRefusal.AlreadyCompleted;
        }

        if (record.RtwInterviewRequired && !interviewHeld)
        {
            return SicknessRefusal.InterviewNotHeld;
        }

        return safeToReturn ? SicknessRefusal.None : SicknessRefusal.SafetyNotConfirmed;
    }

    public static bool InterviewRequired(RtwInterviewRule rule, int spellDays, int afterDays) =>
        rule switch
        {
            RtwInterviewRule.Always => true,
            RtwInterviewRule.Never => false,
            _ => spellDays >= afterDays,
        };
}

// Whether a fit note is needed, and whether the one on file still covers the
// absence.
//
// Derived rather than stored. Stored, it is written once when the absence is
// reported and then never changes, so somebody who called in for three days
// and is still off three weeks later reads "self-certified" and never reaches
// the list of notes to chase — which is the list that exists to catch them.
public static class FitNotes
{
    public static FitNoteStatus StatusOf(
        SicknessRecord record,
        DateOnly asOf,
        int selfCertDays)
    {
        if (record.Kind == SicknessKind.MedicalProcedure
            || !SicknessLifecycle.Counts(record.ApprovalStatus))
        {
            return FitNoteStatus.NotRequired;
        }

        DateOnly covered = Absences.Span(record, asOf).End;

        List<SicknessDocument> notes =
            [.. record.Documents.Where(document => document.Kind == SicknessDocumentKind.FitNote)];

        if (notes.Count > 0)
        {
            // A note with no stated end cannot be judged expired, and guessing
            // it has would chase somebody for a document they already sent.
            if (notes.Exists(note => note.ValidTo is null))
            {
                return FitNoteStatus.Received;
            }

            DateOnly latest = notes.Max(note => note.ValidTo!.Value);

            return latest >= covered ? FitNoteStatus.Received : FitNoteStatus.Expired;
        }

        return Absences.SpellLength(record, asOf) > selfCertDays
            ? FitNoteStatus.Awaiting
            : FitNoteStatus.SelfCertified;
    }

    // The two worth a chase. Received and self-certified are settled, and not
    // required never was.
    public static bool IsOutstanding(FitNoteStatus status) =>
        status is FitNoteStatus.Awaiting or FitNoteStatus.Expired;
}

public static class Absences
{
    // The calendar stretch a record covers, with an ongoing absence running to
    // today and an overnight shift credited to the day it began.
    public static (DateOnly Start, DateOnly End) Span(SicknessRecord record, DateOnly asOf)
    {
        DateOnly end = record.EndDate ?? asOf;

        if (end < record.StartDate)
        {
            end = record.StartDate;
        }

        if (record.OvernightShift && end > record.StartDate)
        {
            end = end.AddDays(-1);
        }

        return (record.StartDate, end);
    }

    public static int SpellLength(SicknessRecord record, DateOnly asOf)
    {
        (DateOnly start, DateOnly end) = Span(record, asOf);

        return end.DayNumber - start.DayNumber + 1;
    }

    // Calendar days, not working ones. "Off for four weeks" is four weeks to
    // everybody involved, and counting working days would make the long-term
    // threshold mean something different for a part-timer.
    public static bool IsLongTerm(SicknessRecord record, DateOnly asOf, int longTermDays) =>
        SpellLength(record, asOf) >= longTermDays;

    // What one absence cost in working time. No window and no merging, so this
    // is the figure for a list or a cost report rather than for a score.
    public static decimal WorkingDaysLost(
        SicknessRecord record,
        DateOnly asOf,
        IReadOnlySet<DayOfWeek> workingDays,
        IReadOnlySet<DateOnly> holidays)
    {
        (DateOnly start, DateOnly end) = Span(record, asOf);

        decimal lost = 0m;

        for (DateOnly day = start; day <= end; day = day.AddDays(1))
        {
            if (IsWorked(day, workingDays, holidays))
            {
                lost += Weight(record, day, start, end, halfDaysAsHalf: true);
            }
        }

        return lost;
    }

    public static bool IsWorked(
        DateOnly day,
        IReadOnlySet<DayOfWeek> workingDays,
        IReadOnlySet<DateOnly> holidays) =>
        workingDays.Contains(day.DayOfWeek) && !holidays.Contains(day);

    // 0.5 where the absence covers only half of its first or last day.
    public static decimal Weight(
        SicknessRecord record,
        DateOnly day,
        DateOnly start,
        DateOnly end,
        bool halfDaysAsHalf)
    {
        if (!halfDaysAsHalf)
        {
            return 1m;
        }

        if (day == start && record.StartPortion != DayPortion.Full)
        {
            return 0.5m;
        }

        return day == end && day != start && record.EndPortion != DayPortion.Full ? 0.5m : 1m;
    }
}
