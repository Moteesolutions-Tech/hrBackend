namespace Motee.Domain.Employees;

// Mirrors the frontend's EmployeeStatus union, which drives the Employees table
// tabs and which row actions are enabled.
public enum EmployeeStatus
{
    Pending,
    Onboarded,
    Probation,
    Active,
    OnLeave,
    Offboarding,
    Inactive,

    // Soft-deleted; recoverable from the Deleted tab.
    Deleted,
}
