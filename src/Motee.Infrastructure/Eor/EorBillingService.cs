using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Eor;
using Motee.Application.Tenancy;
using Motee.Domain.Audit;
using Motee.Domain.Eor;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Eor;

// Invoices a provider has sent, and whether they match what we engaged.
//
// That comparison is the reason to hold invoices at all. The screen renders
// them as a read-only list checked against nothing, so a provider billing for
// nine workers when seven are engaged reads as a normal month.
internal sealed class EorBillingService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditTrail audit,
    TimeProvider timeProvider) : IEorBillingService
{
    private const string Module = "organization.eor";

    private readonly TenantCalendar _calendar = new(dbContext, currentTenant, timeProvider);

    public async Task<IReadOnlyList<EorInvoiceDto>> InvoicesAsync(
        Guid? providerId = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<EorInvoice> source = dbContext.EorInvoices.AsNoTracking();

        if (providerId is Guid wanted)
        {
            source = source.Where(invoice => invoice.ProviderId == wanted);
        }

        List<EorInvoice> invoices = await source
            .OrderByDescending(invoice => invoice.Period)
            .ToListAsync(cancellationToken);

        return await DescribeAsync(invoices, cancellationToken);
    }

    public async Task<EorResult<EorInvoiceDto>> RecordAsync(
        EorInvoiceRequest request,
        CancellationToken cancellationToken = default)
    {
        EorProvider? provider = await dbContext.EorProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == request.ProviderId, cancellationToken);

        if (provider is null)
        {
            return EorResult<EorInvoiceDto>.Refused(EorRefusal.ProviderNotFound);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        EorInvoice invoice = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = request.ProviderId,

            // Normalised to the first of the month. A period is a month, not a
            // day, and two invoices for "May" dated the 1st and the 31st would
            // both be stored and both counted.
            Period = FirstOf(request.Period),
            Reference = Trimmed(request.Reference),
            Currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant(),
            BilledWorkerCount = request.BilledWorkerCount,
            GrossPay = request.GrossPay,
            EmployerCosts = request.EmployerCosts,
            ManagementFee = request.ManagementFee,
            Total = request.Total,
            IssuedOn = request.IssuedOn,
            DueOn = request.DueOn,
            DocumentFileId = request.DocumentFileId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        EorRefusal refusal = Invoices.Check(invoice);

        if (refusal != EorRefusal.None)
        {
            return EorResult<EorInvoiceDto>.Refused(refusal);
        }

        dbContext.EorInvoices.Add(invoice);

        audit.Record(
            AuditAction.Create,
            Module,
            $"Recorded {provider.Name} invoice for {invoice.Period:MMMM yyyy}",
            entityId: invoice.Id,
            entityType: nameof(EorInvoice));

        await dbContext.SaveChangesAsync(cancellationToken);

        return EorResult<EorInvoiceDto>.Ok(
            (await DescribeAsync([invoice], cancellationToken))[0]);
    }

    public async Task<EorResult<EorInvoiceDto>> UpdateAsync(
        Guid id,
        EorInvoiceRequest request,
        CancellationToken cancellationToken = default)
    {
        EorInvoice? invoice = await dbContext.EorInvoices
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (invoice is null)
        {
            return EorResult<EorInvoiceDto>.Failed(EorOutcome.NotFound);
        }

        bool providerExists = await dbContext.EorProviders
            .AnyAsync(row => row.Id == request.ProviderId, cancellationToken);

        if (!providerExists)
        {
            return EorResult<EorInvoiceDto>.Refused(EorRefusal.ProviderNotFound);
        }

        invoice.ProviderId = request.ProviderId;
        invoice.Period = FirstOf(request.Period);
        invoice.Reference = Trimmed(request.Reference);
        invoice.Currency = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
        invoice.BilledWorkerCount = request.BilledWorkerCount;
        invoice.GrossPay = request.GrossPay;
        invoice.EmployerCosts = request.EmployerCosts;
        invoice.ManagementFee = request.ManagementFee;
        invoice.Total = request.Total;
        invoice.IssuedOn = request.IssuedOn;
        invoice.DueOn = request.DueOn;
        invoice.DocumentFileId = request.DocumentFileId;

        EorRefusal refusal = Invoices.Check(invoice);

        if (refusal != EorRefusal.None)
        {
            return EorResult<EorInvoiceDto>.Refused(refusal);
        }

        invoice.UpdatedAt = timeProvider.GetUtcNow();

        audit.Record(
            AuditAction.Update,
            Module,
            $"Updated EOR invoice for {invoice.Period:MMMM yyyy}",
            entityId: invoice.Id,
            entityType: nameof(EorInvoice));

        await dbContext.SaveChangesAsync(cancellationToken);

        return EorResult<EorInvoiceDto>.Ok(
            (await DescribeAsync([invoice], cancellationToken))[0]);
    }

    public async Task<EorResult<EorInvoiceDto>> SettleAsync(
        Guid id,
        SettleInvoiceRequest request,
        CancellationToken cancellationToken = default)
    {
        EorInvoice? invoice = await dbContext.EorInvoices
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (invoice is null)
        {
            return EorResult<EorInvoiceDto>.Failed(EorOutcome.NotFound);
        }

        // Null reopens it, for one marked paid by mistake. Paid, overdue and
        // pending all follow from this date, so there is no status to correct
        // separately.
        invoice.PaidOn = request.PaidOn;
        invoice.UpdatedAt = timeProvider.GetUtcNow();

        audit.Record(
            AuditAction.Update,
            Module,
            request.PaidOn is DateOnly paid
                ? $"EOR invoice for {invoice.Period:MMMM yyyy} marked paid on {paid:d MMMM yyyy}"
                : $"EOR invoice for {invoice.Period:MMMM yyyy} reopened as unpaid",
            entityId: invoice.Id,
            entityType: nameof(EorInvoice));

        await dbContext.SaveChangesAsync(cancellationToken);

        return EorResult<EorInvoiceDto>.Ok(
            (await DescribeAsync([invoice], cancellationToken))[0]);
    }

    public async Task<EorOutcome> RemoveAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        EorInvoice? invoice = await dbContext.EorInvoices
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (invoice is null)
        {
            return EorOutcome.NotFound;
        }

        dbContext.EorInvoices.Remove(invoice);

        audit.Record(
            AuditAction.Delete,
            Module,
            $"Removed EOR invoice for {invoice.Period:MMMM yyyy}",
            entityId: invoice.Id,
            entityType: nameof(EorInvoice));

        await dbContext.SaveChangesAsync(cancellationToken);

        return EorOutcome.Succeeded;
    }

    private async Task<List<EorInvoiceDto>> DescribeAsync(
        IReadOnlyList<EorInvoice> invoices,
        CancellationToken cancellationToken)
    {
        if (invoices.Count == 0)
        {
            return [];
        }

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        List<Guid> providerIds = [.. invoices.Select(invoice => invoice.ProviderId).Distinct()];

        Dictionary<Guid, string> providers = await dbContext.EorProviders
            .AsNoTracking()
            .Where(provider => providerIds.Contains(provider.Id))
            .ToDictionaryAsync(
                provider => provider.Id, provider => provider.Name, cancellationToken);

        // Every engagement with these providers, so the headcount comparison can
        // be made per invoice. One query rather than one per row.
        List<EorEngagement> engagements = await dbContext.EorEngagements
            .AsNoTracking()
            .Where(engagement => providerIds.Contains(engagement.ProviderId))
            .ToListAsync(cancellationToken);

        return
        [
            .. invoices.Select(invoice => new EorInvoiceDto
            {
                Id = invoice.Id,
                ProviderId = invoice.ProviderId,
                ProviderName = providers.GetValueOrDefault(invoice.ProviderId, "Unknown"),
                Period = invoice.Period,
                PeriodLabel = invoice.Period.ToString("MMMM yyyy"),
                Reference = invoice.Reference,
                Currency = invoice.Currency,
                GrossPay = invoice.GrossPay,
                EmployerCosts = invoice.EmployerCosts,
                ManagementFee = invoice.ManagementFee,
                Total = invoice.Total,
                Standing = Invoices.StandingOf(invoice, today),
                DaysOverdue = Invoices.DaysOverdue(invoice, today),
                IssuedOn = invoice.IssuedOn,
                DueOn = invoice.DueOn,
                PaidOn = invoice.PaidOn,
                DocumentFileId = invoice.DocumentFileId,
                BilledWorkerCount = invoice.BilledWorkerCount,
                EngagedWorkerCount = Invoices.EngagedDuring(
                    engagements, invoice.ProviderId, invoice.Period),
                TotalAddsUp = Invoices.TotalAddsUp(invoice),
                SumOfParts = Invoices.SumOfParts(invoice),
            }),
        ];
    }

    private static DateOnly FirstOf(DateOnly date) => new(date.Year, date.Month, 1);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
