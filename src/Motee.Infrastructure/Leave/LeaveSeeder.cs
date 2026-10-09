using Motee.Domain.Common;
using Motee.Domain.Leave;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Leave;

// The leave setup a new company starts with.
//
// Country-dependent, unlike the other seeders here, because statutory minimums differ and
// a shared default would be wrong for one of the two countries on the day it was written.
// These are starting points a tenant edits freely, like the access levels and the
// approval chain beside them.
internal sealed class LeaveSeeder(MoteeDbContext dbContext, TimeProvider timeProvider)
{
    // Statutory minimums, not generous defaults. A company that offers more should say so
    // deliberately rather than inherit a number we invented — and one that never looks at
    // the screen is at least compliant.
    //
    // UK: 28 days including bank holidays (Working Time Regulations). Recorded as 28 with
    // holidays excluded from the count, which is how most UK employers actually run it —
    // the alternative charges staff for days the office is shut.
    //
    // Nigeria: 6 working days after twelve months' service (Labour Act s.18). Low enough
    // that most employers improve on it, which is exactly why it should be visible rather
    // than assumed.
    private static decimal AnnualDaysFor(CountryCode country) =>
        country == CountryCode.UnitedKingdom ? 28m : 6m;

    // Statutory sick pay in both countries runs far longer than most companies grant in
    // full pay. Twelve days is a common company scheme and a number HR will recognise as
    // ours rather than the law's.
    private const decimal SickDays = 12m;

    public void Seed(Guid tenantId, CountryCode country)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        AddType(tenantId, now, "annual", "Annual Leave", 0, policy => policy with
        {
            DaysPerYear = AnnualDaysFor(country),
            MinNoticeDays = 14,
            MaxConsecutiveDays = 0,
            CarryOverAllowed = true,
            MaxCarryOverDays = 5m,
            CarryOverExpiryMonths = 3,
            AccruesMonthly = true,
            PublicHolidayNote = "Public holidays are not deducted from your entitlement.",
        });

        // No notice, because nobody gives notice of falling ill. A fit note is what the
        // policy asks for instead, and only past a week — asking for one on day one is
        // how staff come to work ill.
        AddType(tenantId, now, "sick", "Sick Leave", 1, policy => policy with
        {
            DaysPerYear = SickDays,
            MinNoticeDays = 0,
            RequiresMedicalCertificate = true,
            AttachmentRequirement = "Fit note required for absences longer than 7 days.",
        });

        // Statutory maternity is far longer than annual leave and is not a bank anybody
        // runs down — it is a block of time around one event, so the entitlement is a
        // ceiling per occasion rather than days that lapse at the year end.
        AddType(tenantId, now, "maternity", "Maternity Leave", 2, policy => policy with
        {
            DaysPerYear = country == CountryCode.UnitedKingdom ? 365m : 84m,
            MinNoticeDays = 28,
            TracksBalance = false,
        });

        AddType(tenantId, now, "paternity", "Paternity Leave", 3, policy => policy with
        {
            DaysPerYear = country == CountryCode.UnitedKingdom ? 10m : 5m,
            MinNoticeDays = 28,
            TracksBalance = false,
        });

        AddType(tenantId, now, "compassionate", "Compassionate Leave", 4, policy => policy with
        {
            DaysPerYear = 5m,
            MinNoticeDays = 0,
            TracksBalance = false,
        });

        // Unpaid leave has no entitlement to run down — the limit is what a manager will
        // agree to, not a number of days somebody has banked.
        AddType(tenantId, now, "unpaid", "Unpaid Leave", 5, policy => policy with
        {
            DaysPerYear = 0m,
            MinNoticeDays = 14,
            IsPaidType = false,
            TracksBalance = false,
        });

        SeedHolidays(tenantId, country, now);
    }

    // This year and the next two. Far enough ahead that somebody booking Christmas in
    // eighteen months gets the right day count, and short enough that the rows stay
    // reviewable — a tenant who wants more can add years, and the calendar is computed so
    // extending is arithmetic rather than research.
    public void SeedHolidays(Guid tenantId, CountryCode country, DateTimeOffset now)
    {
        int thisYear = now.Year;

        for (int year = thisYear; year <= thisYear + 2; year++)
        {
            foreach ((DateOnly date, string name) in PublicHolidayCalendar.For(country, year))
            {
                dbContext.PublicHolidays.Add(new PublicHoliday
                {
                    Id = Guid.NewGuid(),

                    // Set explicitly: registration runs before any tenant is current, so
                    // the context has nothing to stamp these from.
                    TenantId = tenantId,
                    Date = date,
                    Name = name,
                    CountryCode = country.Value,
                    CreatedAt = now,
                });
            }
        }
    }

    private void AddType(
        Guid tenantId,
        DateTimeOffset now,
        string code,
        string name,
        int sequence,
        Func<PolicyDraft, PolicyDraft> configure)
    {
        PolicyDraft draft = configure(new PolicyDraft());

        Guid typeId = Guid.NewGuid();

        dbContext.LeaveTypes.Add(new LeaveType
        {
            Id = typeId,
            TenantId = tenantId,
            Code = code,
            Name = name,
            IsPaid = draft.IsPaidType,
            IsActive = true,
            Sequence = sequence,
            CreatedAt = now,
            UpdatedAt = now,
        });

        dbContext.LeavePolicies.Add(new LeavePolicy
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            LeaveTypeId = typeId,
            Name = $"{name} Policy",
            DaysPerYear = draft.DaysPerYear,
            MinNoticeDays = draft.MinNoticeDays,
            MaxConsecutiveDays = draft.MaxConsecutiveDays,
            ExcludePublicHolidays = true,
            RequiresMedicalCertificate = draft.RequiresMedicalCertificate,
            AttachmentRequirement = draft.AttachmentRequirement,
            TracksBalance = draft.TracksBalance,
            CarryOverAllowed = draft.CarryOverAllowed,
            MaxCarryOverDays = draft.MaxCarryOverDays,
            CarryOverExpiryMonths = draft.CarryOverExpiryMonths,
            AccruesMonthly = draft.AccruesMonthly,
            PublicHolidayNote = draft.PublicHolidayNote,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    // A plain bag so each type above reads as the handful of things that make it
    // different, rather than as fifteen arguments most of which are the default.
    private sealed record PolicyDraft
    {
        public decimal DaysPerYear { get; init; }

        public int MinNoticeDays { get; init; }

        public int MaxConsecutiveDays { get; init; }

        public bool RequiresMedicalCertificate { get; init; }

        public string? AttachmentRequirement { get; init; }

        public bool CarryOverAllowed { get; init; }

        public decimal MaxCarryOverDays { get; init; }

        public int CarryOverExpiryMonths { get; init; }

        public bool AccruesMonthly { get; init; }

        public string? PublicHolidayNote { get; init; }

        public bool IsPaidType { get; init; } = true;

        public bool TracksBalance { get; init; } = true;
    }
}
