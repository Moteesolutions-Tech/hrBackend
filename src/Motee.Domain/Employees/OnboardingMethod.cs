namespace Motee.Domain.Employees;

// How the record entered the system. Shown on the employee profile header so HR can
// see a record's provenance.
public enum OnboardingMethod
{
    Manual,
    Invite,
    Bulk,
}
