using Motee.Domain.Common;

namespace Motee.Domain.Contracts;

public enum ContractType
{
    Employment,
    Nda,
    Contractor,
    Internship,
    Consultancy,

    // A change to an existing contract rather than a replacement. Its own type
    // because an amendment read on its own is misleading — it has to be read
    // beside what it amends.
    Amendment,
}

// Where a contract stands, as somebody decided it.
//
// ExpiringSoon and Expired are deliberately absent: both are facts about the end
// date rather than decisions, so they are derived on read. Storing them would need
// a job to flip every contract at midnight, and a contract renewed on its last day
// would keep saying it had expired.
public enum ContractStatus
{
    Draft,

    // Sent out, waiting on signatures.
    PendingSignature,

    Active,

    // Ended early. Distinct from expired, which is a contract reaching its own end
    // date — one is a decision and the other is a calendar, and conflating them
    // loses the only part anybody would be asked about later.
    Terminated,
}

// Who has signed, derived from the signatures rather than stored.
public enum SignatureStatus
{
    Unsigned,

    // One side only. In practice the employee, since the company usually signs on
    // issue — but either order is allowed, because a company that counter-signs
    // last is not doing anything wrong.
    PartiallySigned,

    FullySigned,
}

public class Contract : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public ContractType ContractType { get; set; }

    public ContractStatus Status { get; set; } = ContractStatus.Draft;

    public DateOnly StartDate { get; set; }

    // Null for an open-ended contract, which is most permanent employment. Not a
    // far-future date standing in for "never": a date means something expires, and
    // every report would have to know which dates were real.
    public DateOnly? EndDate { get; set; }

    public bool AutoRenew { get; set; }

    // How much warning either side owes the other. Also what decides when a
    // contract starts reading as expiring soon — a month's notice on a contract
    // with a week left is already late.
    public int NoticePeriodDays { get; set; }

    // decimal, never double. Money, and the same rule as an expense claim.
    public decimal? Salary { get; set; }

    public required string Currency { get; set; }

    public bool IsArchived { get; set; }

    // Filed into the documents module. A contract and its signed PDF are the same
    // thing to a person and different things to the system, and this is the link
    // between them.
    public Guid? DocumentId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// One party's signature.
//
// A row per party rather than two columns, because an NDA may be countersigned by
// somebody other than the employee's manager and a consultancy agreement may need
// three names. Two columns would be the common case hard-coded.
public class ContractSignature : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ContractId { get; set; }

    // The employee when they sign their own; the signing administrator otherwise.
    public Guid? EmployeeId { get; set; }

    public Guid? UserId { get; set; }

    // "Employee", "HR Director". What they signed as, which is not always what
    // their job title says today — and the contract has to keep saying what it
    // said when it was signed.
    public required string SignedAs { get; set; }

    // The drawn signature, in stored_files with every other upload. An id rather
    // than a data URL: a base64 image in a row is a column nobody can index, back
    // up cheaply, or serve with an expiring link.
    public Guid? SignatureFileId { get; set; }

    public DateTimeOffset SignedAt { get; set; }
}

public class ContractNote : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ContractId { get; set; }

    public required string Content { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum ContractAction
{
    SendForSignature,
    Activate,
    Terminate,
}

// What a contract will accept next.
public static class ContractLifecycle
{
    private static readonly Dictionary<ContractStatus, ContractAction[]> Allowed = new()
    {
        [ContractStatus.Draft] = [ContractAction.SendForSignature],

        // Activating straight from pending is allowed, because a contract both
        // parties have signed is in force whether or not anybody pressed a button.
        [ContractStatus.PendingSignature] = [ContractAction.Activate, ContractAction.Terminate],

        [ContractStatus.Active] = [ContractAction.Terminate],

        // Ended. Reissuing is a new contract — an amendment or a replacement — and
        // reopening this one would leave the termination describing something that
        // no longer happened.
        [ContractStatus.Terminated] = [],
    };

    public static bool Can(ContractStatus from, ContractAction action) =>
        Allowed[from].Contains(action);

    public static string Refusal(ContractStatus from, ContractAction action) =>
        (from, action) switch
        {
            (ContractStatus.PendingSignature, ContractAction.SendForSignature) =>
                "This contract has already been sent out for signature.",
            (ContractStatus.Active, ContractAction.SendForSignature) =>
                "This contract is already in force.",
            (ContractStatus.Terminated, _) =>
                "This contract has been terminated. Issue a new one rather than reopening it.",
            (ContractStatus.Draft, ContractAction.Activate) =>
                "Send it for signature before putting it in force.",
            (ContractStatus.Draft, ContractAction.Terminate) =>
                "This contract is a draft. Delete it rather than terminating it.",
            (ContractStatus.Active, ContractAction.Activate) =>
                "This contract is already in force.",
            _ => "That is not a move this contract can make.",
        };
}

public static class Contracts
{
    // The window before an end date in which a contract counts as expiring soon.
    //
    // Taken from the notice period rather than a fixed thirty days, because that is
    // what the number is for: a contract with three months' notice needs flagging
    // three months out, and flagging it at thirty days would be telling somebody
    // after the moment they needed to act.
    public const int MinimumNoticeWindowDays = 30;

    public static bool IsExpired(Contract contract, DateOnly today) =>
        contract.Status != ContractStatus.Terminated
            && contract.EndDate is DateOnly end
            && end < today;

    public static bool IsExpiringSoon(Contract contract, DateOnly today)
    {
        if (contract.Status is ContractStatus.Terminated or ContractStatus.Draft
            || contract.EndDate is not DateOnly end
            || end < today)
        {
            return false;
        }

        // Auto-renewing contracts are not expiring; they are continuing. Flagging
        // them would fill the list with rows nobody has to act on, and the ones
        // that matter would be lost among them.
        if (contract.AutoRenew)
        {
            return false;
        }

        int window = Math.Max(contract.NoticePeriodDays, MinimumNoticeWindowDays);

        return end <= today.AddDays(window);
    }

    // Unsigned, one side, or both. Derived from the rows, so it cannot disagree
    // with the signatures underneath it.
    //
    // Two is "fully signed" because that is what a contract needs — the person and
    // the company. A third signature is extra assurance rather than a new bar.
    public static SignatureStatus SignatureStatusOf(int signatures) => signatures switch
    {
        0 => SignatureStatus.Unsigned,
        1 => SignatureStatus.PartiallySigned,
        _ => SignatureStatus.FullySigned,
    };
}
