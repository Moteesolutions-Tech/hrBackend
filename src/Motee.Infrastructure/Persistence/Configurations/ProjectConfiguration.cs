using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Projects;

namespace Motee.Infrastructure.Persistence.Configurations;

internal static class ProjectJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // The converter compares by serialising, so a list has to be replaced
    // rather than mutated for a change to be noticed.
    internal static ValueComparer<List<T>> Comparer<T>() =>
        new(
            (left, right) => JsonSerializer.Serialize(left, Options)
                == JsonSerializer.Serialize(right, Options),
            value => JsonSerializer.Serialize(value, Options).GetHashCode(),
            value => JsonSerializer.Deserialize<List<T>>(
                JsonSerializer.Serialize(value, Options), Options)!);

    internal static void Ids<TEntity>(
        EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, List<Guid>>> property)
        where TEntity : class =>
        builder.Property(property)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Options),
                json => JsonSerializer.Deserialize<List<Guid>>(json, Options)!,
                Comparer<Guid>())
            .IsRequired();
}

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects");

        builder.HasKey(project => project.Id);

        builder.Property(project => project.Code).HasMaxLength(40).IsRequired();
        builder.Property(project => project.Name).HasMaxLength(200).IsRequired();
        builder.Property(project => project.Description).HasMaxLength(4000);
        builder.Property(project => project.Client).HasMaxLength(200);
        builder.Property(project => project.CostCentreCode).HasMaxLength(40);

        builder.Property(project => project.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(project => project.Priority)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(project => project.Budget).HasPrecision(18, 2);

        // The code is how a project is referenced everywhere else, so it is
        // one project per code within a company.
        builder
            .HasIndex(project => new { project.TenantId, project.Code })
            .IsUnique()
            .HasDatabaseName("ix_projects_tenant_id_code");

        builder
            .HasIndex(project => new { project.TenantId, project.Status })
            .HasDatabaseName("ix_projects_tenant_id_status");
    }
}

