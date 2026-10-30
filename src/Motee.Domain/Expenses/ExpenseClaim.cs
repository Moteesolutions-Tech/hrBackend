using Motee.Domain.Common;

namespace Motee.Domain.Expenses;

public enum ExpenseCategory
{
    Travel,
    Meals,
    Accommodation,
    Equipment,
    Software,
    Training,
    Other,
}

public enum ExpenseStatus
{
    // Being written, or sent back to be rewritten. Not with anybody.
    Draft,

    Submitted,
    Approved,
    Rejected,

    // Paid. The last step of the chain is the pay-out, so clearing it means the
    // money has gone rather than that somebody agreed it should.
    Reimbursed,
}

// Money somebody spent and wants back.
public class ExpenseClaim : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    // EXP-2026-00124. Issued when the claim is first submitted, never reissued.
    //
    // Null while it is a draft, because a reference is what somebody quotes in an
    // email and a draft nobody has seen has nothing to quote. Handing one out at
    // creation would burn sequence numbers on claims that are abandoned.
    public string? Reference { get; set; }

    public required string Title { get; set; }

    public ExpenseCategory Category { get; set; }

    // Stored as the claim was incurred, not converted. A conversion rate belongs to
    // the day it was applied, and rewriting the figure into tenant currency at
    // submission would leave nobody able to reconcile it against the receipt.
    //
    // decimal, never double: this is money, and a float cannot hold 0.10 exactly.
    public decimal Amount { get; set; }

    // ISO 4217 — "GBP", "NGN". Three characters, stored as given.
    public required string Currency { get; set; }

    public required string Merchant { get; set; }

    // When the money was spent, which is not when the claim was filed. Expense
    // policies are written in terms of the former.
    public DateOnly IncurredOn { get; set; }

    public string? Notes { get; set; }

    public ExpenseStatus Status { get; set; } = ExpenseStatus.Draft;

    // The chain reviewing it. Null until submitted, and the single source of truth
    // for where the review stands — this record keeps no copy of that, because two
    // places to look is how they end up disagreeing.
    public Guid? ApprovalInstanceId { get; set; }

    // Sent back for correction. A flag rather than a sixth status, because a
    // returned claim behaves exactly like a draft everywhere else — it reopens,
    // edits, resubmits and discards the same way. Making it a status would mean
    // writing "draft or returned" at every one of those decisions.
    public bool Returned { get; set; }

    public string? ReturnedReason { get; set; }

    // Receipts. Ids into stored_files rather than bytes: the files live where every
    // other upload lives, and a claim carrying its own copy would be a second
    // storage path to secure and expire.
    public List<Guid> ReceiptFileIds { get; set; } = [];

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? ReimbursedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ExpenseAction
{
    Submit,
    Withdraw,
    Return,
    Reimburse,
}

// What a claim will accept next.
//
// Written down rather than grown as a chain of ifs, like the clock's and the
// timesheet's. The rule worth stating: Draft is both where a claim starts and where
// a returned one goes back to, so the same transitions serve both.
public static class ExpenseLifecycle
{
    private static readonly Dictionary<ExpenseStatus, ExpenseAction[]> Allowed = new()
    {
        [ExpenseStatus.Draft] = [ExpenseAction.Submit],

        // Withdrawing is the filer's; returning is the reviewer's. Approving and
        // rejecting are not here because they belong to the approval engine — the
        // claim follows the chain rather than deciding for itself.
        [ExpenseStatus.Submitted] = [ExpenseAction.Withdraw, ExpenseAction.Return],

        // Agreed, not yet paid. Finance moves it the last step.
        [ExpenseStatus.Approved] = [ExpenseAction.Reimburse],

        // Turned down. Resubmitting a rejected claim would let somebody keep asking
        // until an approver gave in; a genuinely different claim is a new one.
        [ExpenseStatus.Rejected] = [],

        // Paid. Nothing comes after the money.
        [ExpenseStatus.Reimbursed] = [],
    };

    public static bool Can(ExpenseStatus from, ExpenseAction action) =>
        Allowed[from].Contains(action);

    public static string Refusal(ExpenseStatus from, ExpenseAction action) =>
        (from, action) switch
        {
            (ExpenseStatus.Submitted, ExpenseAction.Submit) =>
                "This claim has already been submitted and is waiting to be reviewed.",
            (ExpenseStatus.Approved, ExpenseAction.Submit) =>
                "This claim has already been approved.",
            (ExpenseStatus.Rejected, ExpenseAction.Submit) =>
                "This claim was turned down. File a new one rather than resubmitting it.",
            (ExpenseStatus.Reimbursed, _) => "This claim has already been paid.",
            (ExpenseStatus.Draft, ExpenseAction.Withdraw) =>
                "This claim has not been submitted, so there is nothing to withdraw.",
            (ExpenseStatus.Draft, ExpenseAction.Return) =>
                "This claim has not been submitted yet.",
            (_, ExpenseAction.Reimburse) =>
                "Only an approved claim can be marked as paid.",
            _ => "That is not a move this claim can make.",
        };
}

public static class ExpenseReferences
{
    public const string Prefix = "EXP";

    // EXP-2026-00124. The year is the submission year, and sequences run per year
    // so the number stays short and sortable rather than climbing for ever.
    public static string Format(int year, int sequence) =>
        $"{Prefix}-{year}-{sequence.ToString().PadLeft(5, '0')}";

    // Pulls the sequence back out of a reference, for working out the next one.
    // Anything that does not match the shape is ignored rather than throwing: a
    // hand-edited reference should not stop the next claim being filed.
    public static bool TrySequenceOf(string? reference, int year, out int sequence)
    {
        sequence = 0;

        string prefix = $"{Prefix}-{year}-";

        return reference is not null
            && reference.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(reference[prefix.Length..], out sequence);
    }
}
