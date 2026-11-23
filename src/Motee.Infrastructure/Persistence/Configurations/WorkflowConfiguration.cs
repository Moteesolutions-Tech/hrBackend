using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Workflows;

namespace Motee.Infrastructure.Persistence.Configurations;

internal static class WorkflowJson
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

internal sealed class WorkflowConfiguration : IEntityTypeConfiguration<Workflow>
{
    public void Configure(EntityTypeBuilder<Workflow> builder)
    {
        builder.ToTable("workflows");

        builder.HasKey(workflow => workflow.Id);

        builder.Property(workflow => workflow.Title).HasMaxLength(200).IsRequired();
        builder.Property(workflow => workflow.Description).HasMaxLength(2000);
        builder.Property(workflow => workflow.EmploymentType).HasMaxLength(40);

        foreach (string property in (string[])
        [
            nameof(Workflow.TriggerMode),
            nameof(Workflow.ScopeKind),
            nameof(Workflow.WorkflowKind),
            nameof(Workflow.Status),
        ])
        {
            builder.Property(property).HasConversion<string>().HasMaxLength(20).IsRequired();
        }

        foreach (string property in (string[])
        [
            nameof(Workflow.ScheduleKind),
            nameof(Workflow.TriggerEvent),
            nameof(Workflow.OffsetUnit),
        ])
        {
            builder.Property(property).HasConversion<string>().HasMaxLength(30);
        }

        // "Which workflows fire on onboarding" is a question the trigger asks
        // in SQL, which is why the schedule is columns rather than jsonb.
        builder
            .HasIndex(workflow => new
            {
                workflow.TenantId,
                workflow.TriggerEvent,
                workflow.Status,
            })
            .HasDatabaseName("ix_workflows_tenant_id_trigger_event_status");

        // One seeded flow per title per company, so re-seeding cannot write a
        // second copy.
        builder
            .HasIndex(workflow => new { workflow.TenantId, workflow.Title })
            .IsUnique()
            .HasFilter("workflow_kind = 'System'")
            .HasDatabaseName("ix_workflows_one_seeded_per_title");
    }
}

internal sealed class WorkflowTaskConfiguration : IEntityTypeConfiguration<WorkflowTask>
{
    public void Configure(EntityTypeBuilder<WorkflowTask> builder)
    {
        builder.ToTable("workflow_tasks");

        builder.HasKey(task => task.Id);

        builder.Property(task => task.Title).HasMaxLength(200).IsRequired();
        builder.Property(task => task.Description).HasMaxLength(2000);
        builder.Property(task => task.ParallelGroup).HasMaxLength(60);

        builder.Property(task => task.AssigneeKind)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(task => task.Priority)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(task => task.Condition).HasConversion<string>().HasMaxLength(30);

        WorkflowJson.List(builder, task => task.DependsOn);

        builder
            .HasOne<Workflow>()
            .WithMany()
            .HasForeignKey(task => task.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(task => new { task.WorkflowId, task.Sequence })
            .HasDatabaseName("ix_workflow_tasks_workflow_id_sequence");
    }
}

internal sealed class WorkflowRunConfiguration : IEntityTypeConfiguration<WorkflowRun>
{
    public void Configure(EntityTypeBuilder<WorkflowRun> builder)
    {
        builder.ToTable("workflow_runs");

        builder.HasKey(run => run.Id);

        builder.Property(run => run.WorkflowTitle).HasMaxLength(200).IsRequired();
        builder.Property(run => run.SubjectName).HasMaxLength(200).IsRequired();

        builder.Property(run => run.SubjectKind)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(run => run.Trigger).HasConversion<string>().HasMaxLength(30);

        // Read with the row and checked in memory against a handful of facts.
        WorkflowJson.List(builder, run => run.Context);

        builder
            .HasOne<Workflow>()
            .WithMany()
            .HasForeignKey(run => run.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);

        // One run per workflow per subject: a trigger that fires twice for the
        // same hire finds the run already there rather than duplicating
        // somebody's work.
        builder
            .HasIndex(run => new { run.WorkflowId, run.SubjectKind, run.SubjectId })
            .IsUnique()
            .HasDatabaseName("ix_workflow_runs_one_per_subject");

        builder
            .HasIndex(run => new { run.TenantId, run.StartedAt })
            .HasDatabaseName("ix_workflow_runs_tenant_id_started_at");
    }
}

internal sealed class RunTaskConfiguration : IEntityTypeConfiguration<RunTask>
{
    public void Configure(EntityTypeBuilder<RunTask> builder)
    {
        builder.ToTable("run_tasks");

        builder.HasKey(task => task.Id);

        builder.Property(task => task.Title).HasMaxLength(200).IsRequired();
        builder.Property(task => task.Description).HasMaxLength(2000);
        builder.Property(task => task.AssigneeName).HasMaxLength(200).IsRequired();
        builder.Property(task => task.ReviewerName).HasMaxLength(200);
        builder.Property(task => task.ReassignedFrom).HasMaxLength(200);
        builder.Property(task => task.ParallelGroup).HasMaxLength(60);
        builder.Property(task => task.Note).HasMaxLength(2000);

        builder.Property(task => task.AssigneeKind)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(task => task.Priority)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // The stored lifecycle only. Blocked and overdue are read off the other
        // rows and today's date, so neither has a column to go stale in.
        builder.Property(task => task.State)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(task => task.Condition).HasConversion<string>().HasMaxLength(30);

        WorkflowJson.List(builder, task => task.DependsOn);

        builder
            .HasOne<WorkflowRun>()
            .WithMany()
            .HasForeignKey(task => task.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(task => new { task.RunId, task.Sequence })
            .HasDatabaseName("ix_run_tasks_run_id_sequence");

        // The inbox reads this: everything one person owes, across every run.
        builder
            .HasIndex(task => new { task.AssigneeEmployeeId, task.State })
            .HasDatabaseName("ix_run_tasks_assignee_employee_id_state");

        builder
            .HasIndex(task => new { task.ReviewerEmployeeId, task.State })
            .HasDatabaseName("ix_run_tasks_reviewer_employee_id_state");
    }
}
