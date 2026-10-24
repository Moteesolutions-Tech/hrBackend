using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Application.Employees;
using Motee.Application.Tenancy;
using Motee.Application.Files;
using Motee.Application.Notifications;
using Motee.Domain.Authorization;
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
    Application.Onboarding.IOnboardingService onboarding,
    UserManager<ApplicationUser> userManager,
    ISessionIssuer sessionIssuer,
    IEmailDispatcher emailDispatcher,
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
        // Before the employee is created, so a level that cannot be granted costs
        // nothing. Checking after would leave a half-done invitation behind on a
        // mistake the owner could fix in one click.
        if (request.AccessLevelId is Guid level
            && !await AssignableAsync(level, cancellationToken))
        {
            return InviteResult.Failed(EmployeeOutcome.UnknownAccessLevel);
        }

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
            cancellationToken,
            request.AccessLevelId);

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

        // Carried over from the link being replaced. A resend is the same invitation
        // sent again — somebody invited to run HR who never opened their email is
        // still being invited to run HR, and dropping that on a resend would undo the
        // decision without anyone touching it.
        Guid? intendedLevel = await dbContext.EmployeeInvitations
            .Where(invitation => invitation.EmployeeId == employeeId
                && invitation.ConsumedAt == null
                && invitation.RevokedAt == null)
            .OrderByDescending(invitation => invitation.CreatedAt)
            .Select(invitation => invitation.AccessLevelId)
            .FirstOrDefaultAsync(cancellationToken);

        // Unless it has since been deactivated, in which case the resend goes without
        // it rather than failing: the person still needs to get in, and the level is
        // something an administrator can grant afterwards.
        if (intendedLevel is Guid level && !await AssignableAsync(level, cancellationToken))
        {
            intendedLevel = null;
        }

        string token = await IssueTokenAsync(
            employee.Id,
            employee.Email,
            PurposeFor(employee),
            now,
            cancellationToken,
            intendedLevel);

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
                EmailSentAt = invitation.EmailSentAt,
                AccessLevelName = dbContext.AccessLevels
                    .Where(level => level.Id == invitation.AccessLevelId)
                    .Select(level => level.Name)
                    .FirstOrDefault(),
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

    // Adds the invitation, commits it, then queues the email. Any earlier invitation is
    // revoked first: after three resends, only the newest link should open the door.
    // Active only. A draft is unfinished and a deactivated one has been withdrawn;
    // neither can be held, so neither is a thing to promise somebody on arrival.
    private Task<bool> AssignableAsync(Guid accessLevelId, CancellationToken cancellationToken) =>
        dbContext.AccessLevels.AnyAsync(
            level => level.Id == accessLevelId && level.Status == AccessLevelStatus.Active,
            cancellationToken);

    private async Task<string> IssueTokenAsync(
        Guid employeeId,
        string email,
        InvitationPurpose purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Guid? accessLevelId = null)
    {
        await RevokeOutstandingAsync(employeeId, now, cancellationToken);

        string token = GenerateToken();

        EmployeeInvitation invitation = new()
        {
            Id = Guid.NewGuid(),
            EmployeeId = employeeId,
            TokenHash = Hash(token),
            Email = email,
            Purpose = purpose,
            CreatedAt = now,
            ExpiresAt = InvitationPolicy.ExpiresAt(now),
            InvitedByUserId = Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null,
            AccessLevelId = accessLevelId,
        };

        dbContext.EmployeeInvitations.Add(invitation);

        // Committed before the email is handed over, and that order matters twice.
        //
        // Delivery runs on a background worker that marks this row as sent: enqueued
        // first, it could look for the invitation before the transaction committed and
        // find nothing. And the link in the email only works once the row exists, so
        // sending first offers a token that would be rejected by whoever is quickest.
        //
        // It also decides what an unreachable queue costs. Saved first, a failed
        // enqueue leaves a real invitation showing as unsent, which is accurate and
        // which Resend fixes. The other way round it threw away the invitation while
        // the employee record it belongs to had already been written.
        await dbContext.SaveChangesAsync(cancellationToken);

        emailDispatcher.Send(
            email,
            new EmployeeInviteEmail
            {
                JoinUrl = links.Join(token),
                ExpiresAt = InvitationPolicy.ExpiresAt(now),
                Purpose = purpose,
            },
            new EmailAbout { Kind = EmailAbout.Invitation, Id = invitation.Id });

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

        // The level they were invited to hold, if they were invited to hold one.
        //
        // Nobody is given one by default: the self-service floor already reaches their
        // own record and their own submissions, which is everything a new joiner needs,
        // and guessing beyond that would be guessing at their job. This is not a guess —
        // somebody chose it when they sent the invitation.
        //
        // Re-checked rather than trusted. A link lives fourteen days, and a level can be
        // deactivated or deleted inside that window; the one stored here says what was
        // meant a fortnight ago, not what is grantable now.
        //
        // A level that can no longer be granted does not stop them joining. They arrive
        // holding nothing, which an administrator can fix in one click, and the
        // alternative is turning somebody away from their first day over a
        // configuration change they had no part in.
        if (invitation.AccessLevelId is Guid intended
            && await AssignableAsync(intended, cancellationToken))
        {
            dbContext.UserAccessLevels.Add(new UserAccessLevel
            {
                Id = Guid.NewGuid(),
                TenantId = employee.TenantId,
                UserId = user.Id,
                AccessLevelId = intended,
                AssignedAt = now,
            });
        }

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

            // Completing the wizard is the submission. Making it a separate act the
            // joiner has to remember is how packs sit finished-but-unsent for a week
            // while HR chase somebody who thinks they are done.
            //
            // After the save, not before: the review resolves approvers and writes its
            // own rows, and it should run against a joiner who is already committed.
            // Its outcome is deliberately not checked — a chain that cannot start is a
            // configuration problem on the company's side, and refusing to let somebody
            // finish signing up over it would be punishing the wrong person.
            await onboarding.SubmitAsync(employee.Id, cancellationToken);
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
    public async Task<JoinerScope?> ScopeAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        EmployeeInvitation? invitation = await FindAsync(token, cancellationToken);

        if (invitation is null)
        {
            return null;
        }

        // Filters ignored for the same reason PreviewAsync ignores them: there is no
        // tenant on the request, and the token is what establishes which one applies.
        Guid? recordId = await dbContext.OnboardingRecords
            .IgnoreQueryFilters()
            .Where(record => record.EmployeeId == invitation.EmployeeId
                && record.TenantId == invitation.TenantId)
            .Select(record => (Guid?)record.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return new JoinerScope
        {
            Outcome = InvitationPolicy.Evaluate(invitation, timeProvider.GetUtcNow()),
            TenantId = invitation.TenantId,
            EmployeeId = invitation.EmployeeId,
            OnboardingRecordId = recordId,
        };
    }

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
