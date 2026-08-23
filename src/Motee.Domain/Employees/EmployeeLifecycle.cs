namespace Motee.Domain.Employees;

// Which status changes are legitimate. Without this any status reaches any other,
// so a leaver can be flipped back to Active from a dropdown with nothing recording
// that it happened.
public static class EmployeeLifecycle
{
    private static readonly Dictionary<EmployeeStatus, EmployeeStatus[]> Allowed = new()
    {
        // Pending is the earliest state, so nothing from here is backwards. Importing
        // 200 existing staff means recording people who are already Active — they
        // should not have to be walked through onboarding they finished years ago.
        [EmployeeStatus.Pending] =
            [EmployeeStatus.Onboarded, EmployeeStatus.Probation, EmployeeStatus.Active,
             EmployeeStatus.Offboarding, EmployeeStatus.Inactive],
        [EmployeeStatus.Onboarded] = [EmployeeStatus.Probation, EmployeeStatus.Active, EmployeeStatus.Offboarding],
        [EmployeeStatus.Probation] = [EmployeeStatus.Active, EmployeeStatus.Offboarding],
        [EmployeeStatus.Active] = [EmployeeStatus.OnLeave, EmployeeStatus.Offboarding],
        [EmployeeStatus.OnLeave] = [EmployeeStatus.Active, EmployeeStatus.Offboarding],
        [EmployeeStatus.Offboarding] = [EmployeeStatus.Inactive],

        // A leaver stays a leaver. Rehiring is a new record or an explicit
        // reinstatement, not a status change.
        [EmployeeStatus.Inactive] = [],

        // The Deleted tab is a recycle bin, so restoring is expected.
        [EmployeeStatus.Deleted] =
            [EmployeeStatus.Pending, EmployeeStatus.Onboarded, EmployeeStatus.Probation,
             EmployeeStatus.Active, EmployeeStatus.OnLeave, EmployeeStatus.Offboarding,
             EmployeeStatus.Inactive],
    };

    public static bool CanMove(EmployeeStatus from, EmployeeStatus to)
    {
        // Saving a form without touching the status must not be rejected.
        if (from == to)
        {
            return true;
        }

        // Anything can be removed from the list; nothing is lost by it.
        if (to == EmployeeStatus.Deleted)
        {
            return true;
        }

        return Allowed.TryGetValue(from, out EmployeeStatus[]? next) && next.Contains(to);
    }

    public static IReadOnlyList<EmployeeStatus> NextFrom(EmployeeStatus from) =>
        from == EmployeeStatus.Deleted
            ? Allowed[from]
            : [.. Allowed.GetValueOrDefault(from, []), EmployeeStatus.Deleted];
}
