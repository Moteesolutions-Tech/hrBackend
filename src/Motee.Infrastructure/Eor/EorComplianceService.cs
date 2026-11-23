using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Eor;
using Motee.Application.Tenancy;
using Motee.Domain.Audit;
using Motee.Domain.Eor;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Eor;

// The compliance register: whether each worker employed abroad has the
// paperwork that makes it lawful.
//
// The expiry is the part that matters. The screen records each requirement as a
// dropdown with no date and no document, so a work permit that lapsed last
// month still reads "Complete" — and on an EOR register that is the
// right-to-work record.
// Takes EorService concretely rather than through its interface, because what
// it needs is the shared describe helper and not the published contract. The
// alternative was duplicating how a worker row is built, and two places
// deriving the same compliance standing is how they come to disagree.
internal sealed class EorComplianceService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditTrail audit,
    EorService workers,
    TimeProvider timeProvider) : IEorComplianceService
{
    private const string Module = "organization.eor";

    private readonly TenantCalendar _calendar = new(dbContext, currentTenant, timeProvider);

    public async Task<IReadOnlyList<ComplianceRowDto>> RegisterAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        List<EorEngagement> engagements = await dbContext.EorEngagements
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (engagements.Count == 0)
        {
            return [];
        }

        List<EorWorkerDto> described =
            await workers.DescribeAsync(engagements, today, cancellationToken);

        return
        [
            // The ones with something outstanding first, and ended engagements
            // last: a worker who left in June is not an outstanding
            // right-to-work risk, but the row is still worth keeping.
            .. described
                .OrderBy(worker => worker.Stage == EngagementStage.Ended)
                .ThenByDescending(worker => worker.Blockers.Count)
                .ThenBy(worker => worker.Name, StringComparer.OrdinalIgnoreCase)
                .Select(worker => new ComplianceRowDto
                {
                    EngagementId = worker.Id,
                    EmployeeId = worker.EmployeeId,
                    Name = worker.Name,
                    Initials = worker.Initials,
                    CountryOfEngagement = worker.CountryOfEngagement,
                    ProviderName = worker.ProviderName,
                    Stage = worker.Stage,
                    Items = worker.Compliance,
                    Blockers = worker.Blockers,
                }),
        ];
    }

    public async Task<EorResult<EorWorkerDto>> RecordAsync(
        Guid engagementId,
        ComplianceKey key,
        ComplianceRequest request,
        CancellationToken cancellationToken = default)
    {
        EorEngagement? engagement = await dbContext.EorEngagements
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == engagementId, cancellationToken);

        if (engagement is null)
        {
            return EorResult<EorWorkerDto>.Failed(EorOutcome.NotFound);
        }

        EorComplianceItem? item = await dbContext.EorComplianceItems
            .FirstOrDefaultAsync(
                row => row.EngagementId == engagementId && row.Key == key, cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        if (item is null)
        {
            // An engagement created before this requirement existed has no row
            // for it. Added rather than refused: the alternative is a register
            // with a gap nobody can fill.
            item = new EorComplianceItem
            {
                Id = Guid.NewGuid(),
                TenantId = engagement.TenantId,
                EngagementId = engagementId,
                Key = key,
                CreatedAt = now,
                UpdatedAt = now,
            };

            dbContext.EorComplianceItems.Add(item);
        }

        ComplianceRequirement requirement = ComplianceCatalogue.Of(key);

        item.State = request.State;
        item.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        item.EvidenceFileId = request.EvidenceFileId;

        // Only where the requirement is one that lapses. A signed contract with
        // an expiry would read as expired a year later and block a worker who is
        // perfectly lawful, so the field is dropped rather than stored for
        // something that cannot run out.
        item.ExpiresOn = requirement.Expires ? request.ExpiresOn : null;

        // Stamped when somebody marks it complete without saying when they
        // checked. The date they did it is the honest answer, and an unverified
        // "complete" is the thing this register exists to avoid.
        item.VerifiedOn = request.VerifiedOn
            ?? (request.State == ComplianceState.Complete
                ? await _calendar.TodayAsync(cancellationToken)
                : null);

        item.UpdatedAt = now;

        audit.Record(
            AuditAction.Update,
            Module,
            $"{requirement.Label} marked {Describe(request.State)}"
            + (item.ExpiresOn is DateOnly expires ? $", expiring {expires:d MMMM yyyy}" : string.Empty),
            entityId: engagementId,
            entityType: nameof(EorEngagement));

        await dbContext.SaveChangesAsync(cancellationToken);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        List<EorWorkerDto> described =
            await workers.DescribeAsync([engagement], today, cancellationToken);

        return described.Count == 0
            ? EorResult<EorWorkerDto>.Failed(EorOutcome.NotFound)
            : EorResult<EorWorkerDto>.Ok(described[0]);
    }

    private static string Describe(ComplianceState state) => state switch
    {
        ComplianceState.Complete => "complete",
        ComplianceState.ActionRequired => "needing attention",
        ComplianceState.NotApplicable => "not applicable",
        _ => "outstanding",
    };
}
