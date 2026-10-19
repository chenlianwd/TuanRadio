using System;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
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
    public void FromException_LlmApiException_ReturnsFailure()
    {
        var inner = new ApiFailureInfo(ApiFailureKind.Authentication, "Auth failed", "detail", "hint");
        var result = ApiFailureInfo.FromException(new LlmApiException(inner));
        Assert.Equal(ApiFailureKind.Authentication, result.Kind);
        Assert.Equal("Auth failed", result.Title);
    }

    [Fact]
    public void FromException_UnknownException_ReturnsUnknown()
    {
        var result = ApiFailureInfo.FromException(new InvalidOperationException("something broke"));
        Assert.Equal(ApiFailureKind.Unknown, result.Kind);
    }

    // Edge TTS 的 403 握手失败是 WebSocketException：不得落入 Unknown 兜底
    // 显示"AI 服务异常"+英文原文（把语音服务故障误导成 AI 服务故障）
    [Fact]
    public void FromException_WebSocketException_ReturnsVoiceServiceFailure()
    {
        var original = AppLanguage.Current;
        try
        {
            AppLanguage.Apply("zh");
            var result = ApiFailureInfo.FromException(
                new WebSocketException("The server returned status code '403' when status code '101' was expected."));

            Assert.Equal(ApiFailureKind.Network, result.Kind);
            Assert.Equal("语音服务连接失败", result.Title);
        }
        finally
        {
            AppLanguage.Apply(original);
        }
    }

    [Fact]
    public void FromException_WebSocketException_EnglishTitleSurvivesKindLocalization()
    {
        var original = AppLanguage.Current;
        try
        {
            AppLanguage.Apply("en");
            var result = ApiFailureInfo.FromException(new WebSocketException("handshake rejected"));

            // 自带双语的文案经 ForCurrentLanguage 应原样放行，不被 Network Kind 的
            // 英文模板（"Can't connect to the AI service"）覆盖
            var localized = ApiFailureLocalization.ForCurrentLanguage(result);
            Assert.Equal("Voice service connection failed", localized.Title);
        }
        finally
        {
            AppLanguage.Apply(original);
        }
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
