using Motee.Domain.Files;

namespace Motee.Tests.Files;

public class FilePolicyTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00];
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x2D];
    private static readonly byte[] Executable = [0x4D, 0x5A, 0x90, 0x00];

    [Fact]
    public void EveryPurposeHasARule()
    {
        Assert.All(Enum.GetValues<FilePurpose>(), purpose =>
            Assert.NotNull(FilePolicy.RuleFor(purpose)));
    }

    [Theory]
    [InlineData(FilePurpose.CompanyLogo, "image/png")]
    [InlineData(FilePurpose.EmployeeAvatar, "image/jpeg")]
    [InlineData(FilePurpose.EmployeeDocument, "application/pdf")]
    public void AcceptsWhatThePurposeAllows(FilePurpose purpose, string contentType)
    {
        Assert.Equal(FileRejection.None, FilePolicy.Check(purpose, contentType, 1024));
    }

    // An avatar slot must not accept a PDF, whatever the client claims.
    [Theory]
    [InlineData(FilePurpose.EmployeeAvatar, "application/pdf")]
    [InlineData(FilePurpose.CompanyLogo, "text/html")]
    [InlineData(FilePurpose.EmployeeDocument, "application/x-msdownload")]
    public void RefusesTypesThePurposeDoesNotAllow(FilePurpose purpose, string contentType)
    {
        Assert.Equal(FileRejection.DisallowedType, FilePolicy.Check(purpose, contentType, 1024));
    }

    [Fact]
    public void RefusesAnEmptyFile()
    {
        Assert.Equal(FileRejection.Empty, FilePolicy.Check(FilePurpose.CompanyLogo, "image/png", 0));
    }

    [Fact]
    public void RefusesAFileOverTheLimit()
    {
        long overLogoLimit = FilePolicy.RuleFor(FilePurpose.CompanyLogo)!.MaxBytes + 1;

        Assert.Equal(
            FileRejection.TooLarge,
            FilePolicy.Check(FilePurpose.CompanyLogo, "image/png", overLogoLimit));
    }

    // A document may be far larger than a logo.
    [Fact]
    public void LimitsDifferByPurpose()
    {
        Assert.True(
            FilePolicy.RuleFor(FilePurpose.EmployeeDocument)!.MaxBytes
            > FilePolicy.RuleFor(FilePurpose.EmployeeAvatar)!.MaxBytes);
    }

    [Theory]
    [InlineData("image/png; charset=binary")]
    [InlineData("IMAGE/PNG")]
    [InlineData("  image/png  ")]
    public void IgnoresParametersAndCasingOnTheContentType(string contentType)
    {
        Assert.Equal(FileRejection.None, FilePolicy.Check(FilePurpose.CompanyLogo, contentType, 1024));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RefusesAMissingContentType(string? contentType)
    {
        Assert.Equal(
            FileRejection.DisallowedType,
            FilePolicy.Check(FilePurpose.CompanyLogo, contentType, 1024));
    }

    [Fact]
    public void AcceptsBytesThatMatchTheDeclaredType()
    {
        Assert.True(FilePolicy.MatchesSignature("image/png", Png));
        Assert.True(FilePolicy.MatchesSignature("image/jpeg", Jpeg));
        Assert.True(FilePolicy.MatchesSignature("application/pdf", Pdf));
    }

    // The reason signature checking exists: an executable renamed to .png and
    // declared as an image would otherwise be stored and served back later.
    [Fact]
    public void RefusesBytesThatContradictTheDeclaredType()
    {
        Assert.False(FilePolicy.MatchesSignature("image/png", Executable));
        Assert.False(FilePolicy.MatchesSignature("application/pdf", Executable));
        Assert.False(FilePolicy.MatchesSignature("image/jpeg", Png));
    }

    [Fact]
    public void ATruncatedFileDoesNotPassAsItsType()
    {
        Assert.False(FilePolicy.MatchesSignature("image/png", [0x89]));
    }

    // Text has no signature to check, so the type and size limits carry it.
    [Fact]
    public void FormatsWithoutASignatureAreNotRejectedOnBytes()
    {
        Assert.True(FilePolicy.MatchesSignature("text/csv", "Name,Email"u8));
    }
}
