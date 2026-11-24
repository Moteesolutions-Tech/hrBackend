using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Common;
using Motee.Application.Company;
using Motee.Application.Countries;
using Motee.Application.Tenancy;
using Motee.Domain.Audit;
using Motee.Domain.Common;
using Motee.Domain.Company;
using Motee.Domain.Countries;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Company;

// Registration and tax verification.
//
// The two halves are performed by different people, and that is the design
// rather than an implementation detail: the company drafts and submits, Motee
// staff review. The screen gives the company a status dropdown containing
// Verified, so a tenant can mark their own registration verified without
// anybody looking at anything.
internal sealed class CompanyVerificationService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    ICountryProfileProvider countries,
    IRequestContext requestContext,
    IAuditTrail audit,
    TimeProvider timeProvider) : ICompanyVerificationService
{
    private const string Module = "organization.company";

    private readonly TenantCalendar _calendar = new(dbContext, currentTenant, timeProvider);

    public async Task<IReadOnlyList<VerificationDto>> AllAsync(
        CancellationToken cancellationToken = default)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return [];
        }

        return await DescribeAsync(tenantId, cancellationToken);
    }

    public async Task<CompanyResult<VerificationDto>> SaveDetailsAsync(
        VerificationKind kind,
        VerificationDetailsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return CompanyResult<VerificationDto>.Failed(CompanyOutcome.NotFound);
        }

        CompanyVerification verification = await EnsureAsync(tenantId, kind, cancellationToken);

        VerificationRefusal refusal = VerificationRules.CheckDetails(verification, request.Number);

        if (refusal != VerificationRefusal.None)
        {
            return CompanyResult<VerificationDto>.Refused(refusal);
        }

        verification.Number = Trimmed(request.Number);
        verification.DocumentFileId = request.DocumentFileId;
        verification.UpdatedAt = timeProvider.GetUtcNow();

        audit.Record(
            AuditAction.Update,
            Module,
            $"{Label(await ProfileAsync(tenantId, cancellationToken), kind)} details saved",
            entityId: verification.Id,
            entityType: nameof(CompanyVerification));

        await dbContext.SaveChangesAsync(cancellationToken);

        return CompanyResult<VerificationDto>.Ok(
            await DescribeOneAsync(tenantId, kind, cancellationToken));
    }

    public async Task<CompanyResult<VerificationDto>> SubmitAsync(
        VerificationKind kind,
        CancellationToken cancellationToken = default)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return CompanyResult<VerificationDto>.Failed(CompanyOutcome.NotFound);
        }

        CompanyVerification verification = await EnsureAsync(tenantId, kind, cancellationToken);

        VerificationRefusal refusal = VerificationRules.CheckSubmission(verification);

        if (refusal != VerificationRefusal.None)
        {
            return CompanyResult<VerificationDto>.Refused(refusal);
        }

        verification.Stage = VerificationStage.Submitted;
        verification.SubmittedOn = await _calendar.TodayAsync(cancellationToken);

        // Cleared on resubmission: a previous rejection's reason beside a fresh
        // submission reads as though the new one had already been turned down.
        verification.DecisionNote = null;
        verification.ReviewedByUserId = null;
        verification.ReviewedByName = null;
        verification.ReviewedOn = null;

        verification.UpdatedAt = timeProvider.GetUtcNow();

        audit.Record(
            AuditAction.Update,
            Module,
            $"{Label(await ProfileAsync(tenantId, cancellationToken), kind)} submitted for review",
            entityId: verification.Id,
            entityType: nameof(CompanyVerification));

        await dbContext.SaveChangesAsync(cancellationToken);

        return CompanyResult<VerificationDto>.Ok(
            await DescribeOneAsync(tenantId, kind, cancellationToken));
    }

    public async Task<CompanyResult<VerificationDto>> WithdrawAsync(
        VerificationKind kind,
        CancellationToken cancellationToken = default)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return CompanyResult<VerificationDto>.Failed(CompanyOutcome.NotFound);
        }

        CompanyVerification verification = await EnsureAsync(tenantId, kind, cancellationToken);

        if (!VerificationFlow.CompanyCanMove(verification.Stage, VerificationStage.Draft))
        {
            return CompanyResult<VerificationDto>.Refused(
                VerificationRefusal.StageNotReachable);
        }

        VerificationStage was = verification.Stage;

        verification.Stage = VerificationStage.Draft;
        verification.SubmittedOn = null;
        verification.UpdatedAt = timeProvider.GetUtcNow();

        // The rejection reason is kept until it is resubmitted, so whoever is
        // fixing it can still read why it came back.
        audit.Record(
            AuditAction.Update,
            Module,
            was == VerificationStage.Rejected
                ? $"{Label(await ProfileAsync(tenantId, cancellationToken), kind)} "
                    + "reopened to correct"
                : $"{Label(await ProfileAsync(tenantId, cancellationToken), kind)} withdrawn",
            entityId: verification.Id,
            entityType: nameof(CompanyVerification));

        await dbContext.SaveChangesAsync(cancellationToken);

        return CompanyResult<VerificationDto>.Ok(
            await DescribeOneAsync(tenantId, kind, cancellationToken));
    }

    public async Task<CompanyResult<VerificationDto>> ReviewAsync(
        Guid tenantId,
        VerificationKind kind,
        VerificationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        // Filters ignored throughout: the reviewer is Motee staff, who belong
        // to no tenant, so nothing has made this company ambient. The tenant is
        // named in the route instead.
        CompanyVerification? verification = await dbContext.CompanyVerifications
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                row => row.TenantId == tenantId && row.Kind == kind, cancellationToken);

        if (verification is null)
        {
            return CompanyResult<VerificationDto>.Failed(CompanyOutcome.NotFound);
        }

        VerificationRefusal refusal = VerificationRules.CheckReview(
            verification, request.Stage, request.Note);

        if (refusal != VerificationRefusal.None)
        {
            return CompanyResult<VerificationDto>.Refused(refusal);
        }

        verification.Stage = request.Stage;
        verification.DecisionNote = Trimmed(request.Note);

        DateTimeOffset now = timeProvider.GetUtcNow();

        if (VerificationFlow.IsSettled(request.Stage))
        {
            verification.ReviewedOn = DateOnly.FromDateTime(now.UtcDateTime);
            verification.ReviewedByUserId = ReviewerId();
            verification.ReviewedByName = await ReviewerNameAsync(cancellationToken);
        }

        verification.UpdatedAt = now;

        ICountryProfile profile = await ProfileAsync(tenantId, cancellationToken);

        // Written against the tenant explicitly: a platform request carries no
        // ambient tenant, and an entry with none would be invisible on the
        // company's own audit screen — which is exactly who needs to see that
        // their registration was rejected.
        audit.Record(
            AuditAction.Update,
            Module,
            Sentence(Label(profile, kind), request.Stage, verification.DecisionNote),
            entityId: verification.Id,
            entityType: nameof(CompanyVerification),
            tenantId: tenantId);

        await dbContext.SaveChangesAsync(cancellationToken);

        return CompanyResult<VerificationDto>.Ok(
            await DescribeOneAsync(tenantId, kind, cancellationToken));
    }

    public async Task<IReadOnlyList<ReviewQueueItemDto>> QueueAsync(
        CancellationToken cancellationToken = default)
    {
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        // Across every tenant, so the filters are ignored on purpose: the
        // caller is Motee staff, who belong to no company. The permission check
        // on the route is what bounds this, not the query.
        var waiting = await dbContext.CompanyVerifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(row => row.Stage == VerificationStage.Submitted
                || row.Stage == VerificationStage.UnderReview)
            .Join(
                dbContext.Tenants.AsNoTracking(),
                verification => verification.TenantId,
                tenant => tenant.Id,
                (verification, tenant) => new
                {
                    Verification = verification,
                    tenant.Name,
                    tenant.CountryCode,
                })
            .ToListAsync(cancellationToken);

        return
        [
            // Longest waiting first, which is the order somebody works a queue
            // in. Submitted before under review at the same age: one has not
            // been picked up at all.
            .. waiting
                .OrderBy(row => row.Verification.SubmittedOn ?? today)
                .ThenBy(row => row.Verification.Stage)
                .Select(row =>
                {
                    ICountryProfile profile = countries.Get(row.CountryCode);
                    CompanyIdentifier identifier = Identifier(profile, row.Verification.Kind);

                    return new ReviewQueueItemDto
                    {
                        TenantId = row.Verification.TenantId,
                        CompanyName = row.Name,
                        CountryName = profile.DisplayName,
                        Kind = row.Verification.Kind,
                        Label = identifier.Label,
                        Authority = identifier.Authority,
                        Number = row.Verification.Number,
                        DocumentFileId = row.Verification.DocumentFileId,
                        Stage = row.Verification.Stage,
                        SubmittedOn = row.Verification.SubmittedOn,
                        DaysWaiting = row.Verification.SubmittedOn is DateOnly submitted
                            ? Math.Max(0, today.DayNumber - submitted.DayNumber)
                            : 0,
                    };
                }),
        ];
    }

    // ── shared ─────────────────────────────────────────────────────────────

    // Created on first touch rather than seeded at registration. A company that
    // never opens this screen does not need two rows, and a row written the
    // moment somebody looks is a row that disagrees with the defaults as soon as
    // the defaults change.
    private async Task<CompanyVerification> EnsureAsync(
        Guid tenantId,
        VerificationKind kind,
        CancellationToken cancellationToken)
    {
        CompanyVerification? held = await dbContext.CompanyVerifications
            .FirstOrDefaultAsync(row => row.Kind == kind, cancellationToken);

        if (held is not null)
        {
            return held;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        CompanyVerification created = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = kind,
            Stage = VerificationStage.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.CompanyVerifications.Add(created);

        return created;
    }

    private async Task<IReadOnlyList<VerificationDto>> DescribeAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        ICountryProfile profile = await ProfileAsync(tenantId, cancellationToken);

        List<CompanyVerification> held = await dbContext.CompanyVerifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(row => row.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        List<VerificationDto> described = [];

        foreach (VerificationKind kind in Enum.GetValues<VerificationKind>())
        {
            CompanyVerification row = held.FirstOrDefault(candidate => candidate.Kind == kind)
                ?? new CompanyVerification
                {
                    Id = Guid.Empty,
                    TenantId = tenantId,
                    Kind = kind,
                };

            described.Add(Describe(
                row, profile, await HistoryAsync(tenantId, row.Id, cancellationToken)));
        }

        return described;
    }

    private async Task<VerificationDto> DescribeOneAsync(
        Guid tenantId,
        VerificationKind kind,
        CancellationToken cancellationToken) =>
        (await DescribeAsync(tenantId, cancellationToken))
            .Single(verification => verification.Kind == kind);

    // From the audit trail. Every stage change is already recorded there with
    // who made it and when, so a history table of its own would be a second
    // copy that can disagree with the auditor's.
    private async Task<IReadOnlyList<VerificationEventDto>> HistoryAsync(
        Guid tenantId,
        Guid verificationId,
        CancellationToken cancellationToken)
    {
        if (verificationId == Guid.Empty)
        {
            return [];
        }

        List<AuditEntry> entries = await dbContext.AuditEntries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entry => entry.TenantId == tenantId && entry.EntityId == verificationId)
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        return
        [
            .. entries.Select(entry => new VerificationEventDto
            {
                Event = entry.Description,
                By = entry.ActorName,
                At = entry.CreatedAt,
            }),
        ];
    }

    private static VerificationDto Describe(
        CompanyVerification verification,
        ICountryProfile profile,
        IReadOnlyList<VerificationEventDto> history)
    {
        CompanyIdentifier identifier = Identifier(profile, verification.Kind);

        VerificationRefusal submission = VerificationRules.CheckSubmission(verification);

        List<string> outstanding = [];

        if (string.IsNullOrWhiteSpace(verification.Number))
        {
            outstanding.Add($"{identifier.NumberLabel} is missing.");
        }

        if (verification.DocumentFileId is null)
        {
            outstanding.Add("The certificate has not been attached.");
        }

        return new VerificationDto
        {
            Kind = verification.Kind,
            Label = identifier.Label,
            NumberLabel = identifier.NumberLabel,
            Authority = identifier.Authority,
            Description = identifier.Description,
            Number = verification.Number,
            DocumentFileId = verification.DocumentFileId,
            Stage = verification.Stage,
            SubmittedOn = verification.SubmittedOn,
            ReviewedByName = verification.ReviewedByName,
            ReviewedOn = verification.ReviewedOn,
            DecisionNote = verification.DecisionNote,
            Editable = VerificationFlow.IsEditableBy(verification.Stage),
            CanSubmit = submission == VerificationRefusal.None,
            Outstanding = outstanding,
            History = history,
        };
    }

    private static CompanyIdentifier Identifier(ICountryProfile profile, VerificationKind kind) =>
        kind == VerificationKind.Registration ? profile.Registration : profile.TaxIdentifier;

    private static string Label(ICountryProfile profile, VerificationKind kind) =>
        Identifier(profile, kind).Label;

    private static string Sentence(string label, VerificationStage stage, string? note) =>
        stage switch
        {
            VerificationStage.Verified => $"{label} verified",
            VerificationStage.Rejected => $"{label} rejected: {note}",
            VerificationStage.UnderReview => $"{label} taken under review",
            _ => $"{label} moved to {stage}",
        };

    private async Task<ICountryProfile> ProfileAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        CountryCode code = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.CountryCode)
            .FirstOrDefaultAsync(cancellationToken);

        return countries.Get(code);
    }

    private Guid? ReviewerId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private async Task<string?> ReviewerNameAsync(CancellationToken cancellationToken)
    {
        if (ReviewerId() is not Guid userId)
        {
            return null;
        }

        return await dbContext.Users
            .IgnoreQueryFilters()
            .Where(user => user.Id == userId)
            .Select(user => $"{user.FirstName} {user.LastName}")
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
