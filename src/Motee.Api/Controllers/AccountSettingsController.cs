using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Motee.Api.Contracts;
using Motee.Api.Http;
using Motee.Application.Settings;

namespace Motee.Api.Controllers;

// A person's own settings: which notifications reach them, which devices they
// are signed in on, and what has happened on their account.
//
// No module grant on anything here. All of it is the caller's own, and needing
// admin.settings to see your own sign-ins would mean nobody but an
// administrator could check whether somebody else had been in their account.
// The HR settings screen is simply where these panels happen to be drawn; there
// is no employee-side settings page for them yet, which is worth raising with
// whoever owns that screen.
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/settings/me")]
[Authorize]
public class AccountSettingsController(
    INotificationPreferenceService preferences,
    ISecuritySettingsService security) : ApiControllerBase
{
    [HttpGet("notifications")]
    [ProducesResponseType<NotificationPreferenceView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Notifications(CancellationToken cancellationToken) =>
        Ok(await preferences.MineAsync(cancellationToken));

    [HttpPut("notifications")]
    [ProducesResponseType<NotificationPreferenceView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SaveNotifications(
        NotificationPreferenceRequest request,
        CancellationToken cancellationToken)
    {
        SettingsResult<NotificationPreferenceView> result =
            await preferences.SaveMineAsync(request, cancellationToken);

        return result.Succeeded
            ? Ok(result.Value!, "Notification preferences saved.")
            : Failure<NotificationPreferenceView>(
                SettingsController.StatusFor(result.Outcome), MessageFor(result.Refusal));
    }

    // The screen calls these trusted devices. They are live sessions, which is
    // what makes revoking one mean anything.
    [HttpGet("sessions")]
    [ProducesResponseType<IReadOnlyList<DeviceSessionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Sessions(CancellationToken cancellationToken) =>
        Ok(await security.SessionsAsync(cancellationToken));

    [HttpDelete("sessions/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RevokeSession(
        Guid id,
        CancellationToken cancellationToken)
    {
        SettingsOutcome outcome = await security.RevokeSessionAsync(id, cancellationToken);

        return outcome == SettingsOutcome.Succeeded
            ? Ok<object?>(null, "Device signed out.")
            : Failure<object?>(MoteeStatusCodes.NotFound, "No such device.");
    }

    [HttpGet("activity")]
    [ProducesResponseType<IReadOnlyList<AccountEventDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Activity(
        [FromQuery] int take = 25,
        CancellationToken cancellationToken = default) =>
        Ok(await security.ActivityAsync(take, cancellationToken));

    private static string MessageFor(SettingsRefusal refusal) => refusal switch
    {
        SettingsRefusal.UnknownTimeZone => "That is not a time zone we recognise.",
        SettingsRefusal.UnknownLanguage => "That is not a language we offer.",
        _ => "That change was not accepted.",
    };
}
