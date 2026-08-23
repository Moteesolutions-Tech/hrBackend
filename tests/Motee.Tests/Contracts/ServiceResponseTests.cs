using System.Text.Json;
using Motee.Api.Contracts;

namespace Motee.Tests.Contracts;

public class ServiceResponseTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record Payload(string Name);

    [Fact]
    public void SuccessCarriesTheDataAndTheSuccessCode()
    {
        ServiceResponse<Payload> response = ServiceResponse<Payload>.Ok(new Payload("Ada"), "Done");

        Assert.True(response.Success);
        Assert.Equal(MoteeStatusCodes.Success, response.ResponseCode);
        Assert.Equal("Done", response.Message);
        Assert.Equal("Ada", response.Data?.Name);
    }

    [Fact]
    public void CreatedUsesItsOwnCode()
    {
        ServiceResponse<Payload> response = ServiceResponse<Payload>.Created(new Payload("Ada"));

        Assert.True(response.Success);
        Assert.Equal(MoteeStatusCodes.Created, response.ResponseCode);
    }

    [Fact]
    public void FailureCarriesNoData()
    {
        ServiceResponse<Payload> response =
            ServiceResponse<Payload>.Failure(MoteeStatusCodes.InvalidRequest, "Bad input");

        Assert.False(response.Success);
        Assert.Equal(MoteeStatusCodes.InvalidRequest, response.ResponseCode);
        Assert.Equal("Bad input", response.Message);
        Assert.Null(response.Data);
    }

    // The frontend reads snake_case keys; the camelCase policy must not rewrite them.
    [Fact]
    public void SerialisesWithTheAgreedKeyNames()
    {
        string json = JsonSerializer.Serialize(
            ServiceResponse<Payload>.Ok(new Payload("Ada")), Options);

        Assert.Contains("\"response_code\"", json, StringComparison.Ordinal);
        Assert.Contains("\"success\"", json, StringComparison.Ordinal);
        Assert.Contains("\"message\"", json, StringComparison.Ordinal);
        Assert.Contains("\"data\"", json, StringComparison.Ordinal);
    }

    // Nested payload properties still follow the camelCase policy.
    [Fact]
    public void LeavesPayloadPropertiesToTheCamelCasePolicy()
    {
        string json = JsonSerializer.Serialize(
            ServiceResponse<Payload>.Ok(new Payload("Ada")), Options);

        Assert.Contains("\"name\":\"Ada\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFailureCodeIsDistinct()
    {
        string[] codes = MoteeStatusCodes.All.ToArray();

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(MoteeStatusCodes.InvalidRequest, 400)]
    [InlineData(MoteeStatusCodes.Unauthorized, 401)]
    [InlineData(MoteeStatusCodes.Forbidden, 403)]
    [InlineData(MoteeStatusCodes.NotFound, 404)]
    [InlineData(MoteeStatusCodes.Conflict, 409)]
    [InlineData(MoteeStatusCodes.TooManyRequests, 429)]
    [InlineData(MoteeStatusCodes.OtpIncorrect, 400)]
    [InlineData(MoteeStatusCodes.OtpExpired, 410)]
    [InlineData(MoteeStatusCodes.OtpLockedOut, 429)]
    [InlineData(MoteeStatusCodes.EmailNotConfirmed, 403)]
    public void MapsEachBusinessCodeToAnHttpStatus(string code, int expected)
    {
        Assert.Equal(expected, MoteeStatusCodes.ToHttpStatus(code));
    }

    // An unrecognised code must not silently become 200.
    [Fact]
    public void MapsUnknownCodesToServerError()
    {
        Assert.Equal(500, MoteeStatusCodes.ToHttpStatus("not-a-code"));
    }

    [Fact]
    public void MapsSuccessCodesToOkAndCreated()
    {
        Assert.Equal(200, MoteeStatusCodes.ToHttpStatus(MoteeStatusCodes.Success));
        Assert.Equal(201, MoteeStatusCodes.ToHttpStatus(MoteeStatusCodes.Created));
    }
}
