using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Documents;
using Motee.Domain.Employees;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class DocumentFolderConfiguration : IEntityTypeConfiguration<DocumentFolder>
{
    public void Configure(EntityTypeBuilder<DocumentFolder> builder)
    {
        builder.ToTable("document_folders");

        builder.HasKey(folder => folder.Id);

        builder.Property(folder => folder.Name).HasMaxLength(150).IsRequired();

        // Presentation, so a short free string rather than an enum — the palette
        // belongs to the client, and pinning it here would mean a release every time
        // somebody wanted a different green.
        builder.Property(folder => folder.Colour).HasMaxLength(20);

        // Cascade: a person's drawers go when the person does. The documents inside
        // are a separate question — they cascade from the document's own owner — and
        // deleting a drawer with anything in it is refused before it gets here.
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(folder => folder.OwnerEmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        // Somebody's own drawers, which is every read on the self-service side.
        builder.HasIndex(folder => new { folder.TenantId, folder.OwnerEmployeeId })
            .HasDatabaseName("ix_document_folders_owner");

        // The tree, declared so EF knows the dependency.
        //
        // The foreign key has always existed in the database; the model never said so,
        // which was invisible while every folder was created on its own. Creating a
        // parent and a child in one save then inserts them in whatever order EF
        // guesses, and half the time the child goes first and the database refuses it.
        //
        // Restrict rather than cascade: deleting a folder with anything under it is
        // refused in the service, and a cascade here would quietly take a subtree with
        // it if that check were ever bypassed.
        builder.HasOne<DocumentFolder>()
            .WithMany()
            .HasForeignKey(folder => folder.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

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

        builder.Property(document => document.ReviewStatus)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(document => document.ReviewNote).HasMaxLength(1000);

        // Set null rather than cascade: a document accepted onto the record stays on it
        // after the person who uploaded it leaves. Losing the filing because the
        // employee row went is the opposite of what a record is for.
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(document => document.UploadedByEmployeeId)
            .OnDelete(DeleteBehavior.SetNull);

        // The review queue: awaiting, oldest first.
        builder.HasIndex(document => new { document.TenantId, document.ReviewStatus })
            .HasDatabaseName("ix_hr_documents_review");

        // Somebody's own uploads, which the self-service list reads on every open.
        builder.HasIndex(document => new { document.TenantId, document.UploadedByEmployeeId })
            .HasDatabaseName("ix_hr_documents_uploader");
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
