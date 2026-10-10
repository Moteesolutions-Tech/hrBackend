using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Contracts;
using Motee.Api.Contracts.Auth;
using Motee.Application.Auth;
using Motee.Application.Employees;
using Motee.Application.Onboarding;
using Motee.Application.Tenancy;
using Motee.Domain.Employees;
using Motee.Domain.Files;
using Motee.Domain.Onboarding;

namespace Motee.Api.Controllers;

// Anonymous by necessity: the joiner has no account until they finish here. The
// token is the credential, so it is random, hashed at rest and single-use.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/join")]
[AllowAnonymous]
public class JoinController(
    IEmployeeInvitationService invitations,
    IJoinerPackService pack) : ApiControllerBase
{
    // What this company asks a joiner for. Country-specific, so the form cannot be built
    // without it: Nigeria asks for guarantors, the UK for a tax declaration.
    [HttpGet("{token}/requirements")]
    [ProducesResponseType<JoinerPackRequirementsDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Requirements(string token, CancellationToken cancellationToken) =>
        WithScope(token, cancellationToken, async _ =>
            Ok(await pack.RequirementsAsync(cancellationToken)));

    [HttpGet("{token}/pack")]
    [ProducesResponseType<JoinerPackDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Pack(string token, CancellationToken cancellationToken) =>
        WithScope(token, cancellationToken, async recordId =>
            Ok(await pack.GetAsync(recordId, cancellationToken)));

    // Accepting the privacy notice, which gates everything else. No body: the version in
    // force is the backend's to stamp, not the caller's to claim.
    [HttpPost("{token}/consent")]
    [ProducesResponseType<JoinerPackDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Consent(string token, CancellationToken cancellationToken) =>
        WithScope(token, cancellationToken, async recordId =>
            Respond(await pack.AcceptPrivacyNoticeAsync(recordId, cancellationToken)));

    [HttpPut("{token}/documents/{kind}")]
    [ProducesResponseType<JoinerPackDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<JoinerPackDto>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> AttachDocument(
        string token,
        JoinerDocumentKind kind,
        AttachJoinerDocumentRequest request,
        CancellationToken cancellationToken) =>
        WithScope(token, cancellationToken, async recordId =>
            Respond(await pack.AttachDocumentAsync(
                recordId, kind, request.FileId, cancellationToken)));

    [HttpDelete("{token}/documents/{kind}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> RemoveDocument(
        string token,
        JoinerDocumentKind kind,
        CancellationToken cancellationToken) =>
        WithScope(token, cancellationToken, async recordId =>
        {
            JoinerPackOutcome outcome = await pack.RemoveDocumentAsync(
                recordId, kind, cancellationToken);

            return outcome == JoinerPackOutcome.Succeeded
                ? Ok<object?>(null, "Document removed.")
                : Failure<object?>(PackStatusFor(outcome), PackMessageFor(outcome));
        });

    [HttpPut("{token}/guarantors")]
    [ProducesResponseType<JoinerPackDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<JoinerPackDto>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Guarantors(
        string token,
        SaveGuarantorsRequest request,
        CancellationToken cancellationToken) =>
        WithScope(token, cancellationToken, async recordId =>
            Respond(await pack.SaveGuarantorsAsync(
                recordId, request.Guarantors, cancellationToken)));

    [HttpPut("{token}/starter-tax")]
    [ProducesResponseType<JoinerPackDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<JoinerPackDto>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> StarterTax(
        string token,
        StarterTaxRequest request,
        CancellationToken cancellationToken) =>
        WithScope(token, cancellationToken, async recordId =>
            Respond(await pack.SaveStarterTaxAsync(recordId, request, cancellationToken)));

    // "Save & finish later". Parks progress against the same link, so returning resumes
    // on the step they left off on.
    [HttpPut("{token}/draft")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> SaveDraft(
        string token,
        SaveJoinerDraftRequest request,
        CancellationToken cancellationToken) =>
        WithScope(token, cancellationToken, async recordId =>
        {
            JoinerPackOutcome outcome = await pack.SaveDraftAsync(
                recordId, request.DraftJson, request.Step, cancellationToken);

            return outcome == JoinerPackOutcome.Succeeded
                ? Ok<object?>(null, "Progress saved.")
                : Failure<object?>(PackStatusFor(outcome), PackMessageFor(outcome));
        });

    [HttpPost("{token}/declare")]
    [ProducesResponseType<JoinerPackDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<JoinerPackDto>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Declare(
        string token,
        DeclareJoinerPackRequest request,
        CancellationToken cancellationToken) =>
        WithScope(token, cancellationToken, async recordId =>
            Respond(await pack.DeclareAsync(recordId, request.SignedName, cancellationToken)));

    // Resolves the token to a record and runs the action as that tenant.
    //
    // The token is the only credential a joiner has, so it is the only thing that decides
    // what is reachable. An endpoint taking a record id would let anybody holding any
    // link fill in anybody else's pack.
    private async Task<IActionResult> WithScope(
        string token,
        CancellationToken cancellationToken,
        Func<Guid, Task<IActionResult>> action)
    {
        JoinerScope? scope = await invitations.ScopeAsync(token, cancellationToken);

        // An unknown token and a withdrawn one are the same answer, so probing tells
        // nobody anything.
        if (scope is null || !scope.Usable)
        {
            return Failure<object?>(
                MoteeStatusCodes.NotFound, "That link is no longer valid.");
        }

        // The joiner has no tenant claim, so the token establishes one for the duration
        // of the call. The query filter reads this the same way it reads a real claim.
        using IDisposable tenant = AmbientTenant.Use(scope.TenantId);

        return await action(scope.OnboardingRecordId!.Value);
    }

    private IActionResult Respond(JoinerPackResult result) =>
        result.Succeeded
            ? Ok(result.Pack!, "Saved.")
            : Failure<JoinerPackDto>(
                PackStatusFor(result.Outcome),
                result.Outstanding.Count > 0
                    ? $"Still needed: {string.Join(", ", result.Outstanding)}."
                    : PackMessageFor(result.Outcome));

    private static string PackStatusFor(JoinerPackOutcome outcome) => outcome switch
    {
        JoinerPackOutcome.NotFound => MoteeStatusCodes.NotFound,

        // Not an invalid request: the form is fine, the prerequisite is not met.
        JoinerPackOutcome.ConsentMissing or JoinerPackOutcome.Incomplete =>
            MoteeStatusCodes.Conflict,
        _ => MoteeStatusCodes.InvalidRequest,
    };

    private static string PackMessageFor(JoinerPackOutcome outcome) => outcome switch
    {
        JoinerPackOutcome.NotFound => "Not found.",
        JoinerPackOutcome.ConsentMissing =>
            "Please accept the privacy notice before entering your details.",
        JoinerPackOutcome.NotApplicable => "That is not asked for in this country.",
        JoinerPackOutcome.UnknownFile => "That file could not be found.",
        JoinerPackOutcome.Incomplete => "Some required details are still missing.",
        JoinerPackOutcome.ContradictorySource =>
            "Choose either a P45 or the Starter Checklist, and fill in the one you chose.",
        JoinerPackOutcome.InvalidGuarantors => "Two guarantors are required.",
        _ => "Could not save.",
    };

    [HttpGet("{token}")]
    [ProducesResponseType<InvitationPreview>(StatusCodes.Status200OK)]
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
    [ProducesResponseType(StatusCodes.Status200OK)]
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
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
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

// The file is uploaded through the files module first, then referenced here — the same
// two-step the avatar and approval attachments use, so the size and type checks live in
// one place.
public sealed record AttachJoinerDocumentRequest
{
    public required Guid FileId { get; init; }
}

public sealed record SaveGuarantorsRequest
{
    public required IReadOnlyList<GuarantorRequest> Guarantors { get; init; }
}

public sealed record SaveJoinerDraftRequest
{
    public required string DraftJson { get; init; }

    public int? Step { get; init; }
}

public sealed record DeclareJoinerPackRequest
{
    public required string SignedName { get; init; }
}
