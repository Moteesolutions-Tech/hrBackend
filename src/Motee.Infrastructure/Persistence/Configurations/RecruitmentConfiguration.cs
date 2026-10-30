using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Motee.Domain.Recruitment;

namespace Motee.Infrastructure.Persistence.Configurations;

// jsonb in all four of these tables is a short list read with its row and never
// queried across — panels, skills, stage sets, scorecard lines. None of them is
// filterable in SQL, which has caught this codebase out before, so everything
// that reads them does so in memory.
internal static class RecruitmentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static void AsJsonb<TEntity, TItem>(
        PropertyBuilder<List<TItem>> property)
        where TEntity : class =>
        property
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, Options),
                json => JsonSerializer.Deserialize<List<TItem>>(json, Options)!,
                new ValueComparer<List<TItem>>(
                    (left, right) => JsonSerializer.Serialize(left, Options)
                        == JsonSerializer.Serialize(right, Options),
                    value => JsonSerializer.Serialize(value, Options).GetHashCode(),
                    value => JsonSerializer.Deserialize<List<TItem>>(
                        JsonSerializer.Serialize(value, Options), Options)!))
            .IsRequired();
}

internal sealed class JobRequisitionConfiguration : IEntityTypeConfiguration<JobRequisition>
{
    public void Configure(EntityTypeBuilder<JobRequisition> builder)
    {
        builder.ToTable("job_requisitions");

        builder.HasKey(requisition => requisition.Id);

        builder.Property(requisition => requisition.Reference).HasMaxLength(20);
        builder.Property(requisition => requisition.PositionTitle).HasMaxLength(200).IsRequired();
        builder.Property(requisition => requisition.Location).HasMaxLength(200);
        builder.Property(requisition => requisition.Currency).HasMaxLength(3);

        builder.Property(requisition => requisition.EmploymentType)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(requisition => requisition.Status)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(requisition => requisition.HiringPriority)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        // Money, so numeric. A double cannot hold a salary band exactly and the
        // error compounds the moment anything is summed.
        builder.Property(requisition => requisition.SalaryMin).HasPrecision(18, 4);
        builder.Property(requisition => requisition.SalaryMax).HasPrecision(18, 4);

        RecruitmentJson.AsJsonb<JobRequisition, Guid>(
            builder.Property(requisition => requisition.InterviewPanel));

        RecruitmentJson.AsJsonb<JobRequisition, string>(
            builder.Property(requisition => requisition.RequiredSkills));

        // Stored as their names, not their ordinals: inserting a stage into the
        // enum later would silently repoint every stored list.
        builder.Property(requisition => requisition.Stages)
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(
                    value.Select(stage => stage.ToString()), RecruitmentJson.Options),
                json => JsonSerializer.Deserialize<List<string>>(json, RecruitmentJson.Options)!
                    .Select(Enum.Parse<CandidateStage>)
                    .ToList(),
                new ValueComparer<List<CandidateStage>>(
                    (left, right) => left!.SequenceEqual(right!),
                    value => value.Aggregate(0, (hash, stage) => HashCode.Combine(hash, stage)),
                    value => value.ToList()))
            .IsRequired();

        // A reference is quoted externally, so two vacancies sharing one is
        // worse than none. Partial, because drafts have none.
        builder.HasIndex(requisition => new { requisition.TenantId, requisition.Reference })
            .IsUnique()
            .HasFilter("reference IS NOT NULL")
            .HasDatabaseName("ix_job_requisitions_reference");

        builder.HasIndex(requisition => new { requisition.TenantId, requisition.Status });
    }
}

