using Motee.Domain.Notifications;
using Motee.Domain.Settings;

namespace Motee.Application.Settings;

// One person's notification settings.
//
// Per user, not per company: the panel's own copy says "your regional
// preferences", and which notifications somebody wants is not an administrative
// decision. It sits on the HR settings screen all the same, which is worth
// flagging to whoever owns that screen — there is no employee-side settings
// page for it to live on yet.
public interface INotificationPreferenceService
{
    Task<NotificationPreferenceView> MineAsync(CancellationToken cancellationToken = default);

    Task<SettingsResult<NotificationPreferenceView>> SaveMineAsync(
        NotificationPreferenceRequest request,
        CancellationToken cancellationToken = default);

    // Read by the notification service before it writes a row, which is the
    // only thing that makes any of these switches real.
    Task<bool> AllowsAsync(
        Guid userId,
        NotificationKind kind,
        CancellationToken cancellationToken = default);
}

public sealed record NotificationPreferenceView
{
    public required IReadOnlyList<NotificationGroupRow> Groups { get; init; }

    public required DigestFrequency DigestFrequency { get; init; }

    public string? TimeZoneId { get; init; }

    public string? Language { get; init; }

    // Which channels actually deliver, and why the others do not. The panel
    // renders three columns of switches; two of them had nothing behind them.
    public required IReadOnlyList<ChannelRow> Channels { get; init; }
}

public sealed record NotificationGroupRow
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required bool Email { get; init; }

    public required bool InApp { get; init; }

    public required bool Push { get; init; }

    // What switching this off stops. Sent so somebody can see that
    // "Performance" covers overdue reviews and overdue goals before they
    // silence it.
    public required IReadOnlyList<string> Covers { get; init; }
}

public sealed record ChannelRow
{
    public required NotificationChannel Channel { get; init; }

    public required ChannelAvailability Availability { get; init; }

    public required string Reason { get; init; }
}

public sealed record NotificationPreferenceRequest
{
    public required IReadOnlyList<NotificationGroupChoice> Groups { get; init; }

    public DigestFrequency DigestFrequency { get; init; } = DigestFrequency.Daily;

    public string? TimeZoneId { get; init; }

    public string? Language { get; init; }
}

public sealed record NotificationGroupChoice
{
    public required string Id { get; init; }

    public bool Email { get; init; }

    public bool InApp { get; init; } = true;

    public bool Push { get; init; }
}
