using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Work;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> builder)
    {
        builder.ToTable("task_items");

        builder.HasKey(task => task.Id);

        builder.Property(task => task.Title).HasMaxLength(200).IsRequired();
        builder.Property(task => task.Notes).HasMaxLength(2000);
        builder.Property(task => task.Category).HasMaxLength(60);
        builder.Property(task => task.Link).HasMaxLength(500);

        builder.Property(task => task.Priority)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        // Every read is somebody's own list, in their own order.
        builder
            .HasIndex(task => new { task.TenantId, task.AssigneeEmployeeId, task.Sequence })
            .HasDatabaseName("ix_task_items_assignee_sequence");

        // And the manager's view of what they gave out.
        builder
            .HasIndex(task => new { task.TenantId, task.CreatedByEmployeeId })
            .HasDatabaseName("ix_task_items_tenant_id_created_by_employee_id");
    }
}
