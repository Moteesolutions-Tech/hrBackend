using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Approvals;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class ApprovalTemplateConfiguration : IEntityTypeConfiguration<ApprovalTemplate>
{
    public void Configure(EntityTypeBuilder<ApprovalTemplate> builder)
    {
        builder.ToTable("approval_templates");
        builder.HasKey(template => template.Id);

        // Owned rather than a table of its own: three columns describing this template,
        // with no meaning apart from it and never queried on their own.
        builder.OwnsOne(template => template.Attachments, attachments =>
        {
            attachments.Property(rules => rules.Allowed)
                .HasColumnName("attachments_allowed")
                .HasDefaultValue(false);

            attachments.Property(rules => rules.Required)
                .HasColumnName("attachments_required")
                .HasDefaultValue(false);

            attachments.Property(rules => rules.Note)
                .HasColumnName("attachment_note")
                .HasMaxLength(500);
        });

        builder.Navigation(template => template.Attachments).IsRequired();

        builder.Property(template => template.DocumentType).HasMaxLength(60).IsRequired();
        builder.Property(template => template.Name).HasMaxLength(150).IsRequired();
        builder.Property(template => template.Description).HasMaxLength(500);

        builder.HasIndex(template => new { template.TenantId, template.DocumentType, template.Name })
            .IsUnique()
            .HasDatabaseName("ix_approval_templates_tenant_type_name");

        // One default per document type. A second would make "start the default chain"
        // ambiguous, and the module starting it has no way to choose.
        builder.HasIndex(template => new { template.TenantId, template.DocumentType })
            .IsUnique()
            .HasFilter("is_default")
            .HasDatabaseName("ix_approval_templates_one_default");
    }
}

internal sealed class ApprovalTemplateStepConfiguration
    : IEntityTypeConfiguration<ApprovalTemplateStep>
{
    public void Configure(EntityTypeBuilder<ApprovalTemplateStep> builder)
    {
        builder.ToTable("approval_template_steps");
        builder.HasKey(step => step.Id);

        builder.Property(step => step.Label).HasMaxLength(200).IsRequired();

        builder.Property(step => step.Approver)
            .HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.HasOne<ApprovalTemplate>()
            .WithMany()
            .HasForeignKey(step => step.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(step => new { step.TemplateId, step.Sequence })
            .IsUnique()
            .HasDatabaseName("ix_approval_template_steps_sequence");
    }
}

internal sealed class ApprovalInstanceConfiguration : IEntityTypeConfiguration<ApprovalInstance>
{
    public void Configure(EntityTypeBuilder<ApprovalInstance> builder)
    {
        builder.ToTable("approval_instances");
        builder.HasKey(instance => instance.Id);

        builder.Property(instance => instance.DocumentType).HasMaxLength(60).IsRequired();
        builder.Property(instance => instance.SubjectType).HasMaxLength(60).IsRequired();

        builder.Property(instance => instance.Status)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        // RESTRICT, deliberately. A template with approvals against it cannot be deleted
        // — those runs are the record of decisions people actually made, and cascading
        // them away with a tidy-up would erase it.
        builder.HasOne<ApprovalTemplate>()
            .WithMany()
            .HasForeignKey(instance => instance.TemplateId)
            .OnDelete(DeleteBehavior.Restrict);

        // "Everything running against this onboarding record" — the query every
        // consuming module makes.
        builder.HasIndex(instance => new { instance.TenantId, instance.SubjectType, instance.SubjectId })
            .HasDatabaseName("ix_approval_instances_subject");

        builder.HasIndex(instance => new { instance.TenantId, instance.Status })
            .HasDatabaseName("ix_approval_instances_status");
    }
}

internal sealed class ApprovalStepInstanceConfiguration
    : IEntityTypeConfiguration<ApprovalStepInstance>
{
    public void Configure(EntityTypeBuilder<ApprovalStepInstance> builder)
    {
        builder.ToTable("approval_step_instances");
        builder.HasKey(step => step.Id);

        builder.Property(step => step.Label).HasMaxLength(200).IsRequired();
        builder.Property(step => step.Note).HasMaxLength(1000);
        builder.Property(step => step.SkippedReason).HasMaxLength(300);

        builder.Property(step => step.Approver)
            .HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.Property(step => step.Status)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasOne<ApprovalInstance>()
            .WithMany()
            .HasForeignKey(step => step.InstanceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(step => new { step.InstanceId, step.Sequence })
            .IsUnique()
            .HasDatabaseName("ix_approval_step_instances_sequence");

        // The approval queue asks "which pending steps name a role I hold". Filtered,
        // because only role steps set this and they are the minority — the positional
        // ones would otherwise pad the index with nulls.
        //
        // No foreign key to access_levels on purpose: a step recording that HR Admin
        // approved something in March is the record of a decision, and it has to survive
        // the level being reorganised. The resolver treats a level it cannot find as
        // nobody, so a dangling id fails closed.
        builder.HasIndex(step => step.ResolvedRoleId)
            .HasDatabaseName("ix_approval_steps_role")
            .HasFilter("resolved_role_id IS NOT NULL");

        // The queue query: what is waiting on me.
        builder.HasIndex(step => new { step.TenantId, step.ResolvedUserId, step.Status })
            .HasDatabaseName("ix_approval_step_instances_queue");
    }
}

internal sealed class ApprovalAttachmentConfiguration : IEntityTypeConfiguration<ApprovalAttachment>
{
    public void Configure(EntityTypeBuilder<ApprovalAttachment> builder)
    {
        builder.ToTable("approval_attachments");

        builder.HasKey(attachment => attachment.Id);

        // CASCADE from the approval: evidence has no meaning apart from the run it was
        // attached to. The StoredFile row is left alone — the files module owns its own
        // lifecycle, and deleting the blob is its decision, not this table's.
        builder.HasOne<ApprovalInstance>()
            .WithMany()
            .HasForeignKey(attachment => attachment.InstanceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Domain.Files.StoredFile>()
            .WithMany()
            .HasForeignKey(attachment => attachment.FileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(attachment => attachment.InstanceId)
            .HasDatabaseName("ix_approval_attachments_instance");

        // The same file must not be hung on one approval twice. A double-click on upload
        // is otherwise two identical rows an approver has to work out are the same thing.
        builder.HasIndex(attachment => new { attachment.InstanceId, attachment.FileId })
            .IsUnique()
            .HasDatabaseName("ix_approval_attachments_unique");
    }
}

internal sealed class ApprovalEventConfiguration : IEntityTypeConfiguration<ApprovalEvent>
{
    public void Configure(EntityTypeBuilder<ApprovalEvent> builder)
    {
        builder.ToTable("approval_events");
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Type).HasMaxLength(20).IsRequired();
        builder.Property(entry => entry.ActorName).HasMaxLength(320);
        builder.Property(entry => entry.Note).HasMaxLength(1000);

        builder.HasOne<ApprovalInstance>()
            .WithMany()
            .HasForeignKey(entry => entry.InstanceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entry => new { entry.InstanceId, entry.At })
            .HasDatabaseName("ix_approval_events_instance");
    }
}
