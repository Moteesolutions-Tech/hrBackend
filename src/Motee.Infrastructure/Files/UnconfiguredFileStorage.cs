using Microsoft.Extensions.Logging;
using Motee.Application.Exports;

namespace Motee.Infrastructure.Files;

// Stands in when no bucket is configured, so an environment without object storage
// still runs.
//
// The alternative was worse than it looks. Constructing the AWS client resolves
// credentials eagerly, and file storage is now a dependency of the employee service
// (avatar links) and the invitation service (the joiner's photo). So with no
// credentials present, every employee and join request failed with an unhandled AWS
// error — endpoints that have nothing to do with files.
//
// Now the failure is confined to the operations that genuinely need storage, and it
// says what is actually wrong. Mirrors how an unconfigured email provider drops
// messages with a warning rather than taking the API down.
internal sealed class UnconfiguredFileStorage(ILogger<UnconfiguredFileStorage> logger) : IFileStorage
{
    private const string Explanation =
        "File storage is not configured. Set Storage:Bucket, and the AWS credentials or "
        + "R2 keys for it, before uploading or exporting.";

    public Task SaveAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default) =>
        Refuse(nameof(SaveAsync), key);

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
        Refuse<Stream?>(nameof(OpenReadAsync), key);

    public Task<string> GetDownloadUrlAsync(
        string key,
        TimeSpan validFor,
        string? downloadFileName = null,
        CancellationToken cancellationToken = default) =>
        Refuse<string>(nameof(GetDownloadUrlAsync), key);

    // Deleting something that was never stored is not a problem worth raising.
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private Task Refuse(string operation, string key) => Refuse<object>(operation, key);

    private Task<T> Refuse<T>(string operation, string key)
    {
        logger.LogError("{Operation} on {Key} was refused. {Explanation}", operation, key, Explanation);

        throw new InvalidOperationException(Explanation);
    }
}
