using Microsoft.EntityFrameworkCore;
using Motee.Application.Exports;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Approvals;

// Turns an attachment's stored file id into a link an approver can open.
//
// Separate from AvatarLinker rather than shared with it, because the two want opposite
// things from the same call. An avatar is rendered in place, so it is signed with no
// download name; an attachment is a fit note or a signed contract somebody needs to
// keep, so it carries its real filename and the browser saves it under that.
internal sealed class ApprovalAttachmentLinker(MoteeDbContext dbContext, IFileStorage storage)
{
    // Longer than an avatar's hour. Reviewing a request means opening the evidence,
    // reading it, and often coming back to it — a link that expires mid-decision is a
    // page somebody has to reload for no reason they can see.
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);

    // One query for the whole set, then local signing arithmetic — an approval with six
    // attachments costs one round trip, not six.
    public async Task<IReadOnlyDictionary<Guid, string>> UrlsForAsync(
        IEnumerable<Guid> fileIds,
        CancellationToken cancellationToken = default)
    {
        List<Guid> wanted = [.. fileIds.Distinct()];

        if (wanted.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        List<(Guid Id, string Key, string Name)> files = await dbContext.StoredFiles
            .AsNoTracking()
            .Where(file => wanted.Contains(file.Id))
            .Select(file => new ValueTuple<Guid, string, string>(
                file.Id, file.StorageKey, file.FileName))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, string> urls = new(files.Count);

        foreach ((Guid id, string key, string name) in files)
        {
            urls[id] = await storage.GetDownloadUrlAsync(key, Lifetime, name, cancellationToken);
        }

        return urls;
    }
}
