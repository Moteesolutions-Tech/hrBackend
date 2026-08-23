using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Files;
using Motee.Domain.Tenants;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> builder)
    {
        builder.ToTable("stored_files");
        builder.HasKey(file => file.Id);

        builder.Property(file => file.FileName).HasMaxLength(255).IsRequired();
        builder.Property(file => file.ContentType).HasMaxLength(150).IsRequired();
        builder.Property(file => file.StorageKey).HasMaxLength(500).IsRequired();

        builder.Property(file => file.Purpose)
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(file => file.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // "Every document for this employee" is the common read.
        builder.HasIndex(file => new { file.TenantId, file.Purpose, file.OwnerId })
            .HasDatabaseName("ix_stored_files_tenant_purpose_owner");

        builder.HasIndex(file => file.StorageKey)
            .HasDatabaseName("ix_stored_files_key")
            .IsUnique();
    }
}
