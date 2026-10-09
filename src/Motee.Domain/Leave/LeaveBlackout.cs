using Motee.Domain.Common;

namespace Motee.Domain.Leave;

// A stretch of the year the company will not approve planned leave over.
//
// Retail over Christmas, finance at year-end close, a factory through its busiest run.
// Distinct from a public holiday, which is a day nobody works: a blackout is a day
// everybody works, and the whole point is that it cannot be booked off.
public class LeaveBlackout : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // What it is called internally: "Christmas trading period".
    public required string Name { get; set; }

    // Shown to whoever runs into it. A refusal that does not say why reads as a bug, and
    // the person then asks HR — which is the cost the message exists to avoid.
    public string? Reason { get; set; }

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    // Which kinds of leave it stops. Never empty, and that is enforced on write.
    //
    // There is deliberately no "applies to everything" option. Sick leave is reported,
    // not requested — somebody is ill whether or not it is a busy week — and a blackout
    // that blocked it would tell a sick person they may not be ill until January. An
    // implicit "all" is exactly how that ships by accident, so the types are always named.
    public IReadOnlyList<Guid> LeaveTypeIds { get; set; } = [];

    // Which departments it covers. Empty means the whole company, which is safe in a way
    // the leave-type default is not: a blackout that covers too many departments is
    // visible and complained about immediately, where one silently blocking sick leave is
    // not.
    public IReadOnlyList<Guid> DepartmentIds { get; set; } = [];

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // Whether this blackout stands in the way of one request.
    //
    // Any overlap at all, not containment: a fortnight booked across the blackout's first
    // day is the booking it exists to prevent, and most of the dates falling outside does
    // not make it acceptable.
    public bool Blocks(DateOnly start, DateOnly end, Guid leaveTypeId, Guid? departmentId) =>
        IsActive
        && start <= EndDate
        && end >= StartDate
        && LeaveTypeIds.Contains(leaveTypeId)
        && (DepartmentIds.Count == 0
            || (departmentId is Guid department && DepartmentIds.Contains(department)));
}
