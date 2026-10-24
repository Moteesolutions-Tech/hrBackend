using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Performance;

namespace Motee.Infrastructure.Persistence.Configurations;

internal sealed class PerformanceGoalConfiguration : IEntityTypeConfiguration<PerformanceGoal>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<PerformanceGoal> builder)
    {
        builder.ToTable("performance_goals");

        builder.HasKey(goal => goal.Id);

        builder.Property(goal => goal.Title).HasMaxLength(200).IsRequired();
        builder.Property(goal => goal.Description).HasMaxLength(2000);
        builder.Property(goal => goal.GoalType).HasMaxLength(50);

        builder.Property(goal => goal.Category)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(goal => goal.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // jsonb. Check-ins are read with the goal and never queried across — "every
        // update written in March" is not a question anybody asks, and a child table
        // to answer it costs a join on every goal on every screen.
        builder.Property(goal => goal.Updates)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Json),
                json => JsonSerializer.Deserialize<List<GoalUpdate>>(json, Json)!,
                new ValueComparer<List<GoalUpdate>>(
                    (left, right) => JsonSerializer.Serialize(left, Json)
                        == JsonSerializer.Serialize(right, Json),
                    value => JsonSerializer.Serialize(value, Json).GetHashCode(),
                    value => JsonSerializer.Deserialize<List<GoalUpdate>>(
                        JsonSerializer.Serialize(value, Json), Json)!))
            .IsRequired();

        // Somebody's own goals, which is every read on the employee screen.
        builder.HasIndex(goal => new { goal.TenantId, goal.EmployeeId });

        // The overdue filter: past its date and unfinished.
        builder.HasIndex(goal => new { goal.TenantId, goal.DueDate });
    }
}
