using System;
using System.Net;
using System.Net.Http;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class ApiFailureInfoTests
{
    [Fact]
    public void FromException_TaskCanceled_ReturnsTimeout()
    {
        var result = ApiFailureInfo.FromException(new TaskCanceledException());
        Assert.Equal(ApiFailureKind.Timeout, result.Kind);
    }

    [Fact]
    public void FromException_TimeoutException_ReturnsTimeout()
    {
        var result = ApiFailureInfo.FromException(new TimeoutException());
        Assert.Equal(ApiFailureKind.Timeout, result.Kind);
    }

    [Fact]
    public void FromException_HttpRequestException_ReturnsNetwork()
    {
        var result = ApiFailureInfo.FromException(new HttpRequestException("Connection refused"));
        Assert.Equal(ApiFailureKind.Network, result.Kind);
    }

    [Fact]
    public void FromException_MinimaxApiException_ReturnsFailure()
    {
        var inner = new ApiFailureInfo(ApiFailureKind.Authentication, "Auth failed", "detail", "hint");
        var result = ApiFailureInfo.FromException(new MinimaxApiException(inner));
        Assert.Equal(ApiFailureKind.Authentication, result.Kind);
        Assert.Equal("Auth failed", result.Title);
    }

    [Fact]
    public void FromException_UnknownException_ReturnsUnknown()
    {
        var result = ApiFailureInfo.FromException(new InvalidOperationException("something broke"));
        Assert.Equal(ApiFailureKind.Unknown, result.Kind);
    }

    [Fact]
    public void FromStatusCode_Unauthorized_ReturnsAuthentication()
    {
        var result = ApiFailureInfo.FromStatusCode(HttpStatusCode.Unauthorized, "");
        Assert.Equal(ApiFailureKind.Authentication, result.Kind);
    }

    [Fact]
    public void FromStatusCode_Forbidden_ReturnsAuthentication()
    {
        var result = ApiFailureInfo.FromStatusCode(HttpStatusCode.Forbidden, "");
        Assert.Equal(ApiFailureKind.Authentication, result.Kind);
    }

    [Fact]
    public void FromStatusCode_TooManyRequests_ReturnsRateLimited()
    {
        var result = ApiFailureInfo.FromStatusCode((HttpStatusCode)429, "");
        Assert.Equal(ApiFailureKind.RateLimited, result.Kind);
    }

    [Fact]
    public void FromStatusCode_ServerError_ReturnsServer()
    {
        var result = ApiFailureInfo.FromStatusCode(HttpStatusCode.InternalServerError, "");
        Assert.Equal(ApiFailureKind.Server, result.Kind);
    }

    [Fact]
    public void FromStatusCode_BadRequest_ReturnsInvalidResponse()
    {
        var result = ApiFailureInfo.FromStatusCode(HttpStatusCode.BadRequest, "bad request");
        Assert.Equal(ApiFailureKind.InvalidResponse, result.Kind);
    }

    [Fact]
    public void MissingApiKey_ReturnsCorrectKind()
    {
        var result = ApiFailureInfo.MissingApiKey();
        Assert.Equal(ApiFailureKind.MissingApiKey, result.Kind);
    }
}
