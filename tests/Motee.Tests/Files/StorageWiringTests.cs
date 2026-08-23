using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Exports;
using Motee.Infrastructure;

namespace Motee.Tests.Files;

// Which S3 client gets built, and whether it is pointed at the right place. None of
// this touches a network: signing a URL is arithmetic, so the result can be checked
// exactly without a bucket existing anywhere.
public class StorageWiringTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        settings["ConnectionStrings:Default"] = "Host=localhost;Database=unused";

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<Motee.Application.Tenancy.ICurrentTenant>(new NoTenant());
        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider();
    }

    private sealed class NoTenant : Motee.Application.Tenancy.ICurrentTenant
    {
        public Guid? TenantId => null;
    }

    // The whole point of the fallback: with no bucket, the AWS client is never built,
    // so an environment with no credentials still serves every request that is not
    // about files. Before this, one missing key took down employees and join too.
    [Fact]
    public void WithNoBucketNothingTriesToReachAws()
    {
        using ServiceProvider provider = Build([]);
        using IServiceScope scope = provider.CreateScope();

        IFileStorage storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => storage.GetDownloadUrlAsync("some/key", TimeSpan.FromMinutes(5))
                .GetAwaiter().GetResult());

        Assert.Contains("not configured", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Deleting something that was never stored is not worth failing over.
    [Fact]
    public async Task DeletingWithoutStorageIsNotAnError()
    {
        using ServiceProvider provider = Build([]);
        using IServiceScope scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IFileStorage>().DeleteAsync("gone");
    }

    [Fact]
    public void ACustomEndpointIsUsedWithPathStyleAddressing()
    {
        using ServiceProvider provider = Build(new Dictionary<string, string?>
        {
            ["Storage:Bucket"] = "motee-dev",
            ["Storage:ServiceUrl"] = "https://abc123.r2.cloudflarestorage.com",
            ["Storage:AccessKey"] = "key",
            ["Storage:SecretKey"] = "secret",
        });

        AmazonS3Config config = (AmazonS3Config)provider
            .GetRequiredService<IAmazonS3>()
            .Config;

        Assert.Equal("https://abc123.r2.cloudflarestorage.com", config.ServiceURL.TrimEnd('/'));

        // Bucket-as-subdomain only resolves against Amazon's own domains, so anything
        // else has to address the bucket as the first path segment.
        Assert.True(config.ForcePathStyle);

        // Signing needs a region even where the store has none of its own.
        Assert.Equal("auto", config.AuthenticationRegion);
    }

    // The link is what the frontend and the export email actually use, so it is
    // checked rather than inferred from the configuration.
    [Fact]
    public async Task ASignedLinkPointsAtTheCustomEndpoint()
    {
        using ServiceProvider provider = Build(new Dictionary<string, string?>
        {
            ["Storage:Bucket"] = "motee-dev",
            ["Storage:ServiceUrl"] = "https://abc123.r2.cloudflarestorage.com",
            ["Storage:AccessKey"] = "key",
            ["Storage:SecretKey"] = "secret",
        });

        IAmazonS3 s3 = provider.GetRequiredService<IAmazonS3>();

        string url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = "motee-dev",
            Key = "tenant/avatars/photo.png",
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddHours(1),
        });

        Assert.StartsWith(
            "https://abc123.r2.cloudflarestorage.com/motee-dev/tenant/avatars/photo.png",
            url,
            StringComparison.Ordinal);

        Assert.Contains("X-Amz-Signature=", url, StringComparison.Ordinal);
    }

    // The AWS credential chain — environment, profile, instance metadata — means
    // nothing to Cloudflare. Failing at startup with the reason beats failing later
    // with an authentication error nobody can place.
    [Fact]
    public void ACustomEndpointWithoutKeysSaysWhatIsMissing()
    {
        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() =>
            Build(new Dictionary<string, string?>
            {
                ["Storage:Bucket"] = "motee-dev",
                ["Storage:ServiceUrl"] = "https://abc123.r2.cloudflarestorage.com",
            }));

        Assert.Contains("Storage:AccessKey", missing.Message, StringComparison.Ordinal);
    }
}
