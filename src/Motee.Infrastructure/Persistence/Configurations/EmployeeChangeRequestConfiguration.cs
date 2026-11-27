using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Employees;
using Motee.Domain.Files;
using Motee.Domain.Tenants;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class EmployeeChangeRequestConfiguration
    : IEntityTypeConfiguration<EmployeeChangeRequest>
{
    public void Configure(EntityTypeBuilder<EmployeeChangeRequest> builder)
    {
        builder.ToTable("employee_change_requests");

        builder.HasKey(request => request.Id);

        // The catalogue key, not a label. Short because it is an identifier.
        builder.Property(request => request.Field).HasMaxLength(60).IsRequired();

        // Wide enough for the longest field in the catalogue, which is the address.
        builder.Property(request => request.RequestedValue).HasMaxLength(400);
        builder.Property(request => request.CurrentValue).HasMaxLength(400);

        builder.Property(request => request.Reason)
            .HasMaxLength(ChangeRequests.LongestReason)
            .IsRequired();

        builder.Property(request => request.DecisionNote).HasMaxLength(ChangeRequests.LongestNote);

        builder.Property(request => request.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(request => request.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(request => request.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        // Set null rather than cascade: deleting the evidence must not delete the
        // record that a change was asked for and decided. The decision outlives the
        // document, the same way a referral outlives the absence that caused it.
        builder.HasOne<StoredFile>()
            .WithMany()
            .HasForeignKey(request => request.FileId)
            .OnDelete(DeleteBehavior.SetNull);

        // The HR queue: pending, oldest first.
        builder.HasIndex(request => new { request.TenantId, request.Status, request.RequestedAt })
            .HasDatabaseName("ix_employee_change_requests_queue");

        // One person's log, and the duplicate-pending check on every raise.
        builder.HasIndex(request => new { request.TenantId, request.EmployeeId, request.Field })
            .HasDatabaseName("ix_employee_change_requests_employee_field");
    }
}
