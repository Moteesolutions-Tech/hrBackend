using Motee.Domain.Common;
using Motee.Domain.Notifications;

namespace Motee.Domain.Settings;

// Which notifications reach somebody, and on which channel.
//
// NotificationKind's own comment says the kind is "what preferences,
// deduplication and 'stop telling me about this' all hang off". Deduplication
// was built; preferences were not, so every switch on the notifications panel
// was decoration and the bell told everybody everything.
//
// Grouped rather than one row per kind. The panel's rows are modules, a person
// thinks "stop telling me about documents" rather than about
// AcknowledgementOutstanding, and a new kind joins an existing group without
// needing a preference nobody has set.
public enum NotificationChannel
{
    InApp,
    Email,
    Push,
}

public enum ChannelAvailability
{
    // Delivers today.
    Available,

    // The product can send email, but notifications do not use it yet: the
    // nightly sweep writes rows and nothing turns them into messages. Reported
    // so the screen can say so rather than showing a switch that silently does
    // nothing.
    NotWired,

    // No delivery mechanism exists at all. There is no device registration, no
    // push certificate and nothing to send to.
    Unavailable,
}

public sealed record NotificationGroup
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required IReadOnlyList<NotificationKind> Kinds { get; init; }

    // On by default, except where a group is noisy enough that the sensible
    // starting point is off.
    public bool EmailByDefault { get; init; }
}

public static class NotificationGroups
{
    public static readonly IReadOnlyList<NotificationGroup> All =
    [
        new()
        {
            Key = "documents",
            Label = "Documents & Contracts",
            Kinds =
            [
                NotificationKind.ContractExpiring,
                NotificationKind.DocumentExpiring,
                NotificationKind.AcknowledgementOutstanding,
            ],
            EmailByDefault = true,
        },
        new()
        {
            Key = "attendance",
            Label = "Attendance & Timesheets",
            Kinds = [NotificationKind.TimesheetDue, NotificationKind.OpenClockSession],
            EmailByDefault = true,
        },
        new()
        {
            Key = "performance",
            Label = "Performance",
            Kinds = [NotificationKind.ReviewOverdue, NotificationKind.GoalOverdue],
        },

        // Its own group rather than folded into another, because this is the
        // one kind somebody asked for personally. Burying a reminder you set
        // yourself inside a group you silenced for other reasons is how a
        // reminder goes missing.
        new()
        {
            Key = "reminders",
            Label = "My Reminders",
            Kinds = [NotificationKind.NoteReminderDue],
            EmailByDefault = true,
        },
    ];

    private static readonly IReadOnlyDictionary<NotificationKind, string> GroupByKind =
        BuildGroups();

    public static readonly IReadOnlyDictionary<NotificationChannel, ChannelAvailability> Channels =
        new Dictionary<NotificationChannel, ChannelAvailability>
        {
            [NotificationChannel.InApp] = ChannelAvailability.Available,
            [NotificationChannel.Email] = ChannelAvailability.NotWired,
            [NotificationChannel.Push] = ChannelAvailability.Unavailable,
        };

