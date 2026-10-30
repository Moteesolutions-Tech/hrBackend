using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Documents;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class DocumentFolderConfiguration : IEntityTypeConfiguration<DocumentFolder>
{
    public void Configure(EntityTypeBuilder<DocumentFolder> builder)
    {
        builder.ToTable("document_folders");

        builder.HasKey(folder => folder.Id);

        builder.Property(folder => folder.Name).HasMaxLength(150).IsRequired();

        builder.Property(folder => folder.Type)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Scoped to the parent, not the tenant: "Policies" under Engineering and
        // "Policies" under Sales are ordinary filing, two at the same level are not.
        //
        // Two indexes because Postgres treats nulls as distinct in a unique index,
        // so the top level — where ParentId is null — would not be covered by one
        // index alone and could hold any number of folders called "Policies".
        builder.HasIndex(folder => new { folder.TenantId, folder.ParentId, folder.Name })
            .IsUnique()
            .HasFilter("parent_id IS NOT NULL");

        builder.HasIndex(folder => new { folder.TenantId, folder.Name })
            .IsUnique()
            .HasFilter("parent_id IS NULL")
            .HasDatabaseName("ix_document_folders_unique_root_name");
    }
}

internal sealed class HrDocumentConfiguration : IEntityTypeConfiguration<HrDocument>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<HrDocument> builder)
    {
        builder.ToTable("hr_documents");

        builder.HasKey(document => document.Id);

        builder.Property(document => document.Name).HasMaxLength(250).IsRequired();
        builder.Property(document => document.Description).HasMaxLength(2000);

        builder.Property(document => document.Category)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(document => document.Assignment)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Ids rather than names, so a department that gets renamed does not quietly
        // stop being assigned.
        builder.Property(document => document.AssignedDepartmentIds)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<List<Guid>>(json, Json)!,
                new ValueComparer<List<Guid>>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json).GetHashCode(),
                    value => JsonSerializer.Deserialize<List<Guid>>(
                        JsonSerializer.Serialize(value, Json), Json)!))
            .IsRequired();

        builder.HasIndex(document => new { document.TenantId, document.FolderId });
    }
}

internal sealed class DocumentVersionConfiguration : IEntityTypeConfiguration<DocumentVersion>
{
    public void Configure(EntityTypeBuilder<DocumentVersion> builder)
    {
        builder.ToTable("document_versions");

        builder.HasKey(version => version.Id);

        builder.Property(version => version.Notes).HasMaxLength(1000);

        // Declared without a navigation property, purely so EF knows the order to
        // write them in. Without it the version was inserted before the document it
        // belongs to and the foreign key rejected it — a failure that only appears
        // when both are saved in one call, which is every create.
        builder.HasOne<HrDocument>()
            .WithMany()
            .HasForeignKey(version => version.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // One number per document. Two rows claiming version 3 would make "the
        // current one" a coin toss, and acknowledgements point at a version.
        builder.HasIndex(version => new { version.DocumentId, version.Version })
            .IsUnique();
    }
}

internal sealed class DocumentShareConfiguration : IEntityTypeConfiguration<DocumentShare>
{
    public void Configure(EntityTypeBuilder<DocumentShare> builder)
    {
        builder.ToTable("document_shares");

        builder.HasKey(share => share.Id);

        builder.Property(share => share.Permission)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.HasOne<HrDocument>()
            .WithMany()
            .HasForeignKey(share => share.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // One share per person per document. A second would be two answers about
        // what they may do with it.
        builder.HasIndex(share => new { share.DocumentId, share.EmployeeId })
            .IsUnique();

        // Everything shared with one person: the employee screen's read.
        builder.HasIndex(share => new { share.TenantId, share.EmployeeId });
    }
}

internal sealed class DocumentAcknowledgementConfiguration
    : IEntityTypeConfiguration<DocumentAcknowledgement>
{
    public void Configure(EntityTypeBuilder<DocumentAcknowledgement> builder)
    {
        builder.ToTable("document_acknowledgements");

        builder.HasKey(ack => ack.Id);

        builder.HasOne<DocumentVersion>()
            .WithMany()
            .HasForeignKey(ack => ack.VersionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Per version, not per document. Agreeing twice to the same version would
        // move the date and make a late acknowledgement look prompt; agreeing to a
        // new version is a different row and the whole point.
        builder.HasIndex(ack => new { ack.VersionId, ack.EmployeeId })
            .IsUnique();

        builder.HasIndex(ack => new { ack.TenantId, ack.DocumentId });
    }
}
