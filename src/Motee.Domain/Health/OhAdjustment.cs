using Motee.Domain.Common;

namespace Motee.Domain.Health;

public enum AdjustmentStage
{
    Proposed,
    Agreed,
    Implemented,
    Reviewed,
    Closed,
}

// A workplace adjustment the clinician recommended.
//
// Its own record with its own lifecycle rather than a line of text on the
// referral, because an adjustment is a commitment somebody has to deliver: a
// phased return has an owner, a start date and a review date, and "agreed" is a
// different thing from "in place".
public class OhAdjustment : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ReferralId { get; set; }

    // The absence this adjustment responds to, where it is not the one the
    // referral itself hangs off — a recurring condition can produce several.
    public Guid? SicknessRecordId { get; set; }

    // "Phased return over four weeks", "desk moved to ground floor".
    //
    // The one clinical-adjacent text in the module, and deliberately the
    // recommendation rather than the reason for it: what to do, never why.
    public required string Description { get; set; }

    public DateOnly RecommendedOn { get; set; }

    // Who agreed it on the company's side. An employee, because this is a
    // commitment a named person made.
    public Guid? AgreedByEmployeeId { get; set; }

    public Guid? ImplementationOwnerEmployeeId { get; set; }

    public DateOnly? EffectiveDate { get; set; }

    public DateOnly? ReviewDate { get; set; }

    public AdjustmentStage Stage { get; set; } = AdjustmentStage.Proposed;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public static class Adjustments
{
    private static readonly AdjustmentStage[] Order = Enum.GetValues<AdjustmentStage>();

    public static int PositionOf(AdjustmentStage stage) => Array.IndexOf(Order, stage);

    // Forward, with closing possible from anywhere — an adjustment can be
    // withdrawn before it is ever put in place, and one that cannot be closed
    // from Proposed sits on the list indefinitely.
    public static bool CanMove(AdjustmentStage from, AdjustmentStage to) =>
        from == to || to == AdjustmentStage.Closed || PositionOf(to) > PositionOf(from);

    public static IReadOnlyList<AdjustmentStage> NextFrom(AdjustmentStage from) =>
        [.. Order.Where(stage => stage != from && CanMove(from, stage))];

    // In place and not withdrawn. What "the adjustments on this case" means
    // when somebody asks whether anything is actually helping.
    public static bool IsInPlace(OhAdjustment adjustment) =>
        adjustment.Stage is AdjustmentStage.Implemented or AdjustmentStage.Reviewed;

    public static OhRefusal Check(OhAdjustment adjustment, AdjustmentStage to)
    {
        if (string.IsNullOrWhiteSpace(adjustment.Description))
        {
            return OhRefusal.DescriptionRequired;
        }

        if (!CanMove(adjustment.Stage, to))
        {
            return OhRefusal.AdjustmentStageNotReachable;
        }

        return to switch
        {
            // An adjustment in place without a date it started is one nobody
            // can review or evidence.
            AdjustmentStage.Implemented when adjustment.EffectiveDate is null =>
                OhRefusal.ReferralDateRequired,

            // Somebody has to own it. An agreed adjustment with no owner is the
            // commitment that quietly never happens.
            AdjustmentStage.Agreed when adjustment.ImplementationOwnerEmployeeId is null =>
                OhRefusal.OwnerNotFound,

            _ => OhRefusal.None,
        };
    }

    public static bool ReviewIsDue(OhAdjustment adjustment, DateOnly today) =>
        adjustment.Stage != AdjustmentStage.Closed
            && adjustment.ReviewDate is DateOnly due
            && due <= today;
}
