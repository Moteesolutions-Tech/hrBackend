using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Leave;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class LeaveTypeConfiguration : IEntityTypeConfiguration<LeaveType>
{
    public void Configure(EntityTypeBuilder<LeaveType> builder)
    {
        builder.ToTable("leave_types");

        builder.HasKey(type => type.Id);

        builder.Property(type => type.Code).HasMaxLength(40);
        builder.Property(type => type.Name).HasMaxLength(100).IsRequired();

        // One "Annual leave" per company. Filtered to the seeded codes because a tenant
        // creating two custom types with no code is their business, but two rows both
        // claiming to be the annual-leave type would make the seeder's own lookups
        // ambiguous.
        builder.HasIndex(type => new { type.TenantId, type.Code })
            .IsUnique()
            .HasDatabaseName("ix_leave_types_code")
            .HasFilter("code IS NOT NULL");

        builder.HasIndex(type => new { type.TenantId, type.Name })
            .IsUnique()
            .HasDatabaseName("ix_leave_types_name");
    }
}

internal sealed class LeavePolicyConfiguration : IEntityTypeConfiguration<LeavePolicy>
{
    public void Configure(EntityTypeBuilder<LeavePolicy> builder)
    {
        builder.ToTable("leave_policies");

        builder.HasKey(policy => policy.Id);

        builder.Property(policy => policy.Name).HasMaxLength(100).IsRequired();
        builder.Property(policy => policy.Description).HasMaxLength(1000);
        builder.Property(policy => policy.AttachmentRequirement).HasMaxLength(500);
        builder.Property(policy => policy.Eligibility).HasMaxLength(500);
        builder.Property(policy => policy.PublicHolidayNote).HasMaxLength(500);
        builder.Property(policy => policy.DocumentUrl).HasMaxLength(2000);

        // Days are money-adjacent, so they are exact rather than floating. 0.1 of a day
        // drifting is a day lost every ten years of arithmetic, and nobody would find it.
        builder.Property(policy => policy.DaysPerYear).HasPrecision(6, 2);
        builder.Property(policy => policy.MaxCarryOverDays).HasPrecision(6, 2);

        // RESTRICT: a policy without its leave type describes nothing. Deactivating the
        // type is the way to retire it, which keeps existing requests readable.
        builder.HasOne<LeaveType>()
            .WithMany()
            .HasForeignKey(policy => policy.LeaveTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        // One live policy per leave type. Two would make "the entitlement" ambiguous and
        // whichever the query happened to return first would silently win.
        builder.HasIndex(policy => new { policy.TenantId, policy.LeaveTypeId })
            .IsUnique()
            .HasDatabaseName("ix_leave_policies_type")
            .HasFilter("is_active");
    }
}

internal sealed class LeaveRequestConfiguration : IEntityTypeConfiguration<LeaveRequest>
{
    public void Configure(EntityTypeBuilder<LeaveRequest> builder)
    {
        builder.ToTable("leave_requests");

        builder.HasKey(request => request.Id);

        // Text, like every other enum here: readable in psql, and reordering the enum
        // cannot silently reclassify who is on leave.
        builder.Property(request => request.Status)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(request => request.HalfDayPeriod).HasMaxLength(10);
        builder.Property(request => request.Reason).HasMaxLength(1000);
        builder.Property(request => request.Notes).HasMaxLength(2000);
        builder.Property(request => request.CancellationReason).HasMaxLength(500);
        builder.Property(request => request.TotalDays).HasPrecision(6, 2);

        // RESTRICT on both: a request is the record of somebody's absence, and it has to
        // survive the tidying up of an employee record or a retired leave type.
        builder.HasOne<Domain.Employees.Employee>()
            .WithMany()
            .HasForeignKey(request => request.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<LeaveType>()
            .WithMany()
            .HasForeignKey(request => request.LeaveTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        // The balance query: this person's requests for this type in this year.
        builder.HasIndex(request => new
        {
            request.TenantId,
            request.EmployeeId,
            request.LeaveTypeId,
            request.LeaveYearStart,
        }).HasDatabaseName("ix_leave_requests_balance");

        // The calendar and the timeline: who is away over a window.
        builder.HasIndex(request => new { request.TenantId, request.StartDate, request.EndDate })
            .HasDatabaseName("ix_leave_requests_window");

        builder.HasIndex(request => request.ApprovalInstanceId)
            .HasDatabaseName("ix_leave_requests_approval");
    }
}

internal sealed class LeaveAdjustmentConfiguration : IEntityTypeConfiguration<LeaveAdjustment>
{
    public void Configure(EntityTypeBuilder<LeaveAdjustment> builder)
    {
        builder.ToTable("leave_adjustments");

        builder.HasKey(adjustment => adjustment.Id);

        builder.Property(adjustment => adjustment.Days).HasPrecision(6, 2);
        builder.Property(adjustment => adjustment.Reason).HasMaxLength(500).IsRequired();

        builder.HasOne<Domain.Employees.Employee>()
            .WithMany()
            .HasForeignKey(adjustment => adjustment.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(adjustment => new
        {
            adjustment.TenantId,
            adjustment.EmployeeId,
            adjustment.LeaveTypeId,
            adjustment.LeaveYearStart,
        }).HasDatabaseName("ix_leave_adjustments_balance");
    }
}

internal sealed class LeaveCarryOverConfiguration : IEntityTypeConfiguration<LeaveCarryOver>
{
    public void Configure(EntityTypeBuilder<LeaveCarryOver> builder)
    {
        builder.ToTable("leave_carry_overs");

        builder.HasKey(carry => carry.Id);

        builder.Property(carry => carry.Days).HasPrecision(6, 2);

        builder.HasOne<Domain.Employees.Employee>()
            .WithMany()
            .HasForeignKey(carry => carry.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        // One carry-over per person per type per year. Running the year-end twice must
        // not double everybody's brought-forward days.
        builder.HasIndex(carry => new
        {
            carry.TenantId,
            carry.EmployeeId,
            carry.LeaveTypeId,
            carry.LeaveYearStart,
        }).IsUnique().HasDatabaseName("ix_leave_carry_overs_unique");
    }
}

internal sealed class PublicHolidayConfiguration : IEntityTypeConfiguration<PublicHoliday>
{
    public void Configure(EntityTypeBuilder<PublicHoliday> builder)
    {
        builder.ToTable("public_holidays");

        builder.HasKey(holiday => holiday.Id);

        builder.Property(holiday => holiday.Name).HasMaxLength(100).IsRequired();
        builder.Property(holiday => holiday.CountryCode).HasMaxLength(2);

        // The same day twice would be counted twice by nothing — the day count uses a
        // set — but it would show as a duplicate on the calendar and invite somebody to
        // delete the wrong one.
        builder.HasIndex(holiday => new { holiday.TenantId, holiday.Date, holiday.CountryCode })
            .IsUnique()
            .HasDatabaseName("ix_public_holidays_date");

        // Every day count for a leave request reads a date range of these.
        builder.HasIndex(holiday => new { holiday.TenantId, holiday.Date })
            .HasDatabaseName("ix_public_holidays_lookup");
    }
}

internal sealed class LeaveBlackoutConfiguration : IEntityTypeConfiguration<LeaveBlackout>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<LeaveBlackout> builder)
    {
        builder.ToTable("leave_blackouts");

        builder.HasKey(blackout => blackout.Id);

        builder.Property(blackout => blackout.Name).HasMaxLength(150).IsRequired();
        builder.Property(blackout => blackout.Reason).HasMaxLength(500);

        // jsonb, like an access level's permissions: read whole, written whole, and never
        // queried by their parts — the date overlap is what the database narrows on, and
        // a company has a handful of live blackouts to test in memory after that.
        AsJsonb(builder, blackout => blackout.LeaveTypeIds, "leave_type_ids");
        AsJsonb(builder, blackout => blackout.DepartmentIds, "department_ids");

        // The query every submission runs: which blackouts touch these dates.
        builder.HasIndex(blackout => new
        {
            blackout.TenantId,
            blackout.StartDate,
            blackout.EndDate,
        }).HasDatabaseName("ix_leave_blackouts_range");
    }

    private static void AsJsonb(
        EntityTypeBuilder<LeaveBlackout> builder,
        System.Linq.Expressions.Expression<Func<LeaveBlackout, IReadOnlyList<Guid>>> property,
        string column) =>
        builder.Property(property)
            .HasColumnName(column)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<IReadOnlyList<Guid>>(json, Json)!,
                new ValueComparer<IReadOnlyList<Guid>>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json).GetHashCode(
                        StringComparison.Ordinal),
                    value => JsonSerializer.Deserialize<IReadOnlyList<Guid>>(
                        JsonSerializer.Serialize(value, Json), Json)!));
}
