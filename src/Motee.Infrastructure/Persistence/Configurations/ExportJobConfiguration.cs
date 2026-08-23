using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Exports;
using Motee.Domain.Tenants;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class ExportJobConfiguration : IEntityTypeConfiguration<ExportJob>
{
    public void Configure(EntityTypeBuilder<ExportJob> builder)
    {
        builder.ToTable("export_jobs");
        builder.HasKey(job => job.Id);

        builder.Property(job => job.Kind).HasMaxLength(50).IsRequired();
        builder.Property(job => job.RequestedByEmail).HasMaxLength(320).IsRequired();
        builder.Property(job => job.FileKey).HasMaxLength(500);
        builder.Property(job => job.FileName).HasMaxLength(255);
        builder.Property(job => job.Error).HasMaxLength(2000);

        builder.Property(job => job.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(job => job.Filters).HasColumnType("jsonb").IsRequired();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(job => job.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // The sweep that removes expired files walks this.
        builder.HasIndex(job => job.ExpiresAt).HasDatabaseName("ix_export_jobs_expires");
        builder.HasIndex(job => job.TenantId).HasDatabaseName("ix_export_jobs_tenant");
    }
}
