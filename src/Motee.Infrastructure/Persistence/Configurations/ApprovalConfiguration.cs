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

        // The queue query: what is waiting on me.
        builder.HasIndex(step => new { step.TenantId, step.ResolvedUserId, step.Status })
            .HasDatabaseName("ix_approval_step_instances_queue");
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
