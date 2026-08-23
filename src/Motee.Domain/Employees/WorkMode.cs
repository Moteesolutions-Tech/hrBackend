namespace Motee.Domain.Employees;

// Where the person works, as a fixed taxonomy rather than free text: the toolbar
// filters on it, so "Remote", "remote" and "Remotely" cannot be three different
// answers to the same question. Display copy stays in the frontend, as with
// EmploymentType.
public enum WorkMode
{
    Remote,
    Hybrid,
    Onsite,
}
