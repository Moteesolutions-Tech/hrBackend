using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Relations;

namespace Motee.Infrastructure.Persistence.Configurations;

internal static class RelationsJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static ValueComparer<List<T>> Comparer<T>() =>
        new(
            (left, right) => JsonSerializer.Serialize(left, Options)
                == JsonSerializer.Serialize(right, Options),
            value => JsonSerializer.Serialize(value, Options).GetHashCode(),
            value => JsonSerializer.Deserialize<List<T>>(
                JsonSerializer.Serialize(value, Options), Options)!);

    internal static void List<TEntity, TItem>(
        EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, List<TItem>>> property)
        where TEntity : class =>
        builder.Property(property)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Options),
                json => JsonSerializer.Deserialize<List<TItem>>(json, Options)!,
                Comparer<TItem>())
            .IsRequired();
}

internal sealed class ErCaseConfiguration : IEntityTypeConfiguration<ErCase>
{
    public void Configure(EntityTypeBuilder<ErCase> builder)
    {
        builder.ToTable("er_cases");

        builder.HasKey(row => row.Id);

        builder.Property(row => row.CaseNumber).HasMaxLength(40).IsRequired();
        builder.Property(row => row.Description).HasMaxLength(8000).IsRequired();
        builder.Property(row => row.AppealGrounds).HasMaxLength(4000);
        builder.Property(row => row.RetentionPeriod).HasMaxLength(60);

        foreach (string property in (string[])
        [
            nameof(ErCase.ComplaintType),
            nameof(ErCase.Stage),
            nameof(ErCase.Priority),
            nameof(ErCase.ConfidentialityLevel),
        ])
        {
            builder.Property(property).HasConversion<string>().HasMaxLength(40).IsRequired();
        }

        builder.Property(row => row.Outcome).HasConversion<string>().HasMaxLength(40);

        RelationsJson.List(builder, row => row.Witnesses);
        RelationsJson.List(builder, row => row.HearingPanelEmployeeIds);

        // One case per number per company. The number is minted from the
        // highest so far, and this is what makes two simultaneous callers safe
        // rather than merely unlikely.
        builder
            .HasIndex(row => new { row.TenantId, row.CaseNumber })
            .IsUnique()
            .HasDatabaseName("ix_er_cases_tenant_id_case_number");

        builder
            .HasIndex(row => new { row.TenantId, row.Stage })
            .HasDatabaseName("ix_er_cases_tenant_id_stage");

        // The employee-facing screen reads this: cases somebody raised or is
        // the subject of.
        builder
            .HasIndex(row => new { row.SubjectEmployeeId, row.Stage })
            .HasDatabaseName("ix_er_cases_subject_employee_id_stage");

        builder
            .HasIndex(row => row.RaisedByEmployeeId)
            .HasDatabaseName("ix_er_cases_raised_by_employee_id");
    }
}

internal sealed class CaseNoteConfiguration : IEntityTypeConfiguration<CaseNote>
{
    public void Configure(EntityTypeBuilder<CaseNote> builder)
    {
        builder.ToTable("case_notes");

        builder.HasKey(note => note.Id);

        builder.Property(note => note.Content).HasMaxLength(8000).IsRequired();

        // Three values rather than a boolean: a single "internal" flag could
        // not express the middle ground where the case team sees a note and the
        // person it is about does not.
        builder.Property(note => note.Visibility)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder
            .HasOne<ErCase>()
            .WithMany()
            .HasForeignKey(note => note.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(note => new { note.CaseId, note.CreatedAt })
            .HasDatabaseName("ix_case_notes_case_id_created_at");
    }
}

internal sealed class CaseMeetingConfiguration : IEntityTypeConfiguration<CaseMeeting>
{
    public void Configure(EntityTypeBuilder<CaseMeeting> builder)
    {
        builder.ToTable("case_meetings");

        builder.HasKey(meeting => meeting.Id);

        builder.Property(meeting => meeting.Notes).HasMaxLength(8000);
        builder.Property(meeting => meeting.MeetingOutcome).HasMaxLength(2000);

        builder.Property(meeting => meeting.MeetingKind)
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        RelationsJson.List(builder, meeting => meeting.Attendees);

        builder
            .HasOne<ErCase>()
            .WithMany()
            .HasForeignKey(meeting => meeting.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(meeting => new { meeting.CaseId, meeting.HeldOn })
            .HasDatabaseName("ix_case_meetings_case_id_held_on");
    }
}

internal sealed class CaseEvidenceConfiguration : IEntityTypeConfiguration<CaseEvidence>
{
    public void Configure(EntityTypeBuilder<CaseEvidence> builder)
    {
        builder.ToTable("case_evidence");

        builder.HasKey(evidence => evidence.Id);

        builder.Property(evidence => evidence.Name).HasMaxLength(300).IsRequired();
        builder.Property(evidence => evidence.Description).HasMaxLength(2000);

        builder
            .HasOne<ErCase>()
            .WithMany()
            .HasForeignKey(evidence => evidence.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(evidence => evidence.CaseId)
            .HasDatabaseName("ix_case_evidence_case_id");
    }
}
