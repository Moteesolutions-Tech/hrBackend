using Motee.Domain.Approvals;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Approvals;

// The approval chains a new tenant starts with.
//
// One, not ten. A company opening the workflows screen to find templates for expense
// claims and job requisitions — neither of which the product does yet — learns that the
// screen lies, and stops reading it. A category gets a default when the module that
// starts it exists.
//
// Seeded as tenant-owned rather than system: like the access levels, this is a starting
// point companies edit freely. Reserving IsSystem for chains we genuinely must protect
// keeps "copy it and edit the copy" a meaningful instruction rather than a routine tax.
internal sealed class ApprovalTemplateSeeder(MoteeDbContext dbContext, TimeProvider timeProvider)
{
    public void Seed(Guid tenantId)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        Guid templateId = Guid.NewGuid();

        // Line manager, then department head. The order most companies actually use, and
        // both rules resolve without any configuration — a chain naming specific people
        // would be wrong for every tenant on the day it was written.
        dbContext.ApprovalTemplates.Add(new ApprovalTemplate
        {
            Id = templateId,

            // Set explicitly: registration runs before any tenant is current, so the
            // context has nothing to stamp these from.
            TenantId = tenantId,
            DocumentType = ApprovalDocumentTypes.Onboarding,
            Name = "Standard onboarding",
            Description = "Line manager approves, then the department head.",
            IsDefault = true,
            IsSystem = false,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        Add(templateId, tenantId, 0, "Line manager approval", ApproverResolver.LineManager, required: true);

        // Optional on purpose. A department with no head recorded is ordinary in a young
        // company, and a required step there would leave every onboarding stuck behind a
        // person who does not exist. Optional means it is skipped, with the reason
        // written on the step, and the chain completes.
        Add(templateId, tenantId, 1, "Department head approval", ApproverResolver.DepartmentHead, required: false);
    }

    private void Add(
        Guid templateId,
        Guid tenantId,
        int sequence,
        string label,
        ApproverResolver approver,
        bool required) =>
        dbContext.ApprovalTemplateSteps.Add(new ApprovalTemplateStep
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TemplateId = templateId,
            Sequence = sequence,
            Label = label,
            Approver = approver,
            Required = required,
        });
}
