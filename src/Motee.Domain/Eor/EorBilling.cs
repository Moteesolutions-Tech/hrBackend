using Motee.Domain.Common;

namespace Motee.Domain.Eor;

public enum InvoiceStanding
{
    Pending,
    Paid,
    Overdue,
}

// An invoice a provider has sent.
//
// The amounts are recorded rather than derived, and that is the one place in
// this module where stored figures are right: the provider's document is the
// record, and recomputing it from our own engagement data would produce a
// number that disagrees with what we have been asked to pay. What is derived is
// the comparison — whether the invoice adds up, and whether the headcount it
// bills for matches the workers we have engaged.
//
// That comparison is the reason to hold invoices at all. The screen shows them
// as a read-only list with no check against anything, so a provider billing for
// nine workers when seven are engaged reads as a normal month.
public class EorInvoice : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ProviderId { get; set; }

    // The month billed for, as the first day of it. A date rather than the
    // screen's "January 2026" label: a label cannot be compared, sorted or
    // matched against the engagements live that month.
    public DateOnly Period { get; set; }

    public string? Reference { get; set; }

    // How many workers the provider says they employed for us. Kept, because it
    // is what the invoice claims — and compared with what we have, which is
    // where it earns its place.
    public int BilledWorkerCount { get; set; }

    // As invoiced, in the invoice's own currency.
    public required string Currency { get; set; }

    public decimal GrossPay { get; set; }

    public decimal EmployerCosts { get; set; }

    public decimal ManagementFee { get; set; }

    // The provider's total. Not assumed to be the sum of the three above —
    // checked against it, because a provider's rounding, a credit note or a
    // one-off charge all show up here and silently replacing their figure with
    // ours would hide a dispute rather than raise it.
    public decimal Total { get; set; }

    public DateOnly IssuedOn { get; set; }

    public DateOnly DueOn { get; set; }

    public DateOnly? PaidOn { get; set; }

    public Guid? DocumentFileId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public static class Invoices
{
    // Rounding, in the invoice's currency. Providers total in their own
    // systems and a few minor units either way is arithmetic, not a dispute.
    public const decimal Tolerance = 0.05m;

    // Paid, overdue or pending — from the dates, not a dropdown.
    //
    // The screen stores a status, so an invoice sits at "Pending" after its due
    // date until somebody opens the row and changes it. An overdue invoice that
    // reads as pending is one nobody chases.
    public static InvoiceStanding StandingOf(EorInvoice invoice, DateOnly today)
    {
        if (invoice.PaidOn is not null)
        {
            return InvoiceStanding.Paid;
        }

        return invoice.DueOn < today ? InvoiceStanding.Overdue : InvoiceStanding.Pending;
    }

    public static decimal SumOfParts(EorInvoice invoice) =>
        invoice.GrossPay + invoice.EmployerCosts + invoice.ManagementFee;

    public static bool TotalAddsUp(EorInvoice invoice) =>
        Math.Abs(SumOfParts(invoice) - invoice.Total) <= Tolerance;

    public static int DaysOverdue(EorInvoice invoice, DateOnly today) =>
        StandingOf(invoice, today) == InvoiceStanding.Overdue
            ? today.DayNumber - invoice.DueOn.DayNumber
            : 0;

    // The engagements this invoice should have covered: live with that provider
    // at any point in the month billed.
    //
    // Any point, not the whole month, because somebody who started on the 20th
    // is on that month's invoice for the part-month — leaving them out would
    // make every joining month look over-billed.
    public static int EngagedDuring(
        IReadOnlyList<EorEngagement> engagements,
        Guid providerId,
        DateOnly period)
    {
        DateOnly from = new(period.Year, period.Month, 1);
        DateOnly to = from.AddMonths(1).AddDays(-1);

        return engagements.Count(engagement =>
            engagement.ProviderId == providerId
                && engagement.StartDate <= to
                && (engagement.EndDate is not DateOnly end || end >= from));
    }

    public static EorRefusal Check(EorInvoice invoice)
    {
        if (invoice.Period == default)
        {
            return EorRefusal.PeriodRequired;
        }

        if (string.IsNullOrWhiteSpace(invoice.Currency))
        {
            return EorRefusal.CurrencyUnknown;
        }

        // Negative, not zero: a credit note is a real thing and a zero-value
        // month happens when a provider has nobody engaged. Neither is an
        // error, so neither is refused.
        if (invoice.GrossPay < 0m
            || invoice.EmployerCosts < 0m
            || invoice.ManagementFee < 0m
            || invoice.Total < 0m)
        {
            return EorRefusal.AmountNegative;
        }

        return invoice.BilledWorkerCount < 0 ? EorRefusal.AmountNegative : EorRefusal.None;
    }
}
