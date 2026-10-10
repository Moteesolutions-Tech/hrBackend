using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Onboarding;
using Motee.Application.Tenancy;
using Motee.Domain.Common;
using Motee.Domain.Files;
using Motee.Domain.Onboarding;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Onboarding;

internal sealed class JoinerPackService(
    MoteeDbContext dbContext,
    ICurrentTenant currentTenant,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IJoinerPackService
{
    public async Task<JoinerPackRequirementsDto> RequirementsAsync(
        CancellationToken cancellationToken = default)
    {
        CountryCode country = await CountryAsync(cancellationToken);

        return new JoinerPackRequirementsDto
        {
            CountryCode = country.Value,
            Documents = JoinerDocumentCatalogue.For(country),
            GuarantorsRequired = country == CountryCode.Nigeria ? 2 : 0,
            CollectsStarterTax = country == CountryCode.UnitedKingdom,
            PrivacyNoticeVersion = PrivacyNotice.CurrentVersion,
        };
    }

    public async Task<JoinerPackDto?> GetAsync(
        Guid onboardingRecordId,
        CancellationToken cancellationToken = default)
    {
        OnboardingRecord? record = await dbContext.OnboardingRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == onboardingRecordId, cancellationToken);

        return record is null ? null : await DescribeAsync(record, cancellationToken);
    }

    public async Task<JoinerPackResult> AcceptPrivacyNoticeAsync(
        Guid onboardingRecordId,
        CancellationToken cancellationToken = default)
    {
        OnboardingRecord? record = await FindAsync(onboardingRecordId, cancellationToken);

        if (record is null)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.NotFound);
        }

        // Re-accepting is a no-op rather than a new timestamp. The first acceptance is the
        // one that matters, and a joiner who reloads the page has not consented twice.
        record.PrivacyConsent ??= new PrivacyConsent
        {
            AcceptedAt = timeProvider.GetUtcNow(),
            NoticeVersion = PrivacyNotice.CurrentVersion,
            IpAddress = requestContext.IpAddress,
        };

        record.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return JoinerPackResult.Ok(await DescribeAsync(record, cancellationToken));
    }

    public async Task<JoinerPackResult> AttachDocumentAsync(
        Guid onboardingRecordId,
        JoinerDocumentKind kind,
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        OnboardingRecord? record = await FindAsync(onboardingRecordId, cancellationToken);

        if (record is null)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.NotFound);
        }

        if (record.PrivacyConsent is null)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.ConsentMissing);
        }

        CountryCode country = await CountryAsync(cancellationToken);

        // A slot offered to one country must not be accepted from the other. Without this
        // a UK joiner could file a guarantor ID nobody will ever look at.
        if (!JoinerDocumentCatalogue.Accepts(country, kind))
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.NotApplicable);
        }

        bool usable = await dbContext.StoredFiles.AnyAsync(
            file => file.Id == fileId && file.Purpose == FilePurpose.EmployeeDocument,
            cancellationToken);

        if (!usable)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.UnknownFile);
        }

        JoinerDocument? existing = await dbContext.JoinerDocuments.FirstOrDefaultAsync(
            document => document.OnboardingRecordId == onboardingRecordId && document.Kind == kind,
            cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        if (existing is not null)
        {
            // Replaced, not added alongside. The old file row is left to the files module
            // — deleting it here would break a link the audit trail may still point at.
            existing.FileId = fileId;
            existing.UploadedAt = now;
        }
        else
        {
            dbContext.JoinerDocuments.Add(new JoinerDocument
            {
                Id = Guid.NewGuid(),
                OnboardingRecordId = onboardingRecordId,
                Kind = kind,
                FileId = fileId,
                UploadedAt = now,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return JoinerPackResult.Ok(await DescribeAsync(record, cancellationToken));
    }

    public async Task<JoinerPackOutcome> RemoveDocumentAsync(
        Guid onboardingRecordId,
        JoinerDocumentKind kind,
        CancellationToken cancellationToken = default)
    {
        JoinerDocument? document = await dbContext.JoinerDocuments.FirstOrDefaultAsync(
            candidate => candidate.OnboardingRecordId == onboardingRecordId
                && candidate.Kind == kind,
            cancellationToken);

        if (document is null)
        {
            return JoinerPackOutcome.NotFound;
        }

        dbContext.JoinerDocuments.Remove(document);

        await dbContext.SaveChangesAsync(cancellationToken);

        return JoinerPackOutcome.Succeeded;
    }

    public async Task<JoinerPackResult> SaveGuarantorsAsync(
        Guid onboardingRecordId,
        IReadOnlyList<GuarantorRequest> guarantors,
        CancellationToken cancellationToken = default)
    {
        OnboardingRecord? record = await FindAsync(onboardingRecordId, cancellationToken);

        if (record is null)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.NotFound);
        }

        if (record.PrivacyConsent is null)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.ConsentMissing);
        }

        CountryCode country = await CountryAsync(cancellationToken);

        // UK tenants are not shown this step, so accepting guarantors from one would store
        // data nothing reads and nobody asked for.
        if (country != CountryCode.Nigeria)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.NotApplicable);
        }

        if (guarantors.Count != 2
            || guarantors.Select(g => g.Position).Order().SequenceEqual([1, 2]) is false)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.InvalidGuarantors);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Replaced wholesale. They are collected as a pair on one step, so a partial save
        // would leave a joiner able to submit with only the first.
        dbContext.Guarantors.RemoveRange(
            dbContext.Guarantors.Where(g => g.OnboardingRecordId == onboardingRecordId));

        foreach (GuarantorRequest request in guarantors)
        {
            dbContext.Guarantors.Add(new Guarantor
            {
                Id = Guid.NewGuid(),
                OnboardingRecordId = onboardingRecordId,
                Position = request.Position,
                Name = request.Name.Trim(),
                Relationship = request.Relationship.Trim(),
                Occupation = Trimmed(request.Occupation),
                Address = Trimmed(request.Address),
                Phone = Trimmed(request.Phone),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return JoinerPackResult.Ok(await DescribeAsync(record, cancellationToken));
    }

    public async Task<JoinerPackResult> SaveStarterTaxAsync(
        Guid onboardingRecordId,
        StarterTaxRequest request,
        CancellationToken cancellationToken = default)
    {
        OnboardingRecord? record = await FindAsync(onboardingRecordId, cancellationToken);

        if (record is null)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.NotFound);
        }

        if (record.PrivacyConsent is null)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.ConsentMissing);
        }

        if (await CountryAsync(cancellationToken) != CountryCode.UnitedKingdom)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.NotApplicable);
        }

        // A source that names a branch without supplying it would derive the emergency
        // code from a form the joiner thinks they filled in.
        if ((request.Source == StarterTaxSource.P45 && request.P45 is null)
            || (request.Source == StarterTaxSource.StarterChecklist
                && request.EmployeeStatement is null))
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.ContradictorySource);
        }

        DateOnly startDate = await dbContext.Employees
            .Where(employee => employee.Id == record.EmployeeId)
            .Select(employee => employee.StartDate)
            .FirstOrDefaultAsync(cancellationToken)
            ?? DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        StarterChecklistDetails? checklist = request.EmployeeStatement is null
            ? null
            : new StarterChecklistDetails
            {
                EmployeeStatement = request.EmployeeStatement,

                // Resolved, never taken from the caller. Somebody asked to pick A, B or C
                // directly will pick the wrong one, and the wrong one is a wrong payslip.
                StarterDeclaration = StarterTaxDerivation.Resolve(request.EmployeeStatement),
                StudentLoan = request.StudentLoan ?? new StudentLoanDetails(),
            };

        DateTimeOffset now = timeProvider.GetUtcNow();

        StarterTaxRecord? tax = await dbContext.StarterTaxRecords.FirstOrDefaultAsync(
            candidate => candidate.OnboardingRecordId == onboardingRecordId, cancellationToken);

        if (tax is null)
        {
            tax = new StarterTaxRecord
            {
                Id = Guid.NewGuid(),
                OnboardingRecordId = onboardingRecordId,
                EmployeeId = record.EmployeeId,
                CreatedAt = now,
            };

            dbContext.StarterTaxRecords.Add(tax);
        }

        tax.Source = request.Source;
        tax.EmploymentStartDate = startDate;
        tax.P45 = request.Source == StarterTaxSource.P45 ? request.P45 : null;
        tax.StarterChecklist = checklist;
        tax.RetainUntil = StarterTaxDerivation.RetainUntil(startDate);
        tax.UpdatedAt = now;

        // Stored, not computed on read. Payroll has to be able to say which code it used
        // and why at the time; re-deriving later against a changed allowance figure would
        // quietly rewrite history.
        tax.Derived = StarterTaxDerivation.Derive(
            request.Source, startDate, tax.P45, checklist);

        await dbContext.SaveChangesAsync(cancellationToken);

        return JoinerPackResult.Ok(await DescribeAsync(record, cancellationToken));
    }

    public async Task<JoinerPackOutcome> SaveDraftAsync(
        Guid onboardingRecordId,
        string draftJson,
        int? step,
        CancellationToken cancellationToken = default)
    {
        OnboardingRecord? record = await FindAsync(onboardingRecordId, cancellationToken);

        if (record is null)
        {
            return JoinerPackOutcome.NotFound;
        }

        record.DraftJson = draftJson;
        record.DraftStep = step;
        record.DraftSavedAt = timeProvider.GetUtcNow();

        // Moved off NotStarted so the pipeline shows them as started rather than as
        // somebody who has not opened the link. That is the whole value of the panel.
        if (record.Submission == OnboardingSubmission.NotStarted)
        {
            record.Submission = OnboardingSubmission.InProgress;
        }

        record.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return JoinerPackOutcome.Succeeded;
    }

    public async Task<JoinerPackResult> DeclareAsync(
        Guid onboardingRecordId,
        string signedName,
        CancellationToken cancellationToken = default)
    {
        OnboardingRecord? record = await FindAsync(onboardingRecordId, cancellationToken);

        if (record is null)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.NotFound);
        }

        if (record.PrivacyConsent is null)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.ConsentMissing);
        }

        IReadOnlyList<string> outstanding = await OutstandingAsync(record, cancellationToken);

        if (outstanding.Count > 0)
        {
            return JoinerPackResult.Failed(JoinerPackOutcome.Incomplete, outstanding);
        }

        record.Declaration = new JoinerDeclaration
        {
            SignedName = signedName.Trim(),
            SignedAt = timeProvider.GetUtcNow(),
            IpAddress = requestContext.IpAddress,
        };

        record.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return JoinerPackResult.Ok(await DescribeAsync(record, cancellationToken));
    }

    // Everything still required. Returned as labels so a refusal names what is missing —
    // "incomplete" on its own sends somebody hunting through eight steps.
    private async Task<IReadOnlyList<string>> OutstandingAsync(
        OnboardingRecord record,
        CancellationToken cancellationToken)
    {
        CountryCode country = await CountryAsync(cancellationToken);

        List<JoinerDocumentKind> supplied = await dbContext.JoinerDocuments
            .Where(document => document.OnboardingRecordId == record.Id)
            .Select(document => document.Kind)
            .ToListAsync(cancellationToken);

        List<string> outstanding =
        [
            .. JoinerDocumentCatalogue.Missing(country, supplied).Select(spec => spec.Label),
        ];

        if (country == CountryCode.Nigeria)
        {
            int guarantors = await dbContext.Guarantors
                .CountAsync(g => g.OnboardingRecordId == record.Id, cancellationToken);

            if (guarantors < 2)
            {
                outstanding.Add($"Guarantor details ({guarantors} of 2 provided)");
            }
        }

        // A UK joiner who declared nothing is allowed to submit — they get the emergency
        // code, which is recoverable. What is not allowed is never being asked, so the
        // record has to exist even when its source is None.
        if (country == CountryCode.UnitedKingdom)
        {
            bool declared = await dbContext.StarterTaxRecords
                .AnyAsync(tax => tax.OnboardingRecordId == record.Id, cancellationToken);

            if (!declared)
            {
                outstanding.Add("Tax details (P45 or Starter Checklist)");
            }
        }

        return outstanding;
    }

    private async Task<JoinerPackDto> DescribeAsync(
        OnboardingRecord record,
        CancellationToken cancellationToken)
    {
        List<JoinerDocumentDto> documents = await dbContext.JoinerDocuments
            .AsNoTracking()
            .Where(document => document.OnboardingRecordId == record.Id)
            .OrderBy(document => document.Kind)
            .Select(document => new JoinerDocumentDto
            {
                Kind = document.Kind,
                FileId = document.FileId,
                FileName = dbContext.StoredFiles
                    .Where(file => file.Id == document.FileId)
                    .Select(file => file.FileName)
                    .FirstOrDefault() ?? "Unknown",
                UploadedAt = document.UploadedAt,
            })
            .ToListAsync(cancellationToken);

        List<GuarantorDto> guarantors = await dbContext.Guarantors
            .AsNoTracking()
            .Where(g => g.OnboardingRecordId == record.Id)
            .OrderBy(g => g.Position)
            .Select(g => new GuarantorDto
            {
                Id = g.Id,
                Position = g.Position,
                Name = g.Name,
                Relationship = g.Relationship,
                Occupation = g.Occupation,
                Address = g.Address,
                Phone = g.Phone,
            })
            .ToListAsync(cancellationToken);

        StarterTaxRecord? tax = await dbContext.StarterTaxRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.OnboardingRecordId == record.Id, cancellationToken);

        return new JoinerPackDto
        {
            OnboardingRecordId = record.Id,
            PrivacyConsent = record.PrivacyConsent,
            Declaration = record.Declaration,
            Documents = documents,
            Guarantors = guarantors,
            StarterTax = tax is null
                ? null
                : new StarterTaxDto
                {
                    Source = tax.Source,
                    EmploymentStartDate = tax.EmploymentStartDate,
                    P45 = tax.P45,
                    StarterChecklist = tax.StarterChecklist,
                    Derived = tax.Derived,
                    RetainUntil = tax.RetainUntil,
                },
            DraftJson = record.DraftJson,
            DraftStep = record.DraftStep,
            DraftSavedAt = record.DraftSavedAt,
            Outstanding = await OutstandingAsync(record, cancellationToken),
        };
    }

    private async Task<OnboardingRecord?> FindAsync(
        Guid onboardingRecordId,
        CancellationToken cancellationToken) =>
        await dbContext.OnboardingRecords
            .FirstOrDefaultAsync(record => record.Id == onboardingRecordId, cancellationToken);

    private async Task<CountryCode> CountryAsync(CancellationToken cancellationToken)
    {
        if (currentTenant.TenantId is not Guid tenantId)
        {
            return CountryCode.Nigeria;
        }

        return await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.CountryCode)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
