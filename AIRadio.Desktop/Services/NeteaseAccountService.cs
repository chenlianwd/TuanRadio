using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>二维码登录会话：轮询键 + 二维码 PNG 位图。</summary>
public sealed record QrLoginSession(string Key, byte[] QrPng);

/// <summary>二维码轮询结果。Confirmed 时携带可用于后续请求的 cookie 字符串。</summary>
public sealed record QrPollResult(QrState State, string? Cookie = null);

public enum QrState
{
    Waiting,
    Scanned,
    Confirmed,
    Expired,
    Failed
}

/// <summary>
/// 网易云账号扫码登录，走本地 NeteaseCloudMusicApi 代理：
/// /login/qr/key + /login/qr/create?qrimg=1 + /login/qr/check（801 待扫码 / 802 已扫码 / 803 成功）。
/// </summary>
public sealed class NeteaseAccountService
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public NeteaseAccountService(HttpClient httpClient, string baseUrl = "http://127.0.0.1:37250")
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<QrLoginSession?> CreateQrSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // timestamp 破缓存：本地代理对 200 响应按 URL 缓存 2 分钟，
            // 二维码会话类接口必须每次拿新数据
            var keyJson = await _httpClient.GetStringAsync(
                $"{_baseUrl}/login/qr/key?timestamp={ClockStamp()}", cancellationToken);
            using var keyDoc = JsonDocument.Parse(keyJson);
            if (!keyDoc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("unikey", out var unikeyEl))
                return null;

            var key = unikeyEl.GetString();
            if (string.IsNullOrEmpty(key))
                return null;

            var createJson = await _httpClient.GetStringAsync(
                $"{_baseUrl}/login/qr/create?key={Uri.EscapeDataString(key)}&qrimg=1&timestamp={ClockStamp()}",
                cancellationToken);
            using var createDoc = JsonDocument.Parse(createJson);
            if (!createDoc.RootElement.TryGetProperty("data", out var createData) ||
                !createData.TryGetProperty("qrimg", out var qrimgEl))
                return null;

            // qrimg 是 "data:image/png;base64,xxxx" 形式的 data URI
            var png = ParseDataUriImage(qrimgEl.GetString());
            return png == null ? null : new QrLoginSession(key, png);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Netease QR session creation failed");
            return null;
        }
    }

    public async Task<QrPollResult> CheckQrAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            // 轮询接口同样要破缓存，否则第一次的"等待扫码"响应会被缓存 2 分钟
            var json = await _httpClient.GetStringAsync(
                $"{_baseUrl}/login/qr/check?key={Uri.EscapeDataString(key)}&timestamp={ClockStamp()}",
                cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("code", out var codeEl) ||
                !codeEl.TryGetInt32(out var code))
                return new QrPollResult(QrState.Failed);

            return code switch
            {
                801 => new QrPollResult(QrState.Waiting),
                802 => new QrPollResult(QrState.Scanned),
                803 => new QrPollResult(QrState.Confirmed,
                    doc.RootElement.TryGetProperty("cookie", out var cookieEl)
                        ? cookieEl.GetString()
                        : null),
                800 => new QrPollResult(QrState.Expired),
                _ => new QrPollResult(QrState.Failed)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Netease QR check failed");
            return new QrPollResult(QrState.Failed);
        }
    }

    /// <summary>用已保存的 cookie 查询昵称；失败返回 null（不抛异常，仅影响状态展示）。</summary>
    public async Task<string?> GetNicknameAsync(string cookie, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{_baseUrl}/login/status?timestamp={ClockStamp()}");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

            var root = doc.RootElement;
            // 常见形状: {data:{code:200, profile:{nickname}}}；登录态失效时无 profile
            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("profile", out var profile) &&
                profile.ValueKind == JsonValueKind.Object &&
                profile.TryGetProperty("nickname", out var nick))
            {
                return nick.GetString();
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Netease login status check failed");
            return null;
        }
    }

    private static long ClockStamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal static byte[]? ParseDataUriImage(string? dataUri)
    {
        if (string.IsNullOrEmpty(dataUri))
            return null;

        var comma = dataUri.IndexOf(',');
        if (comma < 0 || !dataUri.Contains("base64", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return Convert.FromBase64String(dataUri[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
