using Motee.Domain.Common;

namespace Motee.Domain.Health;

// Where an occupational health case has got to.
//
// Ordered, and the order matters: the stage is what the board groups by and
// what decides the next action. The first four are facts about the absence
// rather than decisions anybody records — see Stages.Of.
public enum OhStage
{
    AbsenceRecorded,
    Monitoring,
    ThresholdReached,
    HrAlerted,

    // From here on, each stage is something somebody did.
    Referred,
    AssessmentCompleted,
    RecommendationsReceived,
    HrReview,
    AdjustmentsImplemented,
    Returned,
    RtwCompleted,
    Closed,
}

// What the clinician advises about fitness for work.
//
// Fitness only. There is deliberately no diagnosis anywhere in this module:
// an occupational health clinician tells the employer whether somebody can
// work and what would help, never what is wrong with them. The frontend's type
// file carries that as a comment saying a diagnosis field "must never be
// added"; here it is structural — there is nowhere to put one.
public enum OhFitness
{
    Pending,
    Fit,
    FitWithAdjustments,
    TemporarilyUnfit,
    Unfit,
}

// An occupational health referral.
//
// A formal process distinct from ordinary sickness absence: HR asks a
// clinician whether somebody is fit for work and what adjustments would help.
// It hangs off the absence that triggered it rather than duplicating any of
// it — the absence, its dates and its clinical reason belong to the sickness
// module, and this record must not become a second copy of them.
public class OhReferral : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    // The absence that triggered monitoring. Optional, because a referral can
    // be raised without a long absence behind it — a phased return after
    // surgery, a manager's concern about somebody still at work.
    public Guid? SicknessRecordId { get; set; }

    public required string ReferenceNumber { get; set; }

    // Start of the continuous absence being monitored. The threshold is counted
    // from here.
    public DateOnly? AbsenceStartDate { get; set; }

    public DateOnly? ReferralDate { get; set; }

    public DateOnly? AssessmentDate { get; set; }

    // What somebody has recorded. Read through Stages.Of, which will not let a
    // case sit at Monitoring while the absence behind it has run for months.
    public OhStage Stage { get; set; } = OhStage.AbsenceRecorded;

    public OhFitness Fitness { get; set; } = OhFitness.Pending;

    // Whether reasonable-adjustment duties were considered. A yes/no, not a
    // determination: whether a condition is a disability in law is not
    // something this product decides.
    public bool ReasonableAdjustmentsConsidered { get; set; }

    public DateOnly? ExpectedReturnDate { get; set; }

    // Administrative only — "appointment moved to the 14th", "waiting on the
    // clinician's report". Never clinical.
    //
    // Withheld from the manager view and from the audit trail's field-level
    // diff, because free text is where a diagnosis would arrive however the
    // field is labelled. Keeping it out of both means a note somebody wrote
    // carelessly is still only readable by the people the module already
    // trusts with medical data.
    public string? CaseNotes { get; set; }

    // Who is driving the case. An employee rather than a user: this is an HR
    // responsibility that shows on a person's own work list.
    public Guid? CaseOwnerEmployeeId { get; set; }

    public DateOnly? ReviewDate { get; set; }

    public DateOnly? ClosedOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum OhRefusal
{
    None,
    EmployeeNotFound,
    StageNotReachable,
    AlreadyOpen,
    AbsenceStartRequired,
    ReferralDateRequired,
    AssessmentDateRequired,
    FitnessRequired,
    AssessmentBeforeReferral,
    ReturnBeforeAbsenceStart,
    ReviewDateInPast,
    DescriptionRequired,
    AdjustmentStageNotReachable,
    OwnerNotFound,
    NotesTooLong,
}

public static class OhStages
{
    // How long a continuous absence runs before it is worth asking a clinician.
    //
    // Four weeks, matching the frontend's OH_REFERRAL_THRESHOLD_DAYS. Short
    // enough to catch somebody before a return becomes hard, long enough that
    // ordinary illness does not generate referrals.
    public const int ReferralThresholdDays = 28;

    // The stage from which a case is somebody's decision rather than a
    // consequence of the calendar.
    public const OhStage FirstDecidedStage = OhStage.Referred;

    private static readonly OhStage[] Order = Enum.GetValues<OhStage>();

    public static int PositionOf(OhStage stage) => Array.IndexOf(Order, stage);

