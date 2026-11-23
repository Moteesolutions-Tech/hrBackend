using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Learning;

namespace Motee.Infrastructure.Persistence.Configurations;

internal static class LearningJson
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

internal sealed class CourseConfiguration : IEntityTypeConfiguration<Course>
{
    public void Configure(EntityTypeBuilder<Course> builder)
    {
        builder.ToTable("courses");

        builder.HasKey(course => course.Id);

        builder.Property(course => course.Title).HasMaxLength(300).IsRequired();
        builder.Property(course => course.Description).HasMaxLength(4000);
        builder.Property(course => course.Instructor).HasMaxLength(200);
        builder.Property(course => course.Provider).HasMaxLength(200);
        builder.Property(course => course.CourseUrl).HasMaxLength(2000);
        builder.Property(course => course.VideoUrl).HasMaxLength(2000);

        builder.Property(course => course.DurationHours).HasPrecision(6, 2);

        foreach (string property in (string[])
        [
            nameof(Course.Category),
            nameof(Course.Status),
            nameof(Course.DeliveryMode),
        ])
        {
            builder.Property(property).HasConversion<string>().HasMaxLength(30).IsRequired();
        }

        LearningJson.List(builder, course => course.Tags);

        builder
            .HasIndex(course => new { course.TenantId, course.Status })
            .HasDatabaseName("ix_courses_tenant_id_status");

        // The compliance figures read this: every compulsory course against
        // every current employee.
        builder
            .HasIndex(course => new { course.TenantId, course.IsMandatory })
            .HasDatabaseName("ix_courses_tenant_id_is_mandatory");
    }
}

internal sealed class CourseQuestionConfiguration : IEntityTypeConfiguration<CourseQuestion>
{
    public void Configure(EntityTypeBuilder<CourseQuestion> builder)
    {
        builder.ToTable("course_questions");

        builder.HasKey(question => question.Id);

        builder.Property(question => question.Prompt).HasMaxLength(2000).IsRequired();

        builder.Property(question => question.QuestionKind)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        LearningJson.List(builder, question => question.Options);

        builder
            .HasOne<Course>()
            .WithMany()
            .HasForeignKey(question => question.CourseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(question => new { question.CourseId, question.Sequence })
            .HasDatabaseName("ix_course_questions_course_id_sequence");
    }
}

internal sealed class CourseEnrollmentConfiguration
    : IEntityTypeConfiguration<CourseEnrollment>
{
    public void Configure(EntityTypeBuilder<CourseEnrollment> builder)
    {
        builder.ToTable("course_enrollments");

        builder.HasKey(enrollment => enrollment.Id);

        builder.Property(enrollment => enrollment.Status)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder
            .HasOne<Course>()
            .WithMany()
            .HasForeignKey(enrollment => enrollment.CourseId)
            .OnDelete(DeleteBehavior.Cascade);

        // One enrolment per person per course: assigning a course twice moves
        // nothing rather than producing a second record of the same training.
        builder
            .HasIndex(enrollment => new { enrollment.CourseId, enrollment.EmployeeId })
            .IsUnique()
            .HasDatabaseName("ix_course_enrollments_one_per_person");

        // The learner's own screen, and the overdue figures.
        builder
            .HasIndex(enrollment => new { enrollment.EmployeeId, enrollment.Status })
            .HasDatabaseName("ix_course_enrollments_employee_id_status");
    }
}

internal sealed class QuizAttemptConfiguration : IEntityTypeConfiguration<QuizAttempt>
{
    public void Configure(EntityTypeBuilder<QuizAttempt> builder)
    {
        builder.ToTable("quiz_attempts");

        builder.HasKey(attempt => attempt.Id);

        builder.Property(attempt => attempt.Score).HasPrecision(5, 1);

        builder
            .HasOne<CourseEnrollment>()
            .WithMany()
            .HasForeignKey(attempt => attempt.EnrollmentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(attempt => new { attempt.EnrollmentId, attempt.AttemptedAt })
            .HasDatabaseName("ix_quiz_attempts_enrollment_id_attempted_at");
    }
}

internal sealed class CertificationConfiguration : IEntityTypeConfiguration<Certification>
{
    public void Configure(EntityTypeBuilder<Certification> builder)
    {
        builder.ToTable("certifications");

        builder.HasKey(certification => certification.Id);

        builder.Property(certification => certification.Title).HasMaxLength(300).IsRequired();
        builder.Property(certification => certification.IssuingBody).HasMaxLength(200);
        builder.Property(certification => certification.Category).HasMaxLength(60);
        builder.Property(certification => certification.CredentialId).HasMaxLength(120);

        builder.Property(certification => certification.CertificationKind)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        // A completion certificate follows its course, but withdrawing the
        // course does not unhappen the training — so the link is cleared
        // rather than the record deleted.
        builder
            .HasOne<Course>()
            .WithMany()
            .HasForeignKey(certification => certification.CourseId)
            .OnDelete(DeleteBehavior.SetNull);

        // One completion certificate per person per course.
        builder
            .HasIndex(certification =>
                new { certification.EmployeeId, certification.CourseId })
            .IsUnique()
            .HasFilter("course_id IS NOT NULL")
            .HasDatabaseName("ix_certifications_one_per_course");

        builder
            .HasIndex(certification =>
                new { certification.TenantId, certification.ExpiresOn })
            .HasDatabaseName("ix_certifications_tenant_id_expires_on");
    }
}
