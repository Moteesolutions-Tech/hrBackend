using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Motee.Application.Tenancy;
using Motee.Domain.Announcements;
using Motee.Domain.Auth;
using Motee.Domain.Common;
using Motee.Domain.Approvals;
using Motee.Domain.Assets;
using Motee.Domain.Attendance;
using Motee.Domain.Audit;
using Motee.Domain.Authorization;
using Motee.Domain.Contracts;
using Motee.Domain.Documents;
using Motee.Domain.Employees;
using Motee.Domain.Expenses;
using Motee.Domain.Exports;
using Motee.Domain.Files;
using Motee.Domain.Leave;
using Motee.Domain.Offboarding;
using Motee.Domain.Onboarding;
using Motee.Domain.Notifications;
using Motee.Domain.Organisation;
using Motee.Domain.Performance;
using Motee.Domain.Recruitment;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Identity;

namespace Motee.Infrastructure.Persistence;

public class MoteeDbContext(DbContextOptions<MoteeDbContext> options, ICurrentTenant currentTenant)
    : IdentityUserContext<ApplicationUser, Guid>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<OtpChallengeRecord> OtpChallenges => Set<OtpChallengeRecord>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Employee> Employees => Set<Employee>();

    public DbSet<EmployeeInvitation> EmployeeInvitations => Set<EmployeeInvitation>();

    public DbSet<WorkSchedule> WorkSchedules => Set<WorkSchedule>();

    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();

    public DbSet<Timesheet> Timesheets => Set<Timesheet>();

    public DbSet<AttendanceCorrection> AttendanceCorrections => Set<AttendanceCorrection>();

    public DbSet<ExpenseClaim> ExpenseClaims => Set<ExpenseClaim>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<JobRequisition> JobRequisitions => Set<JobRequisition>();

    public DbSet<Candidate> Candidates => Set<Candidate>();

    public DbSet<Scorecard> Scorecards => Set<Scorecard>();

    public DbSet<CandidateOffer> CandidateOffers => Set<CandidateOffer>();

    public DbSet<Interview> Interviews => Set<Interview>();

    public DbSet<Announcement> Announcements => Set<Announcement>();

    public DbSet<AnnouncementAcknowledgement> AnnouncementAcknowledgements =>
        Set<AnnouncementAcknowledgement>();

    public DbSet<AnnouncementView> AnnouncementViews => Set<AnnouncementView>();

    public DbSet<Contract> Contracts => Set<Contract>();

    public DbSet<ContractSignature> ContractSignatures => Set<ContractSignature>();

    public DbSet<ContractNote> ContractNotes => Set<ContractNote>();

    public DbSet<DocumentFolder> DocumentFolders => Set<DocumentFolder>();

    public DbSet<HrDocument> HrDocuments => Set<HrDocument>();

    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();

    public DbSet<DocumentShare> DocumentShares => Set<DocumentShare>();

    public DbSet<DocumentAcknowledgement> DocumentAcknowledgements =>
        Set<DocumentAcknowledgement>();

    public DbSet<PerformanceGoal> PerformanceGoals => Set<PerformanceGoal>();

    public DbSet<PerformanceReview> PerformanceReviews => Set<PerformanceReview>();

    public DbSet<PerformanceFeedback> PerformanceFeedback => Set<PerformanceFeedback>();

    public DbSet<FeedbackRequest> FeedbackRequests => Set<FeedbackRequest>();

    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<AssetAssignment> AssetAssignments => Set<AssetAssignment>();

    public DbSet<AccessLevel> AccessLevels => Set<AccessLevel>();

    // What each user holds. Many-to-many: someone can be both a Line Manager and a
    // Recruiter, and the two sets combine.
    public DbSet<UserAccessLevel> UserAccessLevels => Set<UserAccessLevel>();

    public DbSet<BusinessUnit> BusinessUnits => Set<BusinessUnit>();

    public DbSet<EmployeeBankDetails> EmployeeBankDetails => Set<EmployeeBankDetails>();

    public DbSet<EmployeeIdentityDocuments> EmployeeIdentityDocuments =>
        Set<EmployeeIdentityDocuments>();

    // Read through the medical permission, never alongside the employee record.
    public DbSet<EmployeeMedical> EmployeeMedical => Set<EmployeeMedical>();

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<ExportJob> ExportJobs => Set<ExportJob>();

    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<ApprovalTemplate> ApprovalTemplates => Set<ApprovalTemplate>();

    public DbSet<ApprovalTemplateStep> ApprovalTemplateSteps => Set<ApprovalTemplateStep>();

    public DbSet<ApprovalInstance> ApprovalInstances => Set<ApprovalInstance>();

    public DbSet<ApprovalStepInstance> ApprovalStepInstances => Set<ApprovalStepInstance>();

    public DbSet<ApprovalEvent> ApprovalEvents => Set<ApprovalEvent>();

    public DbSet<ApprovalAttachment> ApprovalAttachments => Set<ApprovalAttachment>();

    public DbSet<ApprovalDelegation> ApprovalDelegations => Set<ApprovalDelegation>();

    public DbSet<LeaveType> LeaveTypes => Set<LeaveType>();

    public DbSet<LeavePolicy> LeavePolicies => Set<LeavePolicy>();

    public DbSet<PublicHoliday> PublicHolidays => Set<PublicHoliday>();

    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();

    public DbSet<LeaveAdjustment> LeaveAdjustments => Set<LeaveAdjustment>();

    public DbSet<LeaveCarryOver> LeaveCarryOvers => Set<LeaveCarryOver>();

    public DbSet<LeaveBlackout> LeaveBlackouts => Set<LeaveBlackout>();

    public DbSet<OnboardingRecord> OnboardingRecords => Set<OnboardingRecord>();

    public DbSet<JoinerDocument> JoinerDocuments => Set<JoinerDocument>();

    public DbSet<Guarantor> Guarantors => Set<Guarantor>();

    public DbSet<StarterTaxRecord> StarterTaxRecords => Set<StarterTaxRecord>();

    public DbSet<OffboardingRecord> OffboardingRecords => Set<OffboardingRecord>();

    public DbSet<OffboardingClearanceItem> OffboardingClearance => Set<OffboardingClearanceItem>();

    // Read by the query filter through a closure, so the filter reflects the tenant
    // of the request currently using this context.
    private Guid? CurrentTenantId => currentTenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MoteeDbContext).Assembly);

        ApplyTenantFilter(modelBuilder);
    }

    // Applied by reflection rather than entity by entity: a new ITenantScoped type is
    // filtered the moment it exists, so isolation cannot be lost by forgetting a line.
    private void ApplyTenantFilter(ModelBuilder modelBuilder)
    {
        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            ParameterExpression parameter = Expression.Parameter(entityType.ClrType, "entity");

            // entity => (Guid?)entity.TenantId == this.CurrentTenantId
            //
            // With no tenant resolved this compares a Guid to null and matches
            // nothing, which is the safe direction to fail.
            BinaryExpression body = Expression.Equal(
                Expression.Convert(
                    Expression.Property(parameter, nameof(ITenantScoped.TenantId)),
                    typeof(Guid?)),
                Expression.Property(Expression.Constant(this), nameof(CurrentTenantId)));

            modelBuilder.Entity(entityType.ClrType)
                .HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }

    public override int SaveChanges()
    {
        StampTenant();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTenant();
        return base.SaveChangesAsync(cancellationToken);
    }

    // Inserting without a TenantId would create a row the filter can never return.
    // Stamping here means callers cannot forget; an explicit TenantId is left alone,
    // so registration can create rows for a tenant nobody is signed into yet.
    private void StampTenant()
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return;
        }

        foreach (EntityEntry<ITenantScoped> entry in ChangeTracker.Entries<ITenantScoped>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = tenantId;
            }
        }
    }
}
