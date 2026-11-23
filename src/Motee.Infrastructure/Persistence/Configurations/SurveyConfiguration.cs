using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Surveys;

namespace Motee.Infrastructure.Persistence.Configurations;

internal static class SurveyJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static ValueComparer<List<T>> Comparer<T>() =>
        new(
            (left, right) => JsonSerializer.Serialize(left, Options)
                == JsonSerializer.Serialize(right, Options),
            value => JsonSerializer.Serialize(value, Options).GetHashCode(),
            value => JsonSerializer.Deserialize<List<T>>(
                JsonSerializer.Serialize(value, Options), Options)!);
}

internal sealed class SurveyConfiguration : IEntityTypeConfiguration<Survey>
{
    public void Configure(EntityTypeBuilder<Survey> builder)
    {
        builder.ToTable("surveys");

        builder.HasKey(survey => survey.Id);

        builder.Property(survey => survey.Title).HasMaxLength(200).IsRequired();
        builder.Property(survey => survey.Description).HasMaxLength(2000).IsRequired();

        foreach (string property in (string[])
        [
            nameof(Survey.SurveyType),
            nameof(Survey.Status),
            nameof(Survey.Audience),
        ])
        {
            builder.Property(property).HasConversion<string>().HasMaxLength(30).IsRequired();
        }

        builder.Property(survey => survey.PulseFrequency)
            .HasConversion<string>()
            .HasMaxLength(20);

        // jsonb: read with the row, and the audience check runs in memory
        // against a handful of ids.
        builder.Property(survey => survey.TargetDepartmentIds)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, SurveyJson.Options),
                json => JsonSerializer.Deserialize<List<Guid>>(json, SurveyJson.Options)!,
                SurveyJson.Comparer<Guid>())
            .IsRequired();

        builder
            .HasIndex(survey => new { survey.TenantId, survey.Status })
            .HasDatabaseName("ix_surveys_tenant_id_status");
    }
}

internal sealed class SurveyQuestionConfiguration : IEntityTypeConfiguration<SurveyQuestion>
{
    public void Configure(EntityTypeBuilder<SurveyQuestion> builder)
    {
        builder.ToTable("survey_questions");

        builder.HasKey(question => question.Id);

        builder.Property(question => question.Text).HasMaxLength(500).IsRequired();

        builder.Property(question => question.QuestionType)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(question => question.Options)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, SurveyJson.Options),
                json => JsonSerializer.Deserialize<List<string>>(json, SurveyJson.Options)!,
                SurveyJson.Comparer<string>())
            .IsRequired();

        // Declared so EF orders the inserts, and so deleting a survey takes
        // its questions with it.
        builder
            .HasOne<Survey>()
            .WithMany()
            .HasForeignKey(question => question.SurveyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasIndex(question => new { question.SurveyId, question.Sequence })
            .HasDatabaseName("ix_survey_questions_survey_sequence");
    }
}

internal sealed class SurveyResponseConfiguration : IEntityTypeConfiguration<SurveyResponse>
{
    public void Configure(EntityTypeBuilder<SurveyResponse> builder)
    {
        builder.ToTable("survey_responses");

        builder.HasKey(response => response.Id);

        builder
            .HasOne<Survey>()
            .WithMany()
            .HasForeignKey(response => response.SurveyId)
            .OnDelete(DeleteBehavior.Cascade);

        // One reply per person per survey. This is the reason the respondent
        // is recorded even on an anonymous survey: without it the same person
        // could answer repeatedly and move every aggregate.
        builder
            .HasIndex(response => new { response.SurveyId, response.RespondentEmployeeId })
            .IsUnique()
            .HasDatabaseName("ix_survey_responses_one_per_person");
    }
}

internal sealed class SurveyAnswerConfiguration : IEntityTypeConfiguration<SurveyAnswer>
{
    public void Configure(EntityTypeBuilder<SurveyAnswer> builder)
    {
        builder.ToTable("survey_answers");

        builder.HasKey(answer => answer.Id);

        builder.Property(answer => answer.Values)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, SurveyJson.Options),
                json => JsonSerializer.Deserialize<List<string>>(json, SurveyJson.Options)!,
                SurveyJson.Comparer<string>())
            .IsRequired();

        builder
            .HasOne<SurveyResponse>()
            .WithMany()
            .HasForeignKey(answer => answer.ResponseId)
            .OnDelete(DeleteBehavior.Cascade);

        // One answer row per question per response.
        builder
            .HasIndex(answer => new { answer.ResponseId, answer.QuestionId })
            .IsUnique()
            .HasDatabaseName("ix_survey_answers_one_per_question");
    }
}
