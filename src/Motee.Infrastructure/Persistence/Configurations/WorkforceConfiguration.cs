using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Workforce;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class WorkforceRequestConfiguration : IEntityTypeConfiguration<WorkforceRequest>
{
    public void Configure(EntityTypeBuilder<WorkforceRequest> builder)
    {
        builder.ToTable("workforce_requests");

        builder.HasKey(request => request.Id);

        builder.Property(request => request.Reference).HasMaxLength(20);
        builder.Property(request => request.Position).HasMaxLength(200).IsRequired();
        builder.Property(request => request.Grade).HasMaxLength(60);
        builder.Property(request => request.Justification).IsRequired();
        builder.Property(request => request.CostCentreCode).HasMaxLength(40);
        builder.Property(request => request.Currency).HasMaxLength(3);
        builder.Property(request => request.RequestedByName).HasMaxLength(200);

        builder.Property(request => request.BudgetEstimate).HasPrecision(18, 4);

        foreach (string property in (string[])
        [
            nameof(WorkforceRequest.HiringReason),
            nameof(WorkforceRequest.VacancyType),
            nameof(WorkforceRequest.Urgency),
        ])
        {
            builder.Property(property).HasConversion<string>().HasMaxLength(20).IsRequired();
        }

        // Quoted to Finance and in approval emails, so two requests sharing
        // one is worse than a draft having none. Partial, because drafts have
        // none.
        builder
            .HasIndex(request => new { request.TenantId, request.Reference })
            .IsUnique()
            .HasFilter("reference IS NOT NULL")
            .HasDatabaseName("ix_workforce_requests_reference");

        builder.HasIndex(request => new { request.TenantId, request.CreatedAt });

        builder.HasIndex(request => request.DepartmentId);
    }
}
