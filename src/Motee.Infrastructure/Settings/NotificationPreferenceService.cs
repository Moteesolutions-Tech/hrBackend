using Microsoft.EntityFrameworkCore;
using Motee.Application.Common;
using Motee.Application.Settings;
using Motee.Domain.Notifications;
using Motee.Domain.Settings;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Settings;

// One person's notification settings.
//
// AllowsAsync is what makes any of these switches real: the notification
// service asks it before it writes a row. Without that the panel was ten rows
// of switches and the bell told everybody everything.
internal sealed class NotificationPreferenceService(
    MoteeDbContext dbContext,
    IRequestContext requestContext,
    TimeProvider timeProvider) : INotificationPreferenceService
{
    public async Task<NotificationPreferenceView> MineAsync(
        CancellationToken cancellationToken = default)
    {
        if (UserId() is not Guid userId)
        {
            return Describe(NotificationPreferenceRules.Defaults());
        }

        NotificationPreference? saved = await dbContext.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.UserId == userId, cancellationToken);

        // The defaults rather than a row written on first read. A row created by
        // looking at a screen is a row that disagrees with the defaults as soon
        // as the defaults change.
        return Describe(saved ?? NotificationPreferenceRules.Defaults());
    }

    public async Task<SettingsResult<NotificationPreferenceView>> SaveMineAsync(
        NotificationPreferenceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (UserId() is not Guid userId)
        {
            return SettingsResult<NotificationPreferenceView>.Failed(SettingsOutcome.NotFound);
        }

        if (!NotificationPreferenceRules.IsKnownZone(request.TimeZoneId))
        {
            return SettingsResult<NotificationPreferenceView>.Refused(
                SettingsRefusal.UnknownTimeZone);
        }

        if (!Languages.IsKnown(request.Language))
        {
            return SettingsResult<NotificationPreferenceView>.Refused(
                SettingsRefusal.UnknownLanguage);
        }

        NotificationPreference? row = await dbContext.NotificationPreferences
            .FirstOrDefaultAsync(saved => saved.UserId == userId, cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow();

        if (row is null)
        {
            row = new NotificationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            };

            dbContext.NotificationPreferences.Add(row);
        }

        // Built whole and assigned, never mutated in place: the column's
        // comparer serialises, so changing the dictionary's contents leaves EF
        // seeing no change and the save does nothing.
        Dictionary<string, ChannelChoice> groups = new(StringComparer.Ordinal);

        foreach (NotificationGroup group in NotificationGroups.All)
        {
            NotificationGroupChoice? asked = request.Groups.FirstOrDefault(choice =>
                string.Equals(choice.Id, group.Key, StringComparison.OrdinalIgnoreCase));

            // A group the payload did not mention keeps whatever was saved, and
            // falls back to the default where nothing was. A client posting one
            // row should not silence the groups it left out.
            groups[group.Key] = asked is null
                ? row.Groups.TryGetValue(group.Key, out ChannelChoice? held)
                    ? held
                    : NotificationPreferenceRules.DefaultFor(group)
                : NotificationPreferenceRules.Possible(new ChannelChoice
                {
                    InApp = asked.InApp,
                    Email = asked.Email,
                    Push = asked.Push,
                });
        }

        row.Groups = groups;
        row.DigestFrequency = request.DigestFrequency;
        row.TimeZoneId = Trimmed(request.TimeZoneId);
        row.Language = Languages.Normalise(request.Language);
        row.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);

        return SettingsResult<NotificationPreferenceView>.Ok(Describe(row));
    }

    public async Task<bool> AllowsAsync(
        Guid userId,
        NotificationKind kind,
        CancellationToken cancellationToken = default)
    {
        // Filters ignored: this is called by the nightly sweep, which belongs to
        // no tenant, and the user id is the constraint. A preference always
        // belongs to the same tenant as the user holding it.
        NotificationPreference? saved = await dbContext.NotificationPreferences
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.UserId == userId, cancellationToken);

        return NotificationPreferenceRules.AllowsInApp(saved, kind);
    }

    private static NotificationPreferenceView Describe(NotificationPreference preference) => new()
    {
        Groups =
        [
            .. NotificationGroups.All.Select(group =>
            {
                ChannelChoice choice = preference.Groups.TryGetValue(group.Key, out ChannelChoice? held)
                    ? held
                    : NotificationPreferenceRules.DefaultFor(group);

                return new NotificationGroupRow
                {
                    Id = group.Key,
                    Label = group.Label,
                    InApp = choice.InApp,
                    Email = choice.Email,
                    Push = choice.Push,
                    Covers = [.. group.Kinds.Select(kind => kind.ToString())],
                };
            }),
        ],
        DigestFrequency = preference.DigestFrequency,
        TimeZoneId = preference.TimeZoneId,
        Language = preference.Language,
        Channels =
        [
            .. NotificationGroups.Channels.Select(entry => new ChannelRow
            {
                Channel = entry.Key,
                Availability = entry.Value,
                Reason = Reason(entry.Key, entry.Value),
            }),
        ],
    };

    private static string Reason(NotificationChannel channel, ChannelAvailability availability) =>
        availability switch
        {
            ChannelAvailability.Available => "Delivers now.",
            ChannelAvailability.NotWired when channel == NotificationChannel.Email =>
                "Email is sent for invitations and password resets, but notifications "
                + "are not emailed yet.",
            ChannelAvailability.NotWired => "Not carrying notifications yet.",
            _ => "There is no push delivery in this version.",
        };

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Guid? UserId() =>
        Guid.TryParse(requestContext.UserId, out Guid userId) ? userId : null;
}
