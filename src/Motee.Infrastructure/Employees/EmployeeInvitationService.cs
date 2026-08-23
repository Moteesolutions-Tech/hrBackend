using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Tenancy;
using Motee.Application.Files;
using Motee.Domain.Employees;
using Motee.Domain.Files;
using Motee.Domain.Identity;
using Motee.Infrastructure.Common;
using Motee.Infrastructure.Identity;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Employees;

internal sealed class EmployeeInvitationService(
    MoteeDbContext dbContext,
    IEmployeeService employees,
    UserManager<ApplicationUser> userManager,
    ISessionIssuer sessionIssuer,
    IEmailQueue emailQueue,
    AppLinks links,
    IFileUploadService files,
    IRequestContext requestContext,
    TimeProvider timeProvider) : IEmployeeInvitationService
{
    private const int TokenBytes = 32;

    public async Task<InviteResult> InviteAsync(
        InviteRequest request,
        CancellationToken cancellationToken = default)
    {
        // The employee record exists immediately so the person appears in the
        // pipeline as Pending, whether or not they ever open the link.
        EmployeeResult created = await employees.CreateAsync(
            new EmployeeRequest
            {
                FirstName = request.FirstName,
                MiddleName = request.MiddleName,
                LastName = request.LastName,
                Email = request.Email,
                Phone = string.Empty,
                JobTitle = request.JobTitle,
                DepartmentId = request.DepartmentId,
                EmploymentType = request.EmploymentType,
                ManagerId = request.ManagerId,
                StartDate = request.StartDate,
                Status = EmployeeStatus.Pending,
            },
            OnboardingMethod.Invite,
            cancellationToken);

        if (!created.Succeeded)
        {
            return InviteResult.Failed(created.Outcome);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        string token = await IssueTokenAsync(
            created.Employee!.Id,
            created.Employee.Email,
            InvitationPurpose.Onboarding,
            now,
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new InviteResult
        {
            Outcome = EmployeeOutcome.Succeeded,
            Employee = created.Employee,
            Token = token,
            ExpiresAt = InvitationPolicy.ExpiresAt(now),
        };
    }

    public async Task<IssueInviteResult> IssueAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        Employee? employee = await dbContext.Employees
            .FirstOrDefaultAsync(candidate => candidate.Id == employeeId, cancellationToken);

        if (employee is null)
        {
            return IssueInviteResult.Failed(IssueInviteOutcome.EmployeeNotFound);
        }

        // Inviting a leaver back in is a mistake, not a convenience.
        if (employee.Status is EmployeeStatus.Offboarding
            or EmployeeStatus.Inactive
            or EmployeeStatus.Deleted)
        {
            return IssueInviteResult.Failed(IssueInviteOutcome.EmployeeNotJoinable);
        }

        // Accepting creates the account, so someone who already has one has nothing to
        // accept. They sign in, or reset their password.
        if (await userManager.FindByEmailAsync(employee.Email) is not null)
        {
            return IssueInviteResult.Failed(IssueInviteOutcome.AccountExists);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        string token = await IssueTokenAsync(
            employee.Id, employee.Email, PurposeFor(employee), now, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new IssueInviteResult
        {
            Outcome = IssueInviteOutcome.Succeeded,
            Token = token,
            ExpiresAt = InvitationPolicy.ExpiresAt(now),
        };
    }

    public async Task<IssueInviteResult> RevokeAsync(
        Guid employeeId,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Employees.AnyAsync(
                employee => employee.Id == employeeId, cancellationToken))
        {
            return IssueInviteResult.Failed(IssueInviteOutcome.EmployeeNotFound);
        }

        int revoked = await RevokeOutstandingAsync(
            employeeId, timeProvider.GetUtcNow(), cancellationToken);

        if (revoked == 0)
        {
            return IssueInviteResult.Failed(IssueInviteOutcome.NoInvitationOutstanding);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return new IssueInviteResult { Outcome = IssueInviteOutcome.Succeeded };
    }

    public async Task<IReadOnlyList<PendingInvitationDto>> PendingAsync(
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        // Expired invitations stay on the list. They are exactly the ones worth
        // chasing — dropping them makes someone who never joined disappear.
        return await dbContext.EmployeeInvitations
            .AsNoTracking()
            .Where(invitation => invitation.ConsumedAt == null && invitation.RevokedAt == null)
            .OrderByDescending(invitation => invitation.CreatedAt)
            .Select(invitation => new PendingInvitationDto
            {
                EmployeeId = invitation.EmployeeId,
                Name = dbContext.Employees
                    .Where(employee => employee.Id == invitation.EmployeeId)
                    .Select(employee => employee.FirstName + " " + employee.LastName)
                    .FirstOrDefault() ?? invitation.Email,
                Email = invitation.Email,
                JobTitle = dbContext.Employees
                    .Where(employee => employee.Id == invitation.EmployeeId)
                    .Select(employee => employee.JobTitle)
                    .FirstOrDefault(),
                SentAt = invitation.CreatedAt,
                ExpiresAt = invitation.ExpiresAt,
                Expired = invitation.ExpiresAt <= now,
            })
            .ToListAsync(cancellationToken);
    }

    // Only the Send Invite route asks the person to complete a profile. Manual Entry
    // and Bulk Upload already captured everything, so those people are shown nothing
    // and asked only for a password.
    private static InvitationPurpose PurposeFor(Employee employee) =>
        employee.OnboardingMethod == OnboardingMethod.Invite
            ? InvitationPurpose.Onboarding
            : InvitationPurpose.Credentials;

    // Adds the invitation and queues the email without saving, so the caller decides
    // the transaction. Any earlier invitation is revoked first: after three resends,
    // only the newest link should open the door.
    private async Task<string> IssueTokenAsync(
        Guid employeeId,
        string email,
        InvitationPurpose purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await RevokeOutstandingAsync(employeeId, now, cancellationToken);

        string token = GenerateToken();

        dbContext.EmployeeInvitations.Add(new EmployeeInvitation
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            TokenHash = Hash(token),
            Email = email,
            Purpose = purpose,
            CreatedAt = now,
            ExpiresAt = InvitationPolicy.ExpiresAt(now),
            InvitedByUserId = Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null,
        });

        emailQueue.Enqueue(purpose == InvitationPurpose.Credentials
            ? new EmailMessage
            {
                To = email,
                Subject = "Set up your Motee password",
                Body =
                    "Your employer has set up your Motee account. "
                    + "Choose a password to sign in.\n\n"
                    + $"{links.Join(token)}\n\n"
                    + "This link works once, and expires on "
                    + $"{InvitationPolicy.ExpiresAt(now):d MMMM yyyy}.",
            }
            : new EmailMessage
            {
                To = email,
                Subject = "You have been invited to Motee",
                Body =
                    "You have been invited to complete your onboarding.\n\n"
                    + $"{links.Join(token)}\n\n"
                    + "This link works once, and expires on "
                    + $"{InvitationPolicy.ExpiresAt(now):d MMMM yyyy}.",
            });

        return token;
    }

    private async Task<int> RevokeOutstandingAsync(
        Guid employeeId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        List<EmployeeInvitation> outstanding = await dbContext.EmployeeInvitations
            .Where(invitation => invitation.EmployeeId == employeeId
                && invitation.ConsumedAt == null
                && invitation.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (EmployeeInvitation invitation in outstanding)
        {
            invitation.RevokedAt = now;
        }

        return outstanding.Count;
    }

    public async Task<InvitationPreview?> PreviewAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        EmployeeInvitation? invitation = await FindAsync(token, cancellationToken);

        if (invitation is null)
        {
            return null;
        }

        InvitationOutcome outcome = InvitationPolicy.Evaluate(invitation, timeProvider.GetUtcNow());

        if (outcome != InvitationOutcome.Valid)
        {
            return new InvitationPreview { Outcome = outcome };
        }

        // Filters bypassed for the same reason as the lookup: no tenant is resolved.
        var details = await dbContext.Employees
            .IgnoreQueryFilters()
            .Where(employee => employee.Id == invitation.EmployeeId)
            .Select(employee => new
            {
                employee.FirstName,
                employee.MiddleName,
                employee.LastName,
                employee.Email,
                employee.JobTitle,
                employee.DepartmentId,
                employee.EmploymentType,
                employee.StartDate,
                Department = dbContext.Departments.IgnoreQueryFilters()
                    .Where(department => department.Id == employee.DepartmentId)
                    .Select(department => department.Name)
                    .FirstOrDefault(),
                CompanyName = dbContext.Tenants
                    .Where(tenant => tenant.Id == employee.TenantId)
                    .Select(tenant => tenant.Name)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (details is null)
        {
            return null;
        }

        // A password page needs only enough to know whose account this is and who set
        // it up. Job title, department, employment type and start date are what HR
        // recorded, and there is nothing for this person to do with them.
        if (invitation.Purpose == InvitationPurpose.Credentials)
        {
            return new InvitationPreview
            {
                Outcome = InvitationOutcome.Valid,
                Purpose = InvitationPurpose.Credentials,
                Email = details.Email,
                CompanyName = details.CompanyName,
            };
        }

        return new InvitationPreview
            {
                Purpose = InvitationPurpose.Onboarding,
                Outcome = InvitationOutcome.Valid,
                FirstName = details.FirstName,
                MiddleName = details.MiddleName,
                LastName = details.LastName,
                Email = details.Email,
                JobTitle = details.JobTitle,
                DepartmentId = details.DepartmentId,
                Department = details.Department,
                EmploymentType = details.EmploymentType,
                StartDate = details.StartDate,
                CompanyName = details.CompanyName,
            };
    }

    public async Task<JoinPhotoResult> UploadPhotoAsync(
        string token,
        JoinPhotoUpload upload,
        CancellationToken cancellationToken = default)
    {
        EmployeeInvitation? invitation = await FindAsync(token, cancellationToken);

        if (invitation is null)
        {
            return JoinPhotoResult.Failed(InvitationOutcome.Revoked);
        }

        InvitationOutcome outcome = InvitationPolicy.Evaluate(invitation, timeProvider.GetUtcNow());

        if (outcome != InvitationOutcome.Valid)
        {
            return JoinPhotoResult.Failed(outcome);
        }

        Employee? employee = await dbContext.Employees
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.Id == invitation.EmployeeId, cancellationToken);

        if (employee is null)
        {
            return JoinPhotoResult.Failed(InvitationOutcome.Revoked);
        }

        // The token identifies the company. Without this the upload has no tenant and
        // the file cannot be stored at all.
        using IDisposable tenant = AmbientTenant.Use(employee.TenantId);

        FileUploadResult stored = await files.UploadAsync(
            new FileUpload
            {
                // Size and type are enforced by FilePolicy, which already limits
                // avatars to 2MB and images only.
                Purpose = FilePurpose.EmployeeAvatar,
                OwnerId = employee.Id,
                FileName = upload.FileName,
                ContentType = upload.ContentType,
                Content = upload.Content,
                SizeBytes = upload.SizeBytes,
            },
            cancellationToken);

        if (!stored.Succeeded)
        {
            return new JoinPhotoResult
            {
                Outcome = InvitationOutcome.Valid,
                Rejection = stored.Rejection,
            };
        }

        // Attached immediately: the wizard is several steps long, and a photo that
        // only lands on submit is lost if they close the tab.
        employee.AvatarFileId = stored.File!.Id;
        employee.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken);

        return new JoinPhotoResult
        {
            Outcome = InvitationOutcome.Valid,
            FileId = stored.File.Id,
        };
    }

    public async Task<AcceptInviteResult> AcceptAsync(
        string token,
        AcceptInviteRequest request,
        CancellationToken cancellationToken = default)
    {
        EmployeeInvitation? invitation = await FindAsync(token, cancellationToken);

        if (invitation is null)
        {
            return AcceptInviteResult.Failed(AcceptInviteOutcome.InvalidToken);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        switch (InvitationPolicy.Evaluate(invitation, now))
        {
            case InvitationOutcome.Expired:
                return AcceptInviteResult.Failed(AcceptInviteOutcome.Expired);
            case InvitationOutcome.Consumed:
                return AcceptInviteResult.Failed(AcceptInviteOutcome.AlreadyUsed);
            case InvitationOutcome.Revoked:
                return AcceptInviteResult.Failed(AcceptInviteOutcome.Revoked);
        }

        Employee? employee = await dbContext.Employees
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.Id == invitation.EmployeeId, cancellationToken);

        if (employee is null)
        {
            return AcceptInviteResult.Failed(AcceptInviteOutcome.InvalidToken);
        }

        if (await userManager.FindByEmailAsync(employee.Email) is not null)
        {
            return AcceptInviteResult.Failed(AcceptInviteOutcome.AccountExists);
        }

        ApplicationUser user = new()
        {
            Id = Guid.NewGuid(),
            UserName = employee.Email,
            Email = employee.Email,
            TenantId = employee.TenantId,

            // Receiving the emailed link is itself proof of mailbox control, so there
            // is no second code to enter.
            EmailConfirmed = true,

            EmployeeId = employee.Id,
            FirstName = employee.FirstName,
            LastName = employee.LastName,
            CreatedAt = now,
        };

        IdentityResult creation = await userManager.CreateAsync(user, request.Password);

        if (!creation.Succeeded)
        {
            return AcceptInviteResult.Failed(
                creation.Errors.Any(error => error.Code.StartsWith("Password", StringComparison.Ordinal))
                    ? AcceptInviteOutcome.WeakPassword
                    : AcceptInviteOutcome.AccountExists);
        }

        // No access level is assigned. The self-service floor already reaches their own
        // record and their own submissions, which is everything a new joiner needs on
        // day one — and guessing a level for them would be guessing at their job.
        // HR grants one when they decide what it should be.

        // The joiner is anonymous, so nothing has resolved a tenant. Without this the
        // profile write finds no employee and the bank, identity and medical rows are
        // stamped with an empty tenant — invisible to the company that owns them.
        // The save has to happen inside this scope, not after it: SaveChanges is what
        // stamps the tenant on the new bank, identity and medical rows, and by then
        // the joiner still has no tenant of their own.
        using (AmbientTenant.Use(employee.TenantId))
        {
            // A credentials link is only a password. HR already captured this record,
            // so anything else in the payload is not theirs to write — and unlike a
            // disabled input, this cannot be worked around.
            if (invitation.Purpose == InvitationPurpose.Onboarding)
            {
                // Only the self-owned half of the form. Everything the admin set at
                // invite time is untouched, whatever the payload says.
                await employees.StageSelfProfileAsync(employee.Id, request, cancellationToken);
            }

            // Onboarded is where a new hire lands. Someone imported from a spreadsheet
            // is already Active and has been for years — accepting an invitation gives
            // them an account, and must not walk their status backwards. The lifecycle
            // already knows which moves are real, so it decides rather than this line.
            if (EmployeeLifecycle.CanMove(employee.Status, EmployeeStatus.Onboarded))
            {
                employee.Status = EmployeeStatus.Onboarded;
            }

            employee.UpdatedAt = now;

            invitation.ConsumedAt = now;

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new AcceptInviteResult
        {
            Outcome = AcceptInviteOutcome.Succeeded,
            Session = await sessionIssuer.IssueAsync(user.Id, null, cancellationToken),
        };
    }

    // The joiner is anonymous, so no tenant is resolved and the global filter would
    // match nothing. The token is the credential; it identifies the tenant, not the
    // other way round.
    private Task<EmployeeInvitation?> FindAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult<EmployeeInvitation?>(null);
        }

        string hash = Hash(token);

        return dbContext.EmployeeInvitations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(invitation => invitation.TokenHash == hash, cancellationToken);
    }


    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

}
