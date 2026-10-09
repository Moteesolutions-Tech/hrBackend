using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Motee.Api.Http;
using Motee.Api.Contracts;
using Motee.Api.Contracts.Auth;
using Motee.Application.Auth;
using Motee.Application.Common;
using Motee.Domain.Auth;
using Motee.Application.Tenancy;
using Motee.Domain.Authorization;
using Motee.Domain.Tenants;

namespace Motee.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth")]
// Every endpoint here is reachable without a token, so nothing but this bounds how
// often one caller can reach them. The authenticated parts of the API are bounded by
// having to hold a token at all.
//
// It is a volume limit, not the per-account control: the OTP resend cooldown and
// Identity's lockout after five failed passwords are what stop one person being
// targeted, and they keep working when an attacker changes address.
[EnableRateLimiting(RateLimitSetup.AuthPolicy)]
public class AuthController(
    ITenantRegistrationService registration,
    IOtpService otp,
    ILoginService login,
    IRefreshTokenService refreshTokens,
    IPasswordResetService passwordReset,
    ISessionIssuer sessionIssuer,
    IUserPermissions userPermissions,
    ICurrentTenant currentTenant,
    ITenantRepository tenants,
    IRequestContext requestContext,
    IUserLookup users,
    IValidator<RegisterTenantRequest> registerValidator) : ApiControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSetup.ExpensiveAuthPolicy)]
    public async Task<IActionResult> Register(
        RegisterTenantRequest request,
        CancellationToken cancellationToken)
    {
        ValidationResult validation = await registerValidator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return Failure<RegisterResponse>(
                MoteeStatusCodes.InvalidRequest,
                string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        RegisterTenantResult result = await registration.RegisterAsync(request, cancellationToken);

        if (!result.Succeeded)
        {
            return Failure<RegisterResponse>(
                result.IsConflict ? MoteeStatusCodes.Conflict : MoteeStatusCodes.InvalidRequest,
                string.Join(" ", result.Errors));
        }

        // Nothing was created, so there is no account to send a code for. The owner has
        // already been emailed to say the address is in use; from here the two paths
        // must be indistinguishable to the caller.
        if (!result.AlreadyRegistered)
        {
            // Issued after the transaction commits — sending mail inside it would hold
            // the transaction open across a network call.
            await otp.IssueAsync(result.UserId, OtpPurpose.EmailVerification, cancellationToken);
        }

        // One response, one message, both cases. "Account created" would have given the
        // answer away in the wording even with an identical body.
        return CreatedEnvelope(
            new RegisterResponse
            {
                Email = request.Email.Trim(),
                VerificationRequired = true,
            },
            "Check your email for a 6-digit code.");
    }

    [HttpPost("verify-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyOtp(
        VerifyOtpApiRequest request,
        CancellationToken cancellationToken)
    {
        Guid? userId = await users.FindIdByEmailAsync(request.Email, cancellationToken);

        // An unknown address answers exactly as a known one with a wrong code does.
        // Anything else would turn this endpoint into the enumeration oracle that
        // register no longer is.
        if (userId is null)
        {
            return Failure<LoginResponse>(
                MoteeStatusCodes.OtpIncorrect, "That code is not correct.");
        }

        OtpAttemptOutcome outcome = await otp.VerifyAsync(
            userId.Value, OtpPurpose.EmailVerification, request.Code, cancellationToken);

        if (outcome == OtpAttemptOutcome.Verified)
        {
            // Signed in on the spot. Verifying the code proves control of the mailbox,
            // and the password was set moments earlier during sign-up, so sending the
            // user back to a login screen asks them to re-prove what is already known.
            IssuedSession? session = await sessionIssuer.IssueAsync(
                userId.Value, requestContext.IpAddress, cancellationToken);

            if (session is null)
            {
                return Failure<LoginResponse>(
                    MoteeStatusCodes.InternalServerError, "Verification failed.");
            }

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
                "Email verified.");
        }

        return outcome switch
        {
            OtpAttemptOutcome.IncorrectCode =>
                Failure<LoginResponse>(MoteeStatusCodes.OtpIncorrect, "That code is not correct."),
            OtpAttemptOutcome.Expired =>
                Failure<LoginResponse>(MoteeStatusCodes.OtpExpired, "That code has expired. Request a new one."),
            OtpAttemptOutcome.LockedOut =>
                Failure<LoginResponse>(MoteeStatusCodes.OtpLockedOut, "Too many attempts. Request a new code."),
            OtpAttemptOutcome.AlreadyUsed =>
                Failure<LoginResponse>(MoteeStatusCodes.OtpAlreadyUsed, "That code has already been used."),
            _ => Failure<LoginResponse>(MoteeStatusCodes.InternalServerError, "Verification failed."),
        };
    }

    [HttpPost("resend-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> ResendOtp(
        ResendOtpApiRequest request,
        CancellationToken cancellationToken)
    {
        Guid? userId = await users.FindIdByEmailAsync(request.Email, cancellationToken);

        // An unknown address gets the same answer as a known one. Saying "no such
        // account" here would hand back exactly what register refuses to disclose.
        if (userId is null)
        {
            return Ok<object?>(null, "A new code is on its way.");
        }

        OtpIssueResult result = await otp.IssueAsync(
            userId.Value, OtpPurpose.EmailVerification, cancellationToken);

        if (!result.Sent)
        {
            Response.Headers.RetryAfter = ((int)Math.Ceiling(result.RetryAfter.TotalSeconds))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);

            return Failure<object?>(
                MoteeStatusCodes.TooManyRequests,
                $"A code was sent recently. Try again in {result.RetryAfter.TotalSeconds:0} seconds.");
        }

        return Ok<object?>(null, "A new code is on its way.");
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(
        LoginApiRequest request,
        CancellationToken cancellationToken)
    {
        LoginResult result = await login.LoginAsync(
            new LoginRequest
            {
                Email = request.Email,
                Password = request.Password,
                IpAddress = requestContext.IpAddress,
            },
            cancellationToken);

        switch (result.Outcome)
        {
            case LoginOutcome.Succeeded:
                return Ok(new LoginResponse
                {
                    AccessToken = result.AccessToken!.Value,
                    ExpiresAt = result.AccessToken.ExpiresAt,
                    RefreshToken = result.RefreshToken?.Value,
                    RefreshTokenExpiresAt = result.RefreshToken?.ExpiresAt,
                    UserId = result.UserId,
                    TenantId = result.TenantId,
                    OnboardingCompleted = result.OnboardingCompleted,
                });

            case LoginOutcome.EmailNotConfirmed:
                return Failure<LoginResponse>(
                    MoteeStatusCodes.EmailNotConfirmed, "Verify your email before signing in.");

            case LoginOutcome.LockedOut:
                return Failure<LoginResponse>(
                    MoteeStatusCodes.TooManyRequests, "Too many failed attempts. Try again later.");

            default:
                return Failure<LoginResponse>(
                    MoteeStatusCodes.Unauthorized, "Incorrect email or password.");
        }
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitSetup.ExpensiveAuthPolicy)]
    public async Task<IActionResult> ForgotPassword(
        ForgotPasswordApiRequest request,
        CancellationToken cancellationToken)
    {
        await passwordReset.RequestAsync(request.Email, cancellationToken);

        // The same answer whether or not the address exists.
        return Ok<object?>(null, "If that address has an account, a 6-digit code is on its way.");
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(
        ResetPasswordApiRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword)
            || request.NewPassword.Length < RegisterTenantValidator.MinimumPasswordLength)
        {
            return Failure<object?>(
                MoteeStatusCodes.InvalidRequest,
                $"Password must be at least {RegisterTenantValidator.MinimumPasswordLength} characters.");
        }

        PasswordResetOutcome outcome = await passwordReset.ResetAsync(
            request.Email, request.Code, request.NewPassword, cancellationToken);

        return outcome switch
        {
            PasswordResetOutcome.Succeeded =>
                Ok<object?>(null, "Password updated. Sign in with your new password."),
            PasswordResetOutcome.Expired =>
                Failure<object?>(MoteeStatusCodes.OtpExpired, "That code has expired. Request a new one."),
            PasswordResetOutcome.LockedOut =>
                Failure<object?>(MoteeStatusCodes.OtpLockedOut, "Too many attempts. Request a new code."),
            PasswordResetOutcome.AlreadyUsed =>
                Failure<object?>(MoteeStatusCodes.OtpAlreadyUsed, "That code has already been used."),
            PasswordResetOutcome.WeakPassword =>
                Failure<object?>(MoteeStatusCodes.InvalidRequest, "That password is not acceptable."),
            _ => Failure<object?>(MoteeStatusCodes.OtpIncorrect, "That code is not correct."),
        };
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(
        RefreshApiRequest request,
        CancellationToken cancellationToken)
    {
        RefreshResult result = await refreshTokens.RotateAsync(
            request.RefreshToken, requestContext.IpAddress, cancellationToken);

        if (result.Succeeded)
        {
            return Ok(new LoginResponse
            {
                AccessToken = result.AccessToken!.Value,
                ExpiresAt = result.AccessToken.ExpiresAt,
                RefreshToken = result.RefreshToken!.Value,
                RefreshTokenExpiresAt = result.RefreshToken.ExpiresAt,
                UserId = result.UserId,
                TenantId = result.TenantId,
                OnboardingCompleted = result.OnboardingCompleted,
            });
        }

        // Reuse is reported as a plain refusal. Telling the caller their stolen token
        // tripped a leak detector only helps them.
        return Failure<LoginResponse>(
            MoteeStatusCodes.Unauthorized, "Your session has ended. Sign in again.");
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (Guid.TryParse(User.FindFirst(MoteeClaimTypes.Subject)?.Value, out Guid userId))
        {
            await refreshTokens.RevokeAllAsync(userId, cancellationToken);
        }

        return Ok<object?>(null, "Signed out.");
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        string? Claim(string type) => User.FindFirst(type)?.Value;

        // Read live rather than from the token: onboarding completes mid-session, and
        // a 15-minute access token would keep routing the admin back into the wizard.
        Tenant? tenant = currentTenant.TenantId is Guid tenantId
            ? await tenants.FindAsync(tenantId, cancellationToken)
            : null;

        // Resolved from the levels they hold, not from the token. The role claim is a
        // label now — deriving permissions from it returned an empty matrix the moment
        // levels stopped being a single named role, and the frontend would have
        // rendered a UI with every action hidden.
        ResolvedPermissions held = Guid.TryParse(Claim(MoteeClaimTypes.Subject), out Guid userId)
            ? await userPermissions.ForAsync(userId, cancellationToken)
            : ResolvedPermissions.None;

        return Ok(new CurrentUserResponse
        {
            UserId = Claim(MoteeClaimTypes.Subject) ?? string.Empty,
            Email = Claim(MoteeClaimTypes.Email) ?? string.Empty,
            TenantId = Claim(MoteeClaimTypes.TenantId),
            Role = Claim(MoteeClaimTypes.Role),
            EmployeeId = Claim(MoteeClaimTypes.EmployeeId),
            IsPlatformStaff = Claim(MoteeClaimTypes.IsPlatformStaff) == "true",
            OnboardingCompleted = tenant?.OnboardingCompletedAt is not null,
            OnboardingCompletedAt = tenant?.OnboardingCompletedAt,

            // The names behind the merge, so an admin asking why someone can delete
            // records gets an answer rather than a combined matrix.
            AccessLevels = held.LevelNames,

            // The owner bypasses every check, so the UI should not hide anything from
            // them on the strength of an empty matrix.
            IsOwner = held.IsOwner,

            Permissions = held.Permissions.Modules,
            Scope = held.Permissions.Scope,
        });
    }
}
