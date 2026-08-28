using Microsoft.EntityFrameworkCore;
using Motee.Application.Approvals;
using Motee.Application.Common;
using Motee.Domain.Approvals;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Approvals;

internal sealed class ApprovalTemplateService(
    MoteeDbContext dbContext,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IApprovalTemplateService
{
    public async Task<IReadOnlyList<ApprovalTemplateDto>> ListAsync(
        string? documentType = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<ApprovalTemplate> matching = dbContext.ApprovalTemplates.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(documentType))
        {
            matching = matching.Where(template => template.DocumentType == documentType);
        }

        List<ApprovalTemplate> templates = await matching
            .OrderBy(template => template.DocumentType)
            .ThenBy(template => template.Name)
            .ToListAsync(cancellationToken);

        List<ApprovalTemplateDto> results = [];

        foreach (ApprovalTemplate template in templates)
        {
            results.Add(await ProjectAsync(template, cancellationToken));
        }

        return results;
    }

    public async Task<ApprovalTemplateDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ApprovalTemplate? template = await dbContext.ApprovalTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return template is null ? null : await ProjectAsync(template, cancellationToken);
    }

    public async Task<ApprovalTemplateResult> CreateAsync(
        ApprovalTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        // A chain with no steps approves nothing and waits for nobody. Refusing here is
        // better than letting a module start it and get an instance that is complete the
        // instant it exists.
        if (request.Steps.Count == 0)
        {
            return ApprovalTemplateResult.Failed(ApprovalTemplateOutcome.NoSteps);
        }

        if (await NameTakenAsync(request.DocumentType, request.Name, null, cancellationToken))
        {
            return ApprovalTemplateResult.Failed(ApprovalTemplateOutcome.DuplicateName);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        ApprovalTemplate template = new()
        {
            Id = Guid.NewGuid(),
            DocumentType = request.DocumentType.Trim(),
            Name = request.Name.Trim(),
            Description = Trimmed(request.Description),
            IsDefault = request.IsDefault,
            IsSystem = false,
            IsActive = request.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedByUserId = CurrentUser(),
        };

        if (request.IsDefault)
        {
            await ClearDefaultAsync(template.DocumentType, null, cancellationToken);
        }

        dbContext.ApprovalTemplates.Add(template);

        WriteSteps(template.Id, request.Steps);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ApprovalTemplateResult.Ok((await GetAsync(template.Id, cancellationToken))!);
    }

    public async Task<ApprovalTemplateResult> UpdateAsync(
        Guid id,
        ApprovalTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        ApprovalTemplate? template = await dbContext.ApprovalTemplates
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (template is null)
        {
            return ApprovalTemplateResult.Failed(ApprovalTemplateOutcome.NotFound);
        }

        // Shipped templates are read-only. A company that edited one would leave every
        // older instance referring to a chain that no longer matches what it says —
        // copying it and changing the copy keeps both truthful.
        if (template.IsSystem)
        {
            return ApprovalTemplateResult.Failed(ApprovalTemplateOutcome.SystemTemplate);
        }

        if (request.Steps.Count == 0)
        {
            return ApprovalTemplateResult.Failed(ApprovalTemplateOutcome.NoSteps);
        }

        if (await NameTakenAsync(request.DocumentType, request.Name, id, cancellationToken))
        {
            return ApprovalTemplateResult.Failed(ApprovalTemplateOutcome.DuplicateName);
        }

        if (request.IsDefault && !template.IsDefault)
        {
            await ClearDefaultAsync(request.DocumentType.Trim(), id, cancellationToken);
        }

        template.DocumentType = request.DocumentType.Trim();
        template.Name = request.Name.Trim();
        template.Description = Trimmed(request.Description);
        template.IsDefault = request.IsDefault;
        template.IsActive = request.IsActive;
        template.UpdatedAt = timeProvider.GetUtcNow();
        template.UpdatedByUserId = CurrentUser();

        // Steps are replaced wholesale rather than diffed. Instances snapshot their own
        // copies at submission, so nothing in flight is reading these rows — which is
        // what makes replacing them safe, and is the reason for the snapshot.
        dbContext.ApprovalTemplateSteps.RemoveRange(
            dbContext.ApprovalTemplateSteps.Where(step => step.TemplateId == id));

        WriteSteps(id, request.Steps);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ApprovalTemplateResult.Ok((await GetAsync(id, cancellationToken))!);
    }

    public async Task<ApprovalTemplateOutcome> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ApprovalTemplate? template = await dbContext.ApprovalTemplates
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (template is null)
        {
            return ApprovalTemplateOutcome.NotFound;
        }

        if (template.IsSystem)
        {
            return ApprovalTemplateOutcome.SystemTemplate;
        }

        // Any instance at all, not only live ones. A completed approval is the record of
        // decisions people made, and it points at the template it ran from — deleting
        // that leaves the history referring to nothing.
        bool used = await dbContext.ApprovalInstances
            .AnyAsync(instance => instance.TemplateId == id, cancellationToken);

        if (used)
        {
            return ApprovalTemplateOutcome.InUse;
        }

        dbContext.ApprovalTemplateSteps.RemoveRange(
            dbContext.ApprovalTemplateSteps.Where(step => step.TemplateId == id));

        dbContext.ApprovalTemplates.Remove(template);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ApprovalTemplateOutcome.Succeeded;
    }

    // Sequences are rewritten contiguously from zero rather than taken from the client.
    // Gaps and duplicates make "the next step" ambiguous, and the unique index would
    // reject the duplicate anyway — with an error nobody could act on.
    private void WriteSteps(Guid templateId, IReadOnlyList<ApprovalTemplateStepRequest> steps)
    {
        for (int index = 0; index < steps.Count; index++)
        {
            dbContext.ApprovalTemplateSteps.Add(new ApprovalTemplateStep
            {
                Id = Guid.NewGuid(),
                TemplateId = templateId,
                Sequence = index,
                Label = steps[index].Label.Trim(),
                Approver = steps[index].Approver,
                Required = steps[index].Required,
            });
        }
    }

    // One default per document type, and the unique index enforces it — so the old one
    // has to be cleared in the same save, or the insert fails on a constraint the caller
    // cannot interpret.
    private async Task ClearDefaultAsync(
        string documentType,
        Guid? excludingId,
        CancellationToken cancellationToken)
    {
        List<ApprovalTemplate> existing = await dbContext.ApprovalTemplates
            .Where(template => template.DocumentType == documentType
                && template.IsDefault
                && template.Id != excludingId)
            .ToListAsync(cancellationToken);

        foreach (ApprovalTemplate template in existing)
        {
            template.IsDefault = false;
        }
    }

    private async Task<bool> NameTakenAsync(
        string documentType,
        string name,
        Guid? excludingId,
        CancellationToken cancellationToken)
    {
        string pattern = EscapeLike(name.Trim());

        return await dbContext.ApprovalTemplates
            .Where(template => template.DocumentType == documentType.Trim()
                && template.Id != excludingId)
            .AnyAsync(
                template => EF.Functions.ILike(template.Name, pattern, @"\"),
                cancellationToken);
    }

    private async Task<ApprovalTemplateDto> ProjectAsync(
        ApprovalTemplate template,
        CancellationToken cancellationToken)
    {
        List<ApprovalTemplateStepDto> steps = await dbContext.ApprovalTemplateSteps
            .AsNoTracking()
            .Where(step => step.TemplateId == template.Id)
            .OrderBy(step => step.Sequence)
            .Select(step => new ApprovalTemplateStepDto
            {
                Id = step.Id,
                Sequence = step.Sequence,
                Label = step.Label,
                Approver = step.Approver,
                Required = step.Required,
            })
            .ToListAsync(cancellationToken);

        // Live runs only. The screen uses this to warn before an edit, and a template
        // used once last year is not a reason to warn anybody.
        int running = await dbContext.ApprovalInstances
            .CountAsync(
                instance => instance.TemplateId == template.Id
                    && instance.Status == ApprovalStatus.InProgress,
                cancellationToken);

        return new ApprovalTemplateDto
        {
            Id = template.Id,
            DocumentType = template.DocumentType,
            Name = template.Name,
            Description = template.Description,
            IsDefault = template.IsDefault,
            IsSystem = template.IsSystem,
            IsActive = template.IsActive,
            Steps = steps,
            RunningInstances = running,
            UpdatedAt = template.UpdatedAt,
        };
    }

    private Guid? CurrentUser() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string value) => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);
}
