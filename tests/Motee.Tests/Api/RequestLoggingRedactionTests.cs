using Motee.Api.Http;

namespace Motee.Tests.Api;

// Payload logging writes request and response bodies into CloudWatch, where they are
// kept for the retention period and readable by everyone holding logs:GetLogEvents.
// These bodies carry chosen passwords, live OTP codes and one-time join links, so the
// redaction is the only thing between "useful debugging" and publishing credentials.
public class RequestLoggingRedactionTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("newPassword")]
    [InlineData("confirmPassword")]
    [InlineData("code")]
    [InlineData("otp")]
    [InlineData("token")]
    [InlineData("refreshToken")]
    [InlineData("accessToken")]
    [InlineData("joinToken")]
    [InlineData("apiKey")]
    public void SensitiveFieldsAreReplaced(string field)
    {
        string redacted = RequestResponseLoggingMiddleware.Redact(
            $$"""{"email":"ada@acme.com","{{field}}":"hunter2"}""");

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
    }

    // Names arrive in several shapes across the API and the frontend. Matching only the
    // exact camelCase spelling would let join_token through untouched.
    [Theory]
    [InlineData("join_token")]
    [InlineData("join-token")]
    [InlineData("JoinToken")]
    [InlineData("JOINTOKEN")]
    public void TheMatchIgnoresCaseAndSeparators(string field)
    {
        string redacted = RequestResponseLoggingMiddleware.Redact($$"""{"{{field}}":"abc123"}""");

        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
    }

    // The whole point is to still be able to read the payload.
    [Fact]
    public void EverythingElseSurvives()
    {
        string redacted = RequestResponseLoggingMiddleware.Redact(
            """{"email":"ada@acme.com","companyName":"Acme","countryCode":"NG"}""");

        Assert.Contains("ada@acme.com", redacted, StringComparison.Ordinal);
        Assert.Contains("Acme", redacted, StringComparison.Ordinal);
        Assert.Contains("NG", redacted, StringComparison.Ordinal);
    }

    // A password nested inside an object or an array is still a password.
    [Fact]
    public void NestedAndArrayValuesAreReached()
    {
        string redacted = RequestResponseLoggingMiddleware.Redact(
            """{"data":{"user":{"password":"hunter2"}},"items":[{"token":"abc123"}]}""");

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
    }

    // Malformed JSON is often why a request failed, but it cannot be parsed to redact
    // and may still hold a password - so the shape is reported and the content is not.
    [Fact]
    public void UnparseableBodiesAreNotEchoed()
    {
        string redacted = RequestResponseLoggingMiddleware.Redact("""{"password":"hunter2"  """);

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("unparseable", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaceholdersForSkippedBodiesArePassedThrough()
    {
        Assert.Equal("<application/pdf, 90000 bytes>",
            RequestResponseLoggingMiddleware.Redact("<application/pdf, 90000 bytes>"));

        Assert.Equal(string.Empty, RequestResponseLoggingMiddleware.Redact(string.Empty));
    }
}
