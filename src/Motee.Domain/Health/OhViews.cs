namespace Motee.Domain.Health;

// What needs to happen next on a case, and who owns it.
//
// Derived from the stage, not stored. The screen keeps nextAction as a field on
// the referral beside the stage and nothing ties them together, so a case moved
// on from Referred keeps showing "Chase the OH appointment" — the action
// centre's whole purpose undone by the one field it reads.
public sealed record OhNextAction
{
    public required string Label { get; init; }

    // Whose move it is, in words rather than a person: at some stages it is the
    // clinician, who is not in this system at all. Naming the case owner for
    // those would put work on an HR list that HR cannot do.
    public required string Owner { get; init; }

    public DateOnly? Due { get; init; }
}

public static class OhNextActions
{
    // How long each waiting stage is allowed to sit before it is chased.
    private const int ChaseAfterDays = 7;

    private const int ReportAfterDays = 14;

    public static OhNextAction? Of(OhReferral referral, DateOnly today)
    {
        OhStage stage = OhStages.Of(referral, today);

        return stage switch
        {
            OhStage.AbsenceRecorded or OhStage.Monitoring => new OhNextAction
            {
                Label = "Watch the absence against the referral threshold",
                Owner = "Line manager",
                Due = referral.AbsenceStartDate?.AddDays(OhStages.ReferralThresholdDays),
            },

            OhStage.ThresholdReached => new OhNextAction
            {
                Label = $"Absence has passed {OhStages.ReferralThresholdDays} days — alert HR",
                Owner = "Line manager",
                Due = today,
            },

            OhStage.HrAlerted => new OhNextAction
            {
                Label = "Decide whether to refer to occupational health",
                Owner = "HR",
                Due = today,
            },

            OhStage.Referred => new OhNextAction
            {
                Label = "Chase the assessment appointment",
                Owner = "Occupational health provider",
                Due = referral.ReferralDate?.AddDays(ChaseAfterDays),
            },

            OhStage.AssessmentCompleted => new OhNextAction
            {
                Label = "Chase the clinician's report",
                Owner = "Occupational health provider",
                Due = referral.AssessmentDate?.AddDays(ReportAfterDays),
            },

            OhStage.RecommendationsReceived => new OhNextAction
            {
                Label = "Review the recommendations and agree adjustments",
                Owner = "HR",
                Due = today,
            },

            OhStage.HrReview => new OhNextAction
            {
                Label = "Agree the adjustments with the manager and the employee",
                Owner = "HR",
                Due = today,
            },

            OhStage.AdjustmentsImplemented => new OhNextAction
            {
                Label = "Confirm the return and book a return-to-work interview",
                Owner = "Line manager",
                Due = referral.ExpectedReturnDate,
            },

            OhStage.Returned => new OhNextAction
            {
                Label = "Hold the return-to-work interview",
                Owner = "Line manager",
                Due = referral.ExpectedReturnDate?.AddDays(ChaseAfterDays),
            },

            OhStage.RtwCompleted => new OhNextAction
            {
                Label = "Review the adjustments, then close the case",
                Owner = "HR",
                Due = referral.ReviewDate,
            },

            // Closed. Nothing to do, and an action on a closed case is how a
            // board fills up with work nobody can complete.
            _ => null,
        };
    }
}

// What a line manager may see.
//
// A shape with nowhere to put the confidential parts, rather than a full record
// with fields stripped on the way out. The same trick the learning module uses
// for quiz questions: somebody forgetting to strip a field is the likeliest way
// this leaks, so there is no field to forget.
//
// Carries fitness and the adjustments, because a manager cannot manage somebody
// back to work without knowing what they can do and what was agreed. Carries no
// case notes, no absence reason, no referral correspondence.
public sealed record OhManagerView
{
    public required Guid Id { get; init; }

    public required Guid EmployeeId { get; init; }

    public required string EmployeeName { get; init; }

    public string? Initials { get; init; }

    public string? Department { get; init; }

    public required OhFitness Fitness { get; init; }

    // The recommendations themselves — what to do, never why.
    public required IReadOnlyList<string> Adjustments { get; init; }

    public DateOnly? ExpectedReturnDate { get; init; }

    public DateOnly? ReviewDate { get; init; }

    // Said plainly rather than left for the client to infer from absent fields,
    // so a screen rendering this knows it is looking at the narrow view.
    public bool IsManagerView => true;
}

// Which slice of the board somebody is looking at.
//
// The tabs read more than the stage: Adjustments cares whether any exist,
// Action Required cares whether anything is outstanding. Kept here so the
// client and the server agree about what each tab means — two definitions of
// "action required" is two different counts on the same screen.
public enum OhBoardTab
{
    All,
    ActionRequired,
    AwaitingProvider,
    Adjustments,
    FollowUp,
    Closed,
}

public static class OhBoard
{
    public static bool Matches(
        OhReferral referral,
        int adjustmentCount,
        OhBoardTab tab,
        DateOnly today)
    {
        OhStage stage = OhStages.Of(referral, today);

        return tab switch
        {
            OhBoardTab.All => true,

            OhBoardTab.ActionRequired =>
                stage != OhStage.Closed && OhNextActions.Of(referral, today) is not null,

            OhBoardTab.AwaitingProvider => OhStages.AwaitsClinician(stage),

            OhBoardTab.Adjustments => adjustmentCount > 0 && stage != OhStage.Closed,

            OhBoardTab.FollowUp => stage is OhStage.AdjustmentsImplemented
                or OhStage.Returned
                or OhStage.RtwCompleted,

            _ => stage == OhStage.Closed,
        };
    }
}

// The figures on the board's header.
public sealed record OhTally
{
    public required int Open { get; init; }

    public required int AwaitingProvider { get; init; }

    public required int WithAdjustments { get; init; }

    public required int ReviewsDue { get; init; }

    // Open cases whose absence has passed the threshold and that nobody has
    // referred. The number this whole screen exists to keep at zero.
    public required int PastThresholdUnreferred { get; init; }

    public required int Closed { get; init; }
}
