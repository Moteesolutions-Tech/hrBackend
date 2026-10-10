using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Onboarding;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class OnboardingRecordConfiguration : IEntityTypeConfiguration<OnboardingRecord>
{
    public void Configure(EntityTypeBuilder<OnboardingRecord> builder)
    {
        builder.ToTable("onboarding_records");

        builder.HasKey(record => record.Id);

        // Text, like every other enum here: readable in psql, and reordering the enum
        // cannot silently move everyone to a different stage.
        builder.Property(record => record.Stage)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(record => record.Submission)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        // CASCADE, unlike the offboarding record beside it, and the difference matters.
        //
        // An offboarding record is the history of somebody leaving — often the only thing
        // that explains a gap — so it survives them. An onboarding record exists for
        // every employee from the moment they are created, and describes a process rather
        // than an outcome. RESTRICT here would mean no employee could ever be deleted,
        // which is not this table's decision to make.
        builder.HasOne<Domain.Employees.Employee>()
            .WithMany()
            .HasForeignKey(record => record.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        // One onboarding per person, enforced by the database rather than by EnsureAsync
        // alone: two requests a moment apart both find nothing and both insert, and it is
        // the second insert that has to fail.
        builder.HasIndex(record => record.EmployeeId)
            .HasDatabaseName("ix_onboarding_employee")
            .IsUnique();

        // The pipeline screen's default view: this tenant, filtered by stage.
        builder.HasIndex(record => new { record.TenantId, record.Stage })
            .HasDatabaseName("ix_onboarding_tenant_stage");

        // No foreign key to the approval instance on purpose. The link is one-way — a
        // module knows its approval, the engine knows nothing of its modules — and a
        // constraint here would be the engine's schema depending on onboarding's.
        builder.HasIndex(record => record.ApprovalInstanceId)
            .HasDatabaseName("ix_onboarding_approval");

        // Owned: four columns describing this record, never queried on their own.
        builder.OwnsOne(record => record.PrivacyConsent, consent =>
        {
            consent.Property(value => value.AcceptedAt).HasColumnName("consent_accepted_at");
            consent.Property(value => value.NoticeVersion)
                .HasColumnName("consent_notice_version").HasMaxLength(40);
            consent.Property(value => value.IpAddress)
                .HasColumnName("consent_ip_address").HasMaxLength(64);
        });

        builder.OwnsOne(record => record.Declaration, declaration =>
        {
            declaration.Property(value => value.SignedName)
                .HasColumnName("declaration_signed_name").HasMaxLength(200);
            declaration.Property(value => value.SignedAt).HasColumnName("declaration_signed_at");
            declaration.Property(value => value.IpAddress)
                .HasColumnName("declaration_ip_address").HasMaxLength(64);
        });

        // jsonb and never interpreted. This is a half-filled form, and validating it
        // would reject drafts for being incomplete — which is what a draft is.
        builder.Property(record => record.DraftJson).HasColumnType("jsonb");
    }
}

internal sealed class JoinerDocumentConfiguration : IEntityTypeConfiguration<JoinerDocument>
{
    public void Configure(EntityTypeBuilder<JoinerDocument> builder)
    {
        builder.ToTable("joiner_documents");

        builder.HasKey(document => document.Id);

        builder.Property(document => document.Kind)
            .HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.HasOne<OnboardingRecord>()
            .WithMany()
            .HasForeignKey(document => document.OnboardingRecordId)
            .OnDelete(DeleteBehavior.Cascade);

        // RESTRICT to the file: a right-to-work check that pointed at a deleted blob
        // would be a check nobody can evidence.
        builder.HasOne<Domain.Files.StoredFile>()
            .WithMany()
            .HasForeignKey(document => document.FileId)
            .OnDelete(DeleteBehavior.Restrict);

        // One current document per slot. Replacing a rejected upload replaces the row, so
        // nobody has to work out which of two passports is the live one.
        builder.HasIndex(document => new { document.OnboardingRecordId, document.Kind })
            .HasDatabaseName("ix_joiner_documents_slot")
            .IsUnique();
    }
}

internal sealed class GuarantorConfiguration : IEntityTypeConfiguration<Guarantor>
{
    public void Configure(EntityTypeBuilder<Guarantor> builder)
    {
        builder.ToTable("guarantors");

        builder.HasKey(guarantor => guarantor.Id);

        builder.Property(guarantor => guarantor.Name).HasMaxLength(200).IsRequired();
        builder.Property(guarantor => guarantor.Relationship).HasMaxLength(100).IsRequired();
        builder.Property(guarantor => guarantor.Occupation).HasMaxLength(150);
        builder.Property(guarantor => guarantor.Address).HasMaxLength(500);
        builder.Property(guarantor => guarantor.Phone).HasMaxLength(40);

        builder.HasOne<OnboardingRecord>()
            .WithMany()
            .HasForeignKey(guarantor => guarantor.OnboardingRecordId)
            .OnDelete(DeleteBehavior.Cascade);

        // Guarantor 1 and 2, once each. Their ID documents are filed against the same
        // numbers, and two rows claiming position 1 makes the match ambiguous.
        builder.HasIndex(guarantor => new { guarantor.OnboardingRecordId, guarantor.Position })
            .HasDatabaseName("ix_guarantors_position")
            .IsUnique();
    }
}

internal sealed class StarterTaxRecordConfiguration : IEntityTypeConfiguration<StarterTaxRecord>
{
    public void Configure(EntityTypeBuilder<StarterTaxRecord> builder)
    {
        builder.ToTable("starter_tax_records");

        builder.HasKey(record => record.Id);

        builder.Property(record => record.Source)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        // jsonb for the three value objects. Each is read whole with the record and never
        // queried by its parts — nothing asks "which joiners had a week 1 indicator".
        //
        // It also means the P45's box layout can change without a migration, which it
        // will: HMRC renumbers boxes between form revisions.
        AsJsonb(builder.Property(record => record.P45));
        AsJsonb(builder.Property(record => record.StarterChecklist));
        AsJsonb(builder.Property(record => record.Derived));

        builder.HasOne<OnboardingRecord>()
            .WithMany()
            .HasForeignKey(record => record.OnboardingRecordId)
            .OnDelete(DeleteBehavior.Cascade);

        // One per onboarding record.
        builder.HasIndex(record => record.OnboardingRecordId)
            .HasDatabaseName("ix_starter_tax_record")
            .IsUnique();

        // The retention sweep: which records are now past HMRC's period.
        builder.HasIndex(record => record.RetainUntil)
            .HasDatabaseName("ix_starter_tax_retain_until");
    }

    private static void AsJsonb<T>(PropertyBuilder<T> property) =>
        property
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<T>(json, Json)!,
                new ValueComparer<T>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json)
                        .GetHashCode(StringComparison.Ordinal),
                    value => JsonSerializer.Deserialize<T>(
                        JsonSerializer.Serialize(value, Json), Json)!));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
