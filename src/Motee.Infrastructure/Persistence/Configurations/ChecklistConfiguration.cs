using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Checklists;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class ChecklistItemConfiguration : IEntityTypeConfiguration<ChecklistItem>
{
    public void Configure(EntityTypeBuilder<ChecklistItem> builder)
    {
        builder.ToTable("checklist_items");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Title).HasMaxLength(100).IsRequired();
        builder.Property(item => item.Description).HasMaxLength(500);
        builder.Property(item => item.Category).HasMaxLength(60);

        builder.Property(item => item.ResponsibleParty)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder
            .HasIndex(item => new { item.TenantId, item.Sequence })
            .HasDatabaseName("ix_checklist_items_tenant_id_sequence");
    }
}

internal sealed class ChecklistCompletionConfiguration
    : IEntityTypeConfiguration<ChecklistCompletion>
{
    public void Configure(EntityTypeBuilder<ChecklistCompletion> builder)
    {
        builder.ToTable("checklist_completions");

        builder.HasKey(completion => completion.Id);

        builder.Property(completion => completion.Note).HasMaxLength(500);

        // Declared so EF orders the inserts, and so removing a task takes its
        // completions with it rather than leaving rows pointing at nothing.
        builder
            .HasOne<ChecklistItem>()
            .WithMany()
            .HasForeignKey(completion => completion.ChecklistItemId)
            .OnDelete(DeleteBehavior.Cascade);

        // One completion per task per starter. Ticking twice updates the row
        // it already has: two rows would put the progress count ahead of the
        // number of tasks, and the database is what makes two tabs safe.
        builder
            .HasIndex(completion => new { completion.EmployeeId, completion.ChecklistItemId })
            .IsUnique()
            .HasDatabaseName("ix_checklist_completions_one_per_task");
    }
}
