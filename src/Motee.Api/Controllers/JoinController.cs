using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Contracts;
using Motee.Api.Contracts.Auth;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Domain.Employees;
using Motee.Domain.Files;

namespace Motee.Api.Controllers;

// Anonymous by necessity: the joiner has no account until they finish here. The
// token is the credential, so it is random, hashed at rest and single-use.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/join")]
[AllowAnonymous]
public class JoinController(IEmployeeInvitationService invitations) : ApiControllerBase
{
    [HttpGet("{token}")]
    public async Task<IActionResult> Preview(string token, CancellationToken cancellationToken)
    {
        InvitationPreview? preview = await invitations.PreviewAsync(token, cancellationToken);

        // An unknown token and a withdrawn one are the same answer, so probing tells
        // an attacker nothing about which employees exist.
        if (preview is null || preview.Outcome == InvitationOutcome.Revoked)
        {
            return Failure<InvitationPreview>(
                MoteeStatusCodes.NotFound, "This invitation link is not valid.");
        }

        return preview.Outcome switch
        {
            InvitationOutcome.Valid => Ok(preview),
            InvitationOutcome.Consumed => Failure<InvitationPreview>(
                MoteeStatusCodes.Conflict, "This invitation has already been used. Sign in instead."),
            _ => Failure<InvitationPreview>(
                MoteeStatusCodes.Gone, "This invitation has expired. Ask your HR team for a new one."),
        };
    }

    // Uploaded before the wizard is submitted, so a photo survives closing the tab
    // halfway through. The token is the only credential, and it is what tells us which
    // company the file belongs to.
    [HttpPost("{token}/photo")]
    [RequestSizeLimit(MaxPhotoBytes)]
    public async Task<IActionResult> Photo(
        string token,
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        await using Stream content = file.OpenReadStream();

        JoinPhotoResult result = await invitations.UploadPhotoAsync(
            token,
            new JoinPhotoUpload
            {
                FileName = file.FileName,
                ContentType = file.ContentType,
                Content = content,
                SizeBytes = file.Length,
            },
            cancellationToken);

        if (result.Succeeded)
        {
            return Ok<object>(new { fileId = result.FileId }, "Photo uploaded.");
        }


        return result.Rejection is FileRejection rejection
            ? Failure<object>(FileStatusFor(rejection), FileMessageFor(rejection))
            : Failure<object>(StatusFor(result.Outcome), MessageFor(result.Outcome));
    }

    [HttpPost("{token}")]
    public async Task<IActionResult> Accept(
        string token,
        AcceptInviteRequest request,
        CancellationToken cancellationToken)
    {
        AcceptInviteResult result = await invitations.AcceptAsync(token, request, cancellationToken);

        if (!result.Succeeded)
        {
            return Failure<LoginResponse>(StatusFor(result.Outcome), MessageFor(result.Outcome));
        }

        IssuedSession session = result.Session!;

        // Signed straight in: they have just set a password and proved control of the
        // mailbox by following the link, so a login screen would ask for both again.
        return Ok(
            new LoginResponse
            {
                AccessToken = session.AccessToken.Value,
                ExpiresAt = session.AccessToken.ExpiresAt,
                RefreshToken = session.RefreshToken.Value,
                RefreshTokenExpiresAt = session.RefreshToken.ExpiresAt,
                UserId = session.UserId,
                TenantId = session.TenantId,
                OnboardingCompleted = session.OnboardingCompleted,
            },
            "Welcome aboard.");
    }

    // A little above the 2MB FilePolicy allows for an avatar, so an oversized file is
    // rejected with a readable message rather than a bare connection reset.
    private const int MaxPhotoBytes = 4 * 1024 * 1024;

    // A dead link tells the joiner nothing useful about which of the four ways it
    // died, but it does need to say "get a new one".
    private static string StatusFor(InvitationOutcome outcome) => outcome switch
    {
        InvitationOutcome.Expired => MoteeStatusCodes.Gone,
        InvitationOutcome.Consumed => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.NotFound,
    };

    private static string MessageFor(InvitationOutcome outcome) => outcome switch
    {
        InvitationOutcome.Expired => "This invitation link has expired. Ask your HR team for a new one.",
        InvitationOutcome.Consumed => "This invitation has already been used. Sign in instead.",
        _ => "This invitation link is not valid.",
    };

    private static string FileStatusFor(FileRejection rejection) => rejection switch
    {
        FileRejection.TooLarge => MoteeStatusCodes.PayloadTooLarge,
        FileRejection.NoTenant => MoteeStatusCodes.InternalServerError,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string FileMessageFor(FileRejection rejection) => rejection switch
    {
        FileRejection.Empty => "That file is empty.",
        FileRejection.TooLarge => "That photo is too large.",
        FileRejection.DisallowedType => "Upload a PNG, JPEG or WebP image.",
        FileRejection.ContentDoesNotMatchType => "That file's contents do not match its type.",
        _ => "Could not upload that photo.",
    };

    private static string StatusFor(AcceptInviteOutcome outcome) => outcome switch
    {
        AcceptInviteOutcome.InvalidToken => MoteeStatusCodes.NotFound,
        AcceptInviteOutcome.Revoked => MoteeStatusCodes.NotFound,
        AcceptInviteOutcome.Expired => MoteeStatusCodes.Gone,
        AcceptInviteOutcome.AlreadyUsed or AcceptInviteOutcome.AccountExists => MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string MessageFor(AcceptInviteOutcome outcome) => outcome switch
    {
        AcceptInviteOutcome.InvalidToken or AcceptInviteOutcome.Revoked =>
            "This invitation link is not valid.",
        AcceptInviteOutcome.Expired =>
            "This invitation has expired. Ask your HR team for a new one.",
        AcceptInviteOutcome.AlreadyUsed or AcceptInviteOutcome.AccountExists =>
            "An account already exists for this address. Sign in instead.",
        AcceptInviteOutcome.WeakPassword =>
            "Password must be at least 8 characters.",
        _ => "Could not complete your onboarding.",
    };
}
