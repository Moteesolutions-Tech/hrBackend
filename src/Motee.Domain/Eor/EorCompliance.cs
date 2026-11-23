using Motee.Domain.Common;

namespace Motee.Domain.Eor;

public enum ComplianceKey
{
    Contract,
    WorkPermit,
    TaxRegistration,
    StatutoryBenefits,
    LocalId,
}

// What is recorded against a compliance requirement.
//
// Expired is not one of them, because it is not something somebody sets: a
// permit marked Complete with an expiry in the past is expired whatever the
// dropdown says, and that is worked out rather than stored. The screen has no
// expiry at all, so a work permit that lapsed last month still reads
// "Complete" — on an EOR register that is the right-to-work record.
public enum ComplianceState
{
    Pending,
    Complete,
    ActionRequired,
    NotApplicable,
}

// How a requirement actually stands today.
public enum ComplianceStanding
{
    Pending,
    Complete,
    ActionRequired,
    NotApplicable,

    // Was complete and has run out.
    Expired,

    // Complete and running out soon enough to act on.
    ExpiringSoon,
}

public class EorComplianceItem : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EngagementId { get; set; }

    public ComplianceKey Key { get; set; }

    public ComplianceState State { get; set; } = ComplianceState.Pending;

    public string? Note { get; set; }

    // The document behind it — the signed contract, the permit, the tax
    // registration letter. Optional, because a provider confirming something by
    // email is still a record, but a requirement marked complete with nothing
    // attached is reported as such rather than counted as evidenced.
    public Guid? EvidenceFileId { get; set; }

    // When the thing expires, where it expires at all. A permit and a local ID
    // do; a signed contract does not.
    public DateOnly? ExpiresOn { get; set; }

    // When somebody here last checked it, as opposed to when the provider says
    // it was done.
    public DateOnly? VerifiedOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record ComplianceRequirement
{
    public required ComplianceKey Key { get; init; }

    public required string Label { get; init; }

    // Whether this requirement is one that lapses. Sent to the client so the
    // form asks for an expiry where there is one to ask for, instead of
    // offering the field on a signed contract.
    public bool Expires { get; init; }

    // Whether a worker can legitimately be in post without it. A right to work
    // cannot be outstanding while somebody is working; a statutory benefits
    // enrolment catching up in the first weeks is normal.
    public bool BlocksWork { get; init; }
}

public static class ComplianceCatalogue
{
    // Fixed, the same five the screen shows. Only the ticks are stored, the way
    // the go-live checklist does it — a tenant inventing their own requirements
    // would make the register incomparable across countries, which is the one
    // thing it is for.
    public static readonly IReadOnlyList<ComplianceRequirement> All =
    [
        new()
        {
            Key = ComplianceKey.Contract,
            Label = "Employment contract",
            BlocksWork = true,
        },
        new()
        {
            Key = ComplianceKey.WorkPermit,
            Label = "Work permit / right to work",
            Expires = true,
            BlocksWork = true,
        },
        new()
        {
            Key = ComplianceKey.TaxRegistration,
            Label = "Local tax registration",
            BlocksWork = true,
        },

        // Not blocking. Enrolment commonly completes in the first weeks of an
        // engagement, and treating it as a bar to working would flag most new
        // starters as unlawful.
        new()
        {
            Key = ComplianceKey.StatutoryBenefits,
            Label = "Statutory benefits enrolment",
        },
        new()
        {
            Key = ComplianceKey.LocalId,
            Label = "Local ID / verification",
            Expires = true,
        },
    ];

    public static ComplianceRequirement Of(ComplianceKey key) =>
        All.Single(requirement => requirement.Key == key);
}

public static class Compliance
{
    // Close enough to act on. A work permit needs renewing before it lapses,
    // and thirty days is the window in which somebody can still do something
    // about it.
    public const int ExpiringWithinDays = 30;

    // What a requirement amounts to today.
    //
    // The expiry wins over the stored state, which is the whole reason the
    // expiry exists. Somebody ticking Complete in March does not make a permit
    // valid in December.
    public static ComplianceStanding StandingOf(EorComplianceItem item, DateOnly today)
    {
        if (item.State != ComplianceState.Complete)
        {
            return item.State switch
            {
                ComplianceState.Pending => ComplianceStanding.Pending,
                ComplianceState.ActionRequired => ComplianceStanding.ActionRequired,
                _ => ComplianceStanding.NotApplicable,
            };
        }

        if (item.ExpiresOn is not DateOnly expires)
        {
            return ComplianceStanding.Complete;
        }

        if (expires < today)
        {
            return ComplianceStanding.Expired;
        }

        return expires.DayNumber - today.DayNumber <= ExpiringWithinDays
            ? ComplianceStanding.ExpiringSoon
            : ComplianceStanding.Complete;
    }

    // Whether this requirement is satisfied, for the purpose of judging the
    // engagement. Expiring soon still counts: it has not lapsed yet, and
    // flagging the worker as non-compliant a month early would make the
    // register cry wolf.
    public static bool IsSatisfied(ComplianceStanding standing) =>
        standing is ComplianceStanding.Complete
            or ComplianceStanding.ExpiringSoon
            or ComplianceStanding.NotApplicable;

    // The requirements standing between this worker and being lawfully in post.
    //
    // Sentences rather than keys, because this reaches a screen somebody has to
    // act on. Only the blocking requirements, and only while the engagement is
    // live: a worker who left in June is not an outstanding right-to-work risk.
    public static IReadOnlyList<string> Blockers(
        IReadOnlyList<EorComplianceItem> items,
        DateOnly today)
    {
        List<string> blockers = [];

        foreach (ComplianceRequirement requirement in ComplianceCatalogue.All)
        {
            if (!requirement.BlocksWork)
            {
                continue;
            }

            EorComplianceItem? item = items.FirstOrDefault(held => held.Key == requirement.Key);

            // Nothing recorded is not the same as nothing required. An absent
            // row is outstanding, because somebody has yet to look at it.
            ComplianceStanding standing = item is null
                ? ComplianceStanding.Pending
                : StandingOf(item, today);

            if (IsSatisfied(standing))
            {
                continue;
            }

            blockers.Add(standing switch
            {
                ComplianceStanding.Expired =>
                    $"{requirement.Label} expired on {item!.ExpiresOn:d MMMM yyyy}.",
                ComplianceStanding.ActionRequired =>
                    $"{requirement.Label} needs attention.",
                _ => $"{requirement.Label} is outstanding.",
            });
        }

        return blockers;
    }

    // What a new engagement starts with: everything outstanding.
    //
    // The screen defaults the employment contract to Complete, so a worker
    // added a minute ago is recorded as having a signed contract nobody has
    // seen. On a register whose purpose is proving the paperwork exists, a
    // default of "done" is the one default that must not be there.
    public static IReadOnlyList<EorComplianceItem> Starting(
        Guid engagementId,
        Guid tenantId,
        DateTimeOffset now) =>
        [
            .. ComplianceCatalogue.All.Select(requirement => new EorComplianceItem
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EngagementId = engagementId,
                Key = requirement.Key,
                State = ComplianceState.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            }),
        ];

    public static bool IsFullyCompliant(
        IReadOnlyList<EorComplianceItem> items,
        DateOnly today) =>
        ComplianceCatalogue.All.All(requirement =>
            items.FirstOrDefault(held => held.Key == requirement.Key) is EorComplianceItem item
                && IsSatisfied(StandingOf(item, today)));
}
