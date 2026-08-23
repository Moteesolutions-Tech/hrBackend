using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Motee.Application.Exports;

namespace Motee.Infrastructure.Exports;

internal sealed class S3FileStorage(IAmazonS3 s3, IConfiguration configuration) : IFileStorage
{
    // The only storage there is — there is no local fallback, so an unset bucket is a
    // misconfiguration rather than a mode.
    private string Bucket =>
        configuration["Storage:Bucket"]
        ?? throw new InvalidOperationException(
            "Storage:Bucket is not configured. Uploads and exports need an S3 bucket, "
            + "including in development.");

    // Set only when talking to S3 itself. R2 and the other S3-compatible stores
    // encrypt at rest unconditionally and reject the header as unsupported, so asking
    // for encryption there fails the upload while changing nothing about the outcome.
    private bool IsAmazon => string.IsNullOrWhiteSpace(configuration["Storage:ServiceUrl"]);

    public async Task SaveAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default) =>
        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = Bucket,
                Key = key,
                InputStream = content,
                ContentType = contentType,

                // Exports hold personal data. Encrypted at rest, and never public —
                // the file is reached through the API, which checks permissions.
                ServerSideEncryptionMethod = IsAmazon
                    ? ServerSideEncryptionMethod.AES256
                    : null,
            },
            cancellationToken);

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            GetObjectResponse response = await s3.GetObjectAsync(
                Bucket, key, cancellationToken);

            return response.ResponseStream;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Expired and swept, or never written. The caller reports it as gone.
            return null;
        }
    }

    public Task<string> GetDownloadUrlAsync(
        string key,
        TimeSpan validFor,
        string? downloadFileName = null,
        CancellationToken cancellationToken = default)
    {
        GetPreSignedUrlRequest request = new()
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(validFor),
        };

        // Without this the browser saves the object key's last segment. With it the
        // file arrives named as the user expects, and as an attachment rather than
        // something the browser tries to render.
        if (!string.IsNullOrWhiteSpace(downloadFileName))
        {
            request.ResponseHeaderOverrides.ContentDisposition =
                $"attachment; filename=\"{downloadFileName}\"";
        }

        return s3.GetPreSignedURLAsync(request);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        s3.DeleteObjectAsync(Bucket, key, cancellationToken);
}