internal sealed class ProjectTaskConfiguration : IEntityTypeConfiguration<ProjectTask>
{
    public void Configure(EntityTypeBuilder<ProjectTask> builder)
    {
        builder.ToTable("project_tasks");

        builder.HasKey(task => task.Id);

        builder.Property(task => task.Name).HasMaxLength(200).IsRequired();
        builder.Property(task => task.Description).HasMaxLength(4000);
        builder.Property(task => task.Phase).HasMaxLength(100);

        builder.Property(task => task.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(task => task.Priority).HasConversion<string>().HasMaxLength(20);

        builder.Property(task => task.EstimatedHours).HasPrecision(10, 2);

        ProjectJson.Ids(builder, task => task.DependsOn);

        builder
            .HasOne<Project>()
            .WithMany()
            .HasForeignKey(task => task.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(task => new { task.ProjectId, task.StartOn })
            .HasDatabaseName("ix_project_tasks_project_id_start_on");
    }
}

internal sealed class ProjectMilestoneConfiguration
    : IEntityTypeConfiguration<ProjectMilestone>
{
    public void Configure(EntityTypeBuilder<ProjectMilestone> builder)
    {
        builder.ToTable("project_milestones");

        builder.HasKey(milestone => milestone.Id);

        builder.Property(milestone => milestone.Name).HasMaxLength(200).IsRequired();
        builder.Property(milestone => milestone.Description).HasMaxLength(4000);

        builder.Property(milestone => milestone.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        ProjectJson.Ids(builder, milestone => milestone.TaskIds);
        ProjectJson.Ids(builder, milestone => milestone.RiskIds);

        builder
            .HasOne<Project>()
            .WithMany()
            .HasForeignKey(milestone => milestone.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(milestone => new { milestone.ProjectId, milestone.TargetOn })
            .HasDatabaseName("ix_project_milestones_project_id_target_on");
    }
}

internal sealed class ProjectAllocationConfiguration
    : IEntityTypeConfiguration<ProjectAllocation>
{
    public void Configure(EntityTypeBuilder<ProjectAllocation> builder)
    {
        builder.ToTable("project_allocations");

        builder.HasKey(allocation => allocation.Id);

        builder.Property(allocation => allocation.ProjectRole).HasMaxLength(120).IsRequired();

        builder.Property(allocation => allocation.AllocationPercent).HasPrecision(5, 2);
        builder.Property(allocation => allocation.HourlyRate).HasPrecision(18, 2);

        builder
            .HasOne<Project>()
            .WithMany()
            .HasForeignKey(allocation => allocation.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per person per project. Setting an allocation twice moves
        // the figure rather than booking somebody twice.
        builder
            .HasIndex(allocation => new { allocation.ProjectId, allocation.EmployeeId })
            .IsUnique()
            .HasDatabaseName("ix_project_allocations_one_per_person");
    }
}

internal sealed class ProjectTimeEntryConfiguration
    : IEntityTypeConfiguration<ProjectTimeEntry>
{
    public void Configure(EntityTypeBuilder<ProjectTimeEntry> builder)
    {
        builder.ToTable("project_time_entries");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Notes).HasMaxLength(1000);

        builder.Property(entry => entry.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(entry => entry.Hours).HasPrecision(6, 2);

        builder
            .HasOne<Project>()
            .WithMany()
            .HasForeignKey(entry => entry.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting a task does not delete the hours worked on it: the time was
        // still spent on the project, and dropping it would quietly reduce the
        // spend.
        builder
            .HasOne<ProjectTask>()
            .WithMany()
            .HasForeignKey(entry => entry.TaskId)
            .OnDelete(DeleteBehavior.SetNull);

        builder
            .HasIndex(entry => new { entry.ProjectId, entry.WorkedOn })
            .HasDatabaseName("ix_project_time_entries_project_id_worked_on");

        // The day-total check reads this on every log.
        builder
            .HasIndex(entry => new { entry.EmployeeId, entry.WorkedOn })
            .HasDatabaseName("ix_project_time_entries_employee_id_worked_on");
    }
}

internal sealed class ProjectRiskConfiguration : IEntityTypeConfiguration<ProjectRisk>
{
    public void Configure(EntityTypeBuilder<ProjectRisk> builder)
    {
        builder.ToTable("project_risks");

        builder.HasKey(risk => risk.Id);

        builder.Property(risk => risk.Title).HasMaxLength(300).IsRequired();
        builder.Property(risk => risk.Description).HasMaxLength(4000);

        foreach (string property in (string[])
        [
            nameof(ProjectRisk.RiskKind),
            nameof(ProjectRisk.Impact),
            nameof(ProjectRisk.Status),
        ])
        {
            builder.Property(property).HasConversion<string>().HasMaxLength(20).IsRequired();
        }

        ProjectJson.Ids(builder, risk => risk.RelatedTaskIds);
        ProjectJson.Ids(builder, risk => risk.RelatedMilestoneIds);

        builder
            .HasOne<Project>()
            .WithMany()
            .HasForeignKey(risk => risk.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(risk => new { risk.ProjectId, risk.Status })
            .HasDatabaseName("ix_project_risks_project_id_status");
    }
}

internal sealed class ProjectDocumentConfiguration : IEntityTypeConfiguration<ProjectDocument>
{
    public void Configure(EntityTypeBuilder<ProjectDocument> builder)
    {
        builder.ToTable("project_documents");

        builder.HasKey(document => document.Id);

        builder.Property(document => document.Name).HasMaxLength(300).IsRequired();
        builder.Property(document => document.Description).HasMaxLength(2000);

        builder.Property(document => document.Category)
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder
            .HasOne<Project>()
            .WithMany()
            .HasForeignKey(document => document.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(document => new { document.ProjectId, document.Category })
            .HasDatabaseName("ix_project_documents_project_id_category");
    }
}

internal sealed class GoLiveCompletionConfiguration : IEntityTypeConfiguration<GoLiveCompletion>
{
    public void Configure(EntityTypeBuilder<GoLiveCompletion> builder)
    {
        builder.ToTable("go_live_completions");

        builder.HasKey(completion => completion.Id);

        builder.Property(completion => completion.ItemKey).HasMaxLength(80).IsRequired();
        builder.Property(completion => completion.Notes).HasMaxLength(2000);

        builder
            .HasOne<Project>()
            .WithMany()
            .HasForeignKey(completion => completion.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // One tick per check per project.
        builder
            .HasIndex(completion => new { completion.ProjectId, completion.ItemKey })
            .IsUnique()
            .HasDatabaseName("ix_go_live_completions_one_per_check");
    }
}
