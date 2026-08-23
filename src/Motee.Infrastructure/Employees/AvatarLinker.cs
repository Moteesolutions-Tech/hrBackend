using Microsoft.EntityFrameworkCore;
using Motee.Application.Employees;
using Motee.Application.Exports;
using Motee.Infrastructure.Persistence;

namespace Motee.Infrastructure.Employees;

// Turns a stored avatar file id into a link the frontend can put straight into an
// <img>. Minted on read rather than stored: the bucket is private, so the only URLs
// that work are signed ones, and S3 caps a signature at seven days. A URL saved in
// the database is a broken image with a delay on it.
internal sealed class AvatarLinker(MoteeDbContext dbContext, IFileStorage storage)
{
    // Long enough to open a profile, walk away, and come back to a page that still
    // renders. Short enough that a link copied out of devtools is not a lasting way
    // to see someone's photo.
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    // Signing is local arithmetic, not a call to S3, so a page of rows costs nothing
    // in round trips. The keys are fetched in one query rather than per row.
    public async Task<IReadOnlyDictionary<Guid, string>> UrlsForAsync(
        IEnumerable<Guid?> fileIds,
        CancellationToken cancellationToken = default)
    {
        List<Guid> wanted = [.. fileIds.OfType<Guid>().Distinct()];

        if (wanted.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        List<(Guid Id, string Key)> keys = await dbContext.StoredFiles
            .AsNoTracking()
            .Where(file => wanted.Contains(file.Id))
            .Select(file => new ValueTuple<Guid, string>(file.Id, file.StorageKey))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, string> urls = new(keys.Count);

        foreach ((Guid id, string key) in keys)
        {
            // No download filename: an avatar is rendered in place, and a
            // Content-Disposition of attachment would make the browser save it.
            urls[id] = await storage.GetDownloadUrlAsync(
                key, Lifetime, downloadFileName: null, cancellationToken);
        }

        return urls;
    }
}
