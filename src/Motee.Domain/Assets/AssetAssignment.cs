using Motee.Domain.Common;

namespace Motee.Domain.Assets;

// One spell of somebody holding an asset.
//
// Asset.AssignedToEmployeeId says who has the laptop now, which is the question a list
// screen asks. This says who has ever had it, which is the question an audit asks, and
// the two are not the same: overwriting the pointer on each handover answers the first
// perfectly and destroys the second.
//
// The pointer stays. It is the fast read, and keeping it means no screen has to find the
// open row to show a current holder. This table is what makes "who had this in March"
// answerable at all.
public class AssetAssignment : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid AssetId { get; set; }

    public Guid EmployeeId { get; set; }

    public DateOnly AssignedOn { get; set; }

    // Null while they still hold it. One open row per asset at most, which is what makes
    // a handover a return followed by an assignment rather than two live claims on the
    // same laptop.
    public DateOnly? ReturnedOn { get; set; }

    // Why it came back: returned at offboarding, swapped for a replacement, reported
    // lost. Free text, because the reasons are a company's own.
    public string? ReturnReason { get; set; }

    // Condition noted at handover and at return. The pair is what supports a deduction
    // or a write-off, and either on its own proves nothing.
    public string? ConditionOnAssign { get; set; }

    public string? ConditionOnReturn { get; set; }

    public Guid? AssignedByUserId { get; set; }

    public Guid? ReturnedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsOpen => ReturnedOn is null;
}
