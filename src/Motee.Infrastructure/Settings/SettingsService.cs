using Microsoft.EntityFrameworkCore;
using Motee.Application.Audit;
using Motee.Application.Settings;
using Motee.Application.Tenancy;
using Motee.Domain.Audit;
using Motee.Domain.Settings;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Settings;

// The tenant-wide configuration behind the settings screen.
//
// Everything saved here is read by something: the module switches by the
// permission check, the password policy by the validator and the sign-in path,
// the holiday year by every leave balance. That was the whole problem with this
// screen — twenty controls, four endpoints, and the few that did save were
// saving where nothing looked.
internal sealed class SettingsService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    IAuditTrail audit,
    TimeProvider timeProvider) : ISettingsService
{
    private const string Module = "admin.settings";

    private readonly TenantCalendar _calendar = new(dbContext, currentTenant, timeProvider);

    // ── modules ────────────────────────────────────────────────────────────

    public async Task<ModuleSettings> ModulesAsync(CancellationToken cancellationToken = default)
    {
        TenantSettings settings = await SettingsAsync(cancellationToken);

        return Describe(StateOf(settings));
    }

    public async Task<SettingsResult<ModuleSettings>> SaveModulesAsync(
        ModuleSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        Tenant? tenant = await TenantAsync(cancellationToken);

        if (tenant is null)
        {
            return SettingsResult<ModuleSettings>.Failed(SettingsOutcome.NotFound);
        }

        // Every id has to be one we know. An unknown one is not quietly dropped
        // here the way it is on the way out of storage: on a save it means the
        // client and the catalogue have parted company, and silently discarding
        // the switch somebody just flicked is worse than saying so.
        foreach (ModuleChoice choice in request.Modules)
        {
            if (FeatureCatalogue.Normalise(choice.Id) is null)
            {
                return SettingsResult<ModuleSettings>.Refused(SettingsRefusal.UnknownFeature);
            }
        }

        // Only what the payload mentions changes. The panel posts every row, but
        // a client that posts one switch should not switch off the features it
        // did not mention.
        FeatureState before = StateOf(tenant.Settings);

        HashSet<string> enabled = new(before.Enabled, StringComparer.Ordinal);
        HashSet<string> hidden = new(before.Hidden, StringComparer.Ordinal);

        foreach (ModuleChoice choice in request.Modules)
        {
            string id = FeatureCatalogue.Normalise(choice.Id)!;

            if (choice.Enabled)
            {
                enabled.Add(id);
            }
            else
            {
                enabled.Remove(id);
            }

            if (choice.ShowInSidebar)
            {
                hidden.Remove(id);
            }
            else
            {
                hidden.Add(id);
            }
        }

        FeatureState after = FeatureState.Of(enabled, hidden);

        // Replaced rather than mutated: the settings document is a jsonb column
        // whose comparer serialises, so changing a list in place leaves EF
        // seeing no change and the save does nothing.
        tenant.Settings = tenant.Settings with
        {
            EnabledModules = [.. FeatureCatalogue.Ids.Where(after.Enabled.Contains)],
            HiddenModules = [.. FeatureCatalogue.Ids.Where(after.Hidden.Contains)],
        };

        IEnumerable<string> switchedOff = before.Enabled.Except(after.Enabled, StringComparer.Ordinal);
        IEnumerable<string> switchedOn = after.Enabled.Except(before.Enabled, StringComparer.Ordinal);

        audit.Record(
            AuditAction.Update,
            Module,
            Sentence([.. switchedOn], [.. switchedOff]),
            entityId: tenant.Id,
            entityType: nameof(Tenant));

        await dbContext.SaveChangesAsync(cancellationToken);

        return SettingsResult<ModuleSettings>.Ok(Describe(after));
    }

    // ── password policy ────────────────────────────────────────────────────

    public async Task<PasswordPolicyView> PasswordPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        TenantSettings settings = await SettingsAsync(cancellationToken);

        return new PasswordPolicyView { Policy = settings.PasswordPolicy };
    }

    public async Task<SettingsResult<PasswordPolicyView>> SavePasswordPolicyAsync(
        PasswordPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        Tenant? tenant = await TenantAsync(cancellationToken);

        if (tenant is null)
        {
            return SettingsResult<PasswordPolicyView>.Failed(SettingsOutcome.NotFound);
        }

        PasswordPolicy policy = PasswordRules.Clamp(new PasswordPolicy
        {
            MinLength = request.MinLength,
            RequireUppercase = request.RequireUppercase,
            RequireNumbers = request.RequireNumbers,
            RequireSymbols = request.WantsSymbols,
            ExpiryDays = request.ExpiryDays,
            PreventReuse = request.PreventReuse,
            SessionTimeoutMinutes = request.SessionTimeoutMinutes,
        });

        tenant.Settings = tenant.Settings with { PasswordPolicy = policy };

        audit.Record(
            AuditAction.Update,
            Module,
            $"Password policy set to {policy.MinLength} characters minimum",
            entityId: tenant.Id,
            entityType: nameof(Tenant));

        await dbContext.SaveChangesAsync(cancellationToken);

        return SettingsResult<PasswordPolicyView>.Ok(new PasswordPolicyView { Policy = policy });
    }

    // ── holiday year ───────────────────────────────────────────────────────

    public async Task<HolidayYearSettings> HolidayYearsAsync(
        CancellationToken cancellationToken = default)
    {
        TenantSettings settings = await SettingsAsync(cancellationToken);

        (int month, int day) =
            HolidayYears.Turnover(settings.LeaveYearStartMonth, settings.LeaveYearStartDay);

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        return new HolidayYearSettings
        {
            StartMonth = month,
            StartDay = day,
            Cycles = HolidayYears.Cycles(month, day, today),
        };
    }

    public async Task<SettingsResult<HolidayYearSettings>> SaveHolidayYearAsync(
        HolidayYearRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!HolidayYears.IsValidTurnover(request.StartMonth, request.StartDay))
        {
            return SettingsResult<HolidayYearSettings>.Refused(SettingsRefusal.TurnoverNotADate);
        }

        Tenant? tenant = await TenantAsync(cancellationToken);

        if (tenant is null)
        {
            return SettingsResult<HolidayYearSettings>.Failed(SettingsOutcome.NotFound);
        }

        tenant.Settings = tenant.Settings with
        {
            LeaveYearStartMonth = request.StartMonth,
            LeaveYearStartDay = request.StartDay,
        };

        DateOnly today = await _calendar.TodayAsync(cancellationToken);

        IReadOnlyList<HolidayYearCycle> cycles =
            HolidayYears.Cycles(request.StartMonth, request.StartDay, today);

        // Worth an entry on its own: moving the turnover moves which year
        // everybody's leave is filed under, and the balances change without
        // anybody touching a leave request.
        audit.Record(
            AuditAction.Update,
            Module,
            $"Holiday year now turns over on {Turnover(request.StartMonth, request.StartDay)}",
            entityId: tenant.Id,
            entityType: nameof(Tenant));

        await dbContext.SaveChangesAsync(cancellationToken);

        return SettingsResult<HolidayYearSettings>.Ok(new HolidayYearSettings
        {
            StartMonth = request.StartMonth,
            StartDay = request.StartDay,
            Cycles = cycles,
        });
    }

    // ── subscription ───────────────────────────────────────────────────────

    public async Task<SubscriptionView> SubscriptionAsync(
        CancellationToken cancellationToken = default)
    {
        Tenant? tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == currentTenant.TenantId, cancellationToken);

        if (tenant is null)
        {
            return new SubscriptionView
            {
                CompanyName = string.Empty,
                Plan = string.Empty,
                PlanLabel = string.Empty,
                Status = string.Empty,
                UsedSeats = 0,
            };
        }

        // Counted, not stored. The screen's demo data carries a usedSeats that
        // disagrees with the user list the moment somebody is deactivated.
        // Platform staff are excluded: they are Motee's people, not the
        // company's, and the tenant filter does not apply to users.
        int seats = await dbContext.Users
            .CountAsync(
                user => user.TenantId == tenant.Id && !user.IsPlatformStaff,
                cancellationToken);

        return new SubscriptionView
        {
            CompanyName = tenant.Name,
            Plan = tenant.Plan,
            PlanLabel = Label(tenant.Plan),
            Status = tenant.Status,
            TrialEndsAt = tenant.TrialEndsAt,
            BillingEmail = tenant.BillingEmail,
            UsedSeats = seats,
        };
    }

    // ── shared ─────────────────────────────────────────────────────────────

    private async Task<Tenant?> TenantAsync(CancellationToken cancellationToken) =>
        currentTenant.TenantId is Guid tenantId
            ? await dbContext.Tenants.FirstOrDefaultAsync(
                tenant => tenant.Id == tenantId, cancellationToken)
            : null;

    private async Task<TenantSettings> SettingsAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return new TenantSettings();
        }

        TenantSettings? settings = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Settings)
            .FirstOrDefaultAsync(cancellationToken);

        return settings ?? new TenantSettings();
    }

    private static FeatureState StateOf(TenantSettings settings) =>
        settings.EnabledModules.Count == 0
            ? FeatureState.Default
            : FeatureState.Of(settings.EnabledModules, settings.HiddenModules);

    private static ModuleSettings Describe(FeatureState state) => new()
    {
        Modules =
        [
            .. FeatureCatalogue.All.Select(feature => new ModuleSettingRow
            {
                Id = feature.Id,
                Name = feature.Label,
                Description = feature.Description,
                Enabled = state.Enabled.Contains(feature.Id),
                ShowInSidebar = state.ShowsInSidebar(feature.Id),
                Essential = feature.Essential,
                ClosesModules = feature.ModuleKeys.Count,
            }),
        ],
    };

    private static string Sentence(
        IReadOnlyList<string> switchedOn,
        IReadOnlyList<string> switchedOff)
    {
        List<string> parts = [];

        if (switchedOn.Count > 0)
        {
            parts.Add($"enabled {string.Join(", ", switchedOn.Select(Name))}");
        }

        if (switchedOff.Count > 0)
        {
            parts.Add($"disabled {string.Join(", ", switchedOff.Select(Name))}");
        }

        // Saving the screen without changing a switch is still a save, and an
        // entry saying nothing changed is more useful than no entry at all —
        // it answers "did anybody touch this".
        return parts.Count == 0
            ? "Module settings saved with no change"
            : $"Modules: {string.Join("; ", parts)}";
    }

    private static string Name(string featureId) =>
        FeatureCatalogue.Find(featureId)?.Label ?? featureId;

    private static string Turnover(int month, int day) =>
        $"{day} {new DateOnly(2024, month, 1):MMMM}";

    // Title-cased from the stored id, because there is no plan catalogue to
    // look a label up in. "growth" becomes "Growth", which is what the screen
    // shows; inventing prices or seat allowances to go with it would be
    // inventing a billing system.
    private static string Label(string plan) =>
        string.IsNullOrWhiteSpace(plan)
            ? string.Empty
            : string.Concat(char.ToUpperInvariant(plan[0]), plan[1..]);
}
