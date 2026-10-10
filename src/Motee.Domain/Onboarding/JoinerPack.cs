using Motee.Domain.Common;

namespace Motee.Domain.Onboarding;

// One uploaded document, keyed by what it evidences.
//
// A row per slot rather than a list on the record, because each one is a file with its
// own lifecycle: replaced when rejected, expiring when a visa does, and looked at
// individually by whoever runs the right-to-work check.
public class JoinerDocument : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid OnboardingRecordId { get; set; }

    public required JoinerDocumentKind Kind { get; set; }

    public Guid FileId { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}

// Somebody who vouches for a new hire. Nigeria asks for two; UK tenants never see this.
//
// Not an employee and not a user — a guarantor is usually external, often a relative, and
// modelling them as a person in the system would put them in the directory.
public class Guarantor : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid OnboardingRecordId { get; set; }

    // 1 or 2. Ordered because their ID documents are filed against the same numbers, and
    // a guarantor whose ID cannot be matched to them is not evidence of anything.
    public int Position { get; set; }

    public required string Name { get; set; }

    public required string Relationship { get; set; }

    public string? Occupation { get; set; }

    public string? Address { get; set; }

    public string? Phone { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// A UK joiner's declared tax position, and the code derived from it.
//
// One row per onboarding record, and only for UK tenants — a Nigerian joiner declares a
// TIN and a state tax office instead, which is a different model rather than this one
// with fields left blank.
public class StarterTaxRecord : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid OnboardingRecordId { get; set; }

    public Guid EmployeeId { get; set; }

    public StarterTaxSource Source { get; set; } = StarterTaxSource.None;

    // The date the derivation is anchored to: which tax year the joiner started in
    // decides whether a P45 is current or stale.
    public DateOnly EmploymentStartDate { get; set; }

    // Set only for the branch that applies, enforced on write. "Both" is not a state a
    // joiner can be in.
    public P45Details? P45 { get; set; }

    public StarterChecklistDetails? StarterChecklist { get; set; }

    // Stored rather than computed on read. Payroll has to be able to say what code it
    // used and why at the time, and re-deriving later against a changed allowance figure
    // would quietly rewrite history.
    public DerivedTax? Derived { get; set; }

    // HMRC's retention period: the current tax year plus three. Stored so a deletion job
    // can find expired records without re-deriving the rule for every row.
    public DateOnly RetainUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