    // Where the case actually is.
    //
    // Before a referral, the stage is a fact about the absence: an absence that
    // passed 28 days is at ThresholdReached whatever the stored value says. The
    // screen stores the stage as a plain field, so a case can sit at
    // "Monitoring duration" with a ninety-day absence behind it — which is the
    // one thing this board exists to stop happening.
    //
    // Once referred, the stored stage governs: from there on each step is
    // something somebody did, and no date can tell us whether they did it.
    public static OhStage Of(OhReferral referral, DateOnly today)
    {
        if (PositionOf(referral.Stage) >= PositionOf(FirstDecidedStage))
        {
            return referral.Stage;
        }

        if (referral.AbsenceStartDate is not DateOnly start)
        {
            return referral.Stage;
        }

        int days = today.DayNumber - start.DayNumber;

        OhStage byCalendar = days >= ReferralThresholdDays
            ? OhStage.ThresholdReached
            : OhStage.Monitoring;

        // The later of the two. HrAlerted is past the threshold and is a fact
        // about a notification having gone out, so the calendar must not pull a
        // case back to ThresholdReached once somebody has been told.
        return PositionOf(referral.Stage) > PositionOf(byCalendar)
            ? referral.Stage
            : byCalendar;
    }

    public static bool IsOpen(OhReferral referral) => referral.Stage != OhStage.Closed;

    public static bool AwaitsClinician(OhStage stage) =>
        stage is OhStage.Referred or OhStage.AssessmentCompleted;

    // Forward along the sequence, one step or several.
    //
    // Several, because real casework skips: a clinician who sends
    // recommendations with the assessment report moves two stages at once, and
    // forcing the intermediate click would mean recording a step that did not
    // happen separately.
    //
    // Backwards is refused with one exception below — a case that has moved on
    // has moved on, and correcting a mistake is a note, not a rewind.
    public static bool CanMove(OhStage from, OhStage to)
    {
        if (from == to)
        {
            return true;
        }

        // Closing is possible from anywhere. People recover, leave, or withdraw
        // consent, and a case that cannot be closed from where it stands is one
        // that stays on the board for good.
        if (to == OhStage.Closed)
        {
            return true;
        }

        // Reopening lands at HR review rather than at the start: what reopens a
        // closed case is new information for HR to weigh, and sending it back to
        // Monitoring would discard everything the clinician said.
        if (from == OhStage.Closed)
        {
            return to == OhStage.HrReview;
        }

        return PositionOf(to) > PositionOf(from);
    }

    // The stages a case can legitimately move to from here, for the client's
    // stage picker. Built from the rule above rather than written out, so a
    // picker cannot offer something the service will refuse.
    public static IReadOnlyList<OhStage> NextFrom(OhStage from) =>
        [.. Order.Where(stage => stage != from && CanMove(from, stage))];
}

public static class OhRules
{
    public const int MaximumNotesLength = 4000;

    // What each stage needs before a case can claim to be at it.
    //
    // The screen has a free stage dropdown, so a case can read "Assessment
    // completed" with no assessment date and "Recommendations received" with no
    // recommendations — which makes the board's own columns unreliable.
    public static OhRefusal CheckMove(OhReferral referral, OhStage to, int adjustmentCount)
    {
        if (!OhStages.CanMove(referral.Stage, to))
        {
            return OhRefusal.StageNotReachable;
        }

        return to switch
        {
            OhStage.Referred when referral.ReferralDate is null =>
                OhRefusal.ReferralDateRequired,

            OhStage.AssessmentCompleted when referral.AssessmentDate is null =>
                OhRefusal.AssessmentDateRequired,

            // A clinician's advice is the fitness opinion. Reaching this stage
            // with it still Pending means the advice has not actually arrived.
            OhStage.RecommendationsReceived when referral.Fitness == OhFitness.Pending =>
                OhRefusal.FitnessRequired,

            // Nothing to have implemented. A case claiming implemented
            // adjustments with none recorded is the board reporting work that
            // does not exist.
            OhStage.AdjustmentsImplemented when adjustmentCount == 0 =>
                OhRefusal.DescriptionRequired,

            _ => OhRefusal.None,
        };
    }

    public static OhRefusal CheckDates(OhReferral referral)
    {
        if (referral.ReferralDate is DateOnly referred
            && referral.AssessmentDate is DateOnly assessed
            && assessed < referred)
        {
            return OhRefusal.AssessmentBeforeReferral;
        }

        if (referral.AbsenceStartDate is DateOnly start
            && referral.ExpectedReturnDate is DateOnly expected
            && expected < start)
        {
            return OhRefusal.ReturnBeforeAbsenceStart;
        }

        return (referral.CaseNotes?.Length ?? 0) > MaximumNotesLength
            ? OhRefusal.NotesTooLong
            : OhRefusal.None;
    }

    // How long the absence has run. The figure the threshold is judged on, and
    // the one the board sorts by.
    public static int? AbsenceDays(OhReferral referral, DateOnly today)
    {
        if (referral.AbsenceStartDate is not DateOnly start)
        {
            return null;
        }

        // An ended case stops counting on the day it closed. Without this a
        // closed case's absence grows for ever and the longest absences on the
        // board are all people who came back years ago.
        DateOnly until = referral.ClosedOn ?? today;

        return Math.Max(0, until.DayNumber - start.DayNumber);
    }

    public static bool ReviewIsDue(OhReferral referral, DateOnly today) =>
        OhStages.IsOpen(referral)
            && referral.ReviewDate is DateOnly due
            && due <= today;
}