internal sealed class CandidateConfiguration : IEntityTypeConfiguration<Candidate>
{
    public void Configure(EntityTypeBuilder<Candidate> builder)
    {
        builder.ToTable("candidates");

        builder.HasKey(candidate => candidate.Id);

        builder.Property(candidate => candidate.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(candidate => candidate.LastName).HasMaxLength(100).IsRequired();

        // 320, matching employees rather than users: an applicant's own
        // address, not a login.
        builder.Property(candidate => candidate.Email).HasMaxLength(320).IsRequired();

        builder.Property(candidate => candidate.Phone).HasMaxLength(40);
        builder.Property(candidate => candidate.Source).HasMaxLength(60);
        builder.Property(candidate => candidate.LinkedIn).HasMaxLength(300);

        builder.Property(candidate => candidate.Stage)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(candidate => candidate.Status)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        RecruitmentJson.AsJsonb<Candidate, string>(
            builder.Property(candidate => candidate.Skills));

        builder.Ignore(candidate => candidate.FullName);

        builder.HasOne<JobRequisition>()
            .WithMany()
            .HasForeignKey(candidate => candidate.RequisitionId)
            .OnDelete(DeleteBehavior.Cascade);

        // One application per person per vacancy. Lower-cased, because
        // somebody reapplying rarely types their address the same way twice.
        builder.HasIndex(candidate => new { candidate.RequisitionId, candidate.Email })
            .IsUnique()
            .HasDatabaseName("ix_candidates_one_application");

        builder.HasIndex(candidate => new { candidate.RequisitionId, candidate.Stage });
        builder.HasIndex(candidate => new { candidate.TenantId, candidate.OwnerEmployeeId });
    }
}

internal sealed class ScorecardConfiguration : IEntityTypeConfiguration<Scorecard>
{
    public void Configure(EntityTypeBuilder<Scorecard> builder)
    {
        builder.ToTable("candidate_scorecards");

        builder.HasKey(scorecard => scorecard.Id);

        builder.Property(scorecard => scorecard.ByName).HasMaxLength(200);

        builder.Property(scorecard => scorecard.Recommendation)
            .HasConversion<string>().HasMaxLength(20);

        RecruitmentJson.AsJsonb<Scorecard, ScorecardCriterion>(
            builder.Property(scorecard => scorecard.Criteria));

        // Declared so EF inserts the candidate first. Without the relationship
        // the ordering is arbitrary, and a create that scores in the same unit
        // of work fails on the foreign key.
        builder.HasOne<Candidate>()
            .WithMany()
            .HasForeignKey(scorecard => scorecard.CandidateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(scorecard => scorecard.CandidateId);
    }
}

internal sealed class CandidateOfferConfiguration : IEntityTypeConfiguration<CandidateOffer>
{
    public void Configure(EntityTypeBuilder<CandidateOffer> builder)
    {
        builder.ToTable("candidate_offers");

        builder.HasKey(offer => offer.Id);

        builder.Property(offer => offer.Status)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(offer => offer.Currency).HasMaxLength(3);
        builder.Property(offer => offer.Salary).HasPrecision(18, 4);

        builder.HasOne<Candidate>()
            .WithMany()
            .HasForeignKey(offer => offer.CandidateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(offer => offer.CandidateId);
    }
}

internal sealed class InterviewConfiguration : IEntityTypeConfiguration<Interview>
{
    public void Configure(EntityTypeBuilder<Interview> builder)
    {
        builder.ToTable("interviews");

        builder.HasKey(interview => interview.Id);

        builder.Property(interview => interview.Round).HasMaxLength(120).IsRequired();
        builder.Property(interview => interview.Location).HasMaxLength(500);

        builder.Property(interview => interview.Mode)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(interview => interview.Status)
            .HasConversion<string>().HasMaxLength(20).IsRequired();

        RecruitmentJson.AsJsonb<Interview, Guid>(builder.Property(interview => interview.Panel));

        builder.HasOne<Candidate>()
            .WithMany()
            .HasForeignKey(interview => interview.CandidateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<JobRequisition>()
            .WithMany()
            .HasForeignKey(interview => interview.RequisitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(interview => interview.CandidateId);
        builder.HasIndex(interview => new { interview.TenantId, interview.ScheduledAt });
    }
}
