namespace Motee.Application.Exports;

// Object storage. S3 in a deployed environment; a local folder in development so the
// flow works without AWS credentials.
public interface IFileStorage
{
    Task SaveAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    // A time-limited link the recipient can open directly, without the API in the
    // path. Unauthenticated by nature: whoever holds the URL can fetch the object
    // until it expires, so the window is kept short.
    Task<string> GetDownloadUrlAsync(
        string key,
        TimeSpan validFor,
        string? downloadFileName = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

// Hands an export to the background worker. Separate from IExportRunner so the
// enqueue side never accidentally runs the job on the request thread in production.
public interface IExportQueue
{
    void Enqueue(Guid exportId);
}