    public static NotificationGroup? Find(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(group =>
                string.Equals(group.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    // Which group a kind belongs to. Every kind belongs to exactly one, checked
    // on start-up: a kind in no group would be a notification nobody can turn
    // off, and one in two groups would be a switch that half works.
    public static string GroupOf(NotificationKind kind) => GroupByKind[kind];

    private static IReadOnlyDictionary<NotificationKind, string> BuildGroups()
    {
        Dictionary<NotificationKind, string> map = [];

        foreach (NotificationGroup group in All)
        {
            foreach (NotificationKind kind in group.Kinds)
            {
                if (map.TryGetValue(kind, out string? existing))
                {
                    throw new InvalidOperationException(
                        $"Notification kind '{kind}' is in both '{existing}' and '{group.Key}'.");
                }

                map[kind] = group.Key;
            }
        }

        foreach (NotificationKind kind in Enum.GetValues<NotificationKind>())
        {
            if (!map.ContainsKey(kind))
            {
                throw new InvalidOperationException(
                    $"Notification kind '{kind}' belongs to no group, so nobody could turn it off.");
            }
        }

        return map;
    }
}

// The interface languages the screen offers.
//
// The two halves of the frontend disagree about what they are: the type allows
// en, fr, es, pt and ar, and the dropdown lists en, fr, pt, ar and sw. So
// picking Kiswahili sets a value the type does not permit, and Spanish is
// permitted and never offered. The union is accepted here so neither half
// silently fails to save, and Translated says which of them the product can
// actually render — which is one.
public sealed record LanguageOption
{
    public required string Code { get; init; }

    public required string Label { get; init; }

    public bool Translated { get; init; }
}

public static class Languages
{
    public static readonly IReadOnlyList<LanguageOption> All =
    [
        new() { Code = "en", Label = "English", Translated = true },
        new() { Code = "fr", Label = "Français" },
        new() { Code = "es", Label = "Español" },
        new() { Code = "pt", Label = "Português" },
        new() { Code = "ar", Label = "العربية" },
        new() { Code = "sw", Label = "Kiswahili" },
    ];

    public const string Default = "en";

    public static bool IsKnown(string? code) =>
        string.IsNullOrWhiteSpace(code)
            || All.Any(option => string.Equals(option.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string? Normalise(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : All.FirstOrDefault(option =>
                string.Equals(option.Code, code.Trim(), StringComparison.OrdinalIgnoreCase))?.Code;
}

public enum DigestFrequency
{
    // Each notification as it is raised.
    Realtime,

    Daily,

    Weekly,
}

// One person's notification settings. One row per user, read and written whole
// because that is how the panel saves: every switch at once, behind one button.
public class NotificationPreference : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    // group key -> channels. jsonb, because the shape is the panel's grid and
    // nothing queries by cell.
    public Dictionary<string, ChannelChoice> Groups { get; set; } = [];

    public DigestFrequency DigestFrequency { get; set; } = DigestFrequency.Daily;

    // The person's own zone and language, distinct from the company's
    // TenantSettings.TimeZoneId. Attendance files a punch against the company
    // clock whatever this says; this is what a digest is timed by and what the
    // interface is rendered in.
    public string? TimeZoneId { get; set; }

    public string? Language { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record ChannelChoice
{
    public bool InApp { get; init; } = true;

    public bool Email { get; init; }

    public bool Push { get; init; }
}

public static class NotificationPreferenceRules
{
    // Only what the product can deliver. Switching on a channel that does not
    // deliver stores a true that means nothing, and the next person to read the
    // row believes somebody is being emailed.
    public static ChannelChoice Possible(ChannelChoice choice) => new()
    {
        InApp = choice.InApp && Delivers(NotificationChannel.InApp),
        Email = choice.Email && Delivers(NotificationChannel.Email),
        Push = choice.Push && Delivers(NotificationChannel.Push),
    };

    public static bool Delivers(NotificationChannel channel) =>
        NotificationGroups.Channels[channel] == ChannelAvailability.Available;

    public static ChannelChoice DefaultFor(NotificationGroup group) => Possible(new ChannelChoice
    {
        InApp = true,
        Email = group.EmailByDefault,
    });

    // What somebody who has never opened the panel gets. Written out rather than
    // stored on first read: a row created by looking at a screen is a row that
    // disagrees with the defaults as soon as the defaults change.
    public static NotificationPreference Defaults() => new()
    {
        Groups = NotificationGroups.All.ToDictionary(
            group => group.Key,
            DefaultFor,
            StringComparer.Ordinal),
    };

    // Whether a notification of this kind should reach this person in the bell.
    //
    // Absent preferences mean the default, not silence. Somebody who has never
    // opened the panel still has to hear that their contract is lapsing.
    public static bool AllowsInApp(NotificationPreference? preference, NotificationKind kind)
    {
        string groupKey = NotificationGroups.GroupOf(kind);

        bool byDefault = NotificationGroups.Find(groupKey) is NotificationGroup group
            && DefaultFor(group).InApp;

        if (preference is null)
        {
            return byDefault;
        }

        return preference.Groups.TryGetValue(groupKey, out ChannelChoice? choice)
            ? choice.InApp
            : byDefault;
    }

    // An IANA zone or nothing. A stored "GMT+1" or a typo would throw wherever
    // it is first used, which is a background digest rather than the screen that
    // set it.
    public static bool IsKnownZone(string? zoneId)
    {
        if (string.IsNullOrWhiteSpace(zoneId))
        {
            return true;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(zoneId.Trim(), out _);
    }
}
