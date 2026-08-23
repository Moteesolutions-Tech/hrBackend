using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Motee.Application.Files;
using Motee.Domain.Common;
using Motee.Domain.Files;
using Motee.Domain.Tenants;
using Motee.Infrastructure.Persistence;

namespace Motee.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class FileUploadTests(PostgresFixture fixture)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34];
    private static readonly byte[] Executable = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00];

    private async Task<(Guid TenantId, ServiceProvider Provider)> ArrangeAsync()
    {
        await fixture.ResetAsync();

        Guid tenantId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Acme",
                Slug = $"acme-{tenantId:N}",
                CountryCode = CountryCode.Nigeria,
            });

            await seed.SaveChangesAsync();
        }

        ServiceProvider provider = fixture.BuildProvider();
        provider.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = tenantId;

        return (tenantId, provider);
    }

    private static IFileUploadService Files(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<IFileUploadService>();

    private static FileUpload Upload(
        byte[] content,
        FilePurpose purpose = FilePurpose.CompanyLogo,
        string fileName = "logo.png",
        string contentType = "image/png",
        Guid? ownerId = null) => new()
    {
        Purpose = purpose,
        OwnerId = ownerId,
        FileName = fileName,
        ContentType = contentType,
        Content = new MemoryStream(content),
        SizeBytes = content.Length,
    };

    [SkippableFact]
    public async Task StoresAFileAndReadsItBack()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        FileUploadResult result = await Files(owned).UploadAsync(Upload(Png));

        Assert.True(result.Succeeded, result.Rejection.ToString());
        Assert.Equal("logo.png", result.File!.FileName);
        Assert.Equal(Png.Length, result.File.SizeBytes);

        FileContent? content = await Files(owned).OpenAsync(result.File.Id);

        using MemoryStream buffer = new();
        await content!.Content.CopyToAsync(buffer);

        Assert.Equal(Png, buffer.ToArray());
    }

    // A hostile name must not reach the storage path.
    [SkippableFact]
    public async Task TheStorageKeyIsGeneratedRatherThanTakenFromTheName()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (Guid tenantId, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        FileUploadResult result = await Files(owned)
            .UploadAsync(Upload(Png, fileName: "../../etc/passwd.png"));

        Assert.True(result.Succeeded);

        PostgresFixture.InMemoryFileStorage storage =
            owned.GetRequiredService<PostgresFixture.InMemoryFileStorage>();

        string key = Assert.Single(storage.Keys);

        Assert.StartsWith($"{tenantId:N}/logos/", key, StringComparison.Ordinal);
        Assert.DoesNotContain("..", key, StringComparison.Ordinal);
        Assert.DoesNotContain("passwd", key, StringComparison.Ordinal);
    }

    // Renaming an executable and declaring it an image must not get it stored.
    [SkippableFact]
    public async Task RefusesBytesThatContradictTheDeclaredType()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        FileUploadResult result = await Files(owned).UploadAsync(Upload(Executable));

        Assert.Equal(FileRejection.ContentDoesNotMatchType, result.Rejection);

        Assert.Empty(owned.GetRequiredService<PostgresFixture.InMemoryFileStorage>().Keys);
    }

    [SkippableFact]
    public async Task RefusesATypeThePurposeDoesNotAllow()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        FileUploadResult result = await Files(owned).UploadAsync(
            Upload(Pdf, FilePurpose.EmployeeAvatar, "cv.pdf", "application/pdf"));

        Assert.Equal(FileRejection.DisallowedType, result.Rejection);
    }

    [SkippableFact]
    public async Task ADocumentAcceptsAPdf()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        FileUploadResult result = await Files(owned).UploadAsync(
            Upload(Pdf, FilePurpose.EmployeeDocument, "contract.pdf", "application/pdf"));

        Assert.True(result.Succeeded, result.Rejection.ToString());
    }

    [SkippableFact]
    public async Task ListsWhatBelongsToAnOwner()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid employeeId = Guid.NewGuid();

        await Files(owned).UploadAsync(
            Upload(Pdf, FilePurpose.EmployeeDocument, "a.pdf", "application/pdf", employeeId));
        await Files(owned).UploadAsync(
            Upload(Pdf, FilePurpose.EmployeeDocument, "b.pdf", "application/pdf", employeeId));
        await Files(owned).UploadAsync(
            Upload(Pdf, FilePurpose.EmployeeDocument, "c.pdf", "application/pdf", Guid.NewGuid()));

        Assert.Equal(2, (await Files(owned).ListAsync(FilePurpose.EmployeeDocument, employeeId)).Count);
        Assert.Equal(3, (await Files(owned).ListAsync(FilePurpose.EmployeeDocument, null)).Count);
    }

    [SkippableFact]
    public async Task DeletingRemovesBothTheRowAndTheObject()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Files(owned).UploadAsync(Upload(Png))).File!.Id;

        Assert.True(await Files(owned).DeleteAsync(id));
        Assert.Null(await Files(owned).GetAsync(id));
        Assert.Empty(owned.GetRequiredService<PostgresFixture.InMemoryFileStorage>().Keys);
    }

    [SkippableFact]
    public async Task AFileIsNotVisibleToAnotherTenant()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid id = (await Files(owned).UploadAsync(Upload(Png))).File!.Id;

        Guid otherTenantId = Guid.NewGuid();

        await using (MoteeDbContext seed = fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant
            {
                Id = otherTenantId,
                Name = "Globex",
                Slug = $"globex-{otherTenantId:N}",
                CountryCode = CountryCode.UnitedKingdom,
            });

            await seed.SaveChangesAsync();
        }

        owned.GetRequiredService<PostgresFixture.MutableTenant>().TenantId = otherTenantId;

        Assert.Null(await Files(owned).GetAsync(id));
        Assert.Null(await Files(owned).OpenAsync(id));
        Assert.False(await Files(owned).DeleteAsync(id));
    }

    [SkippableFact]
    public async Task RecordsWhoUploadedIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Guid userId = Guid.NewGuid();
        owned.GetRequiredService<PostgresFixture.StubRequestContext>().UserId = userId.ToString();

        FileUploadResult result = await Files(owned).UploadAsync(Upload(Png));

        Assert.Equal(userId, result.File!.UploadedByUserId);
    }

    [SkippableFact]
    public async Task RefusesAnEmptyFile()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (_, ServiceProvider provider) = await ArrangeAsync();
        await using ServiceProvider owned = provider;

        Assert.Equal(
            FileRejection.Empty,
            (await Files(owned).UploadAsync(Upload(Encoding.UTF8.GetBytes(string.Empty)))).Rejection);
    }
}
