using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 酷狗账号扫码与会话刷新。二维码确认只提供基础 token/userid；完整会话必须继续经过
/// /login/token、/register/dev 和 /user/verify，并始终通过 Authorization 头传递。
/// </summary>
public sealed class KugouAccountService
{
    private static readonly string[] SessionFields =
        { "token", "userid", "t1", "vip_type", "vip_token" };

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public KugouAccountService(HttpClient httpClient, string baseUrl = "http://127.0.0.1:37251")
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<QrLoginSession?> CreateQrSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _httpClient.GetStringAsync(
                $"{_baseUrl}/login/qr/key?timestamp={ClockStamp()}", cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("qrcode", out var qrcodeEl))
            {
                return null;
            }

            var key = qrcodeEl.GetString();
            if (string.IsNullOrEmpty(key) || !data.TryGetProperty("qrcode_img", out var imgEl))
                return null;

            var png = NeteaseAccountService.ParseDataUriImage(imgEl.GetString());
            return png == null ? null : new QrLoginSession(key, png);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kugou QR session creation failed");
            return null;
        }
    }

    public async Task<QrPollResult> CheckQrAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _httpClient.GetStringAsync(
                $"{_baseUrl}/login/qr/check?key={Uri.EscapeDataString(key)}&timestamp={ClockStamp()}",
                cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("status", out var statusEl) ||
                !statusEl.TryGetInt32(out var status))
            {
                return new QrPollResult(QrState.Failed);
            }

            return status switch
            {
                1 => new QrPollResult(QrState.Waiting),
                2 => new QrPollResult(QrState.Scanned),
                4 => new QrPollResult(QrState.Confirmed,
                    await BuildCookieAsync(data, cancellationToken)),
                0 => new QrPollResult(QrState.Expired),
                _ => new QrPollResult(QrState.Failed)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kugou QR check failed");
            return new QrPollResult(QrState.Failed);
        }
    }

    /// <summary>
    /// 原地补全历史 Cookie 或新扫码会话。接口失败时保留仍可用的已有字段，不伪造会员状态。
    /// </summary>
    public async Task<string?> RefreshCredentialAsync(
        string cookie,
        bool forceDeviceRegistration = false,
        bool forceSessionRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(KugouCookieCodec.Get(cookie, "token")) ||
            string.IsNullOrWhiteSpace(KugouCookieCodec.Get(cookie, "userid")))
        {
            return null;
        }

        var current = cookie;
        if (forceSessionRefresh || KugouCookieCodec.NeedsSessionRefresh(current))
        {
            var refreshed = await TryRefreshLoginTokenAsync(current, cancellationToken);
            if (!string.IsNullOrWhiteSpace(refreshed))
                current = refreshed;
        }

        if (forceDeviceRegistration || !KugouCookieCodec.HasUsableDfid(current))
        {
            var dfid = await GetOrCreateDfidAsync(current, cancellationToken);
            if (!string.IsNullOrWhiteSpace(dfid))
            {
                current = KugouCookieCodec.Merge(current,
                    new KeyValuePair<string, string?>("dfid", dfid));
            }
        }

        if (forceSessionRefresh || string.IsNullOrWhiteSpace(KugouCookieCodec.Get(current, "auth")))
        {
            var auth = await TryGetUserAuthAsync(current, cancellationToken);
            if (!string.IsNullOrWhiteSpace(auth))
            {
                current = KugouCookieCodec.Merge(current,
                    new KeyValuePair<string, string?>("auth", auth));
            }
        }

        return current;
    }

    /// <summary>兼容旧调用：补 dfid 时保留 t1/vip/auth 等所有既有字段。</summary>
    public async Task<string?> EnsureDfidCookieAsync(
        string cookie,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookie))
            return null;
        if (KugouCookieCodec.HasUsableDfid(cookie))
            return cookie;

        var dfid = await GetOrCreateDfidAsync(cookie, cancellationToken);
        return string.IsNullOrWhiteSpace(dfid)
            ? null
            : KugouCookieCodec.Merge(cookie,
                new KeyValuePair<string, string?>("dfid", dfid));
    }

    /// <summary>查询昵称；Authorization 不能省略，否则 /user/detail 会返回未登录错误。</summary>
    public async Task<string?> GetNicknameAsync(string cookie, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = BuildAuthorizedRequest(
                $"{_baseUrl}/user/detail?userid={Uri.EscapeDataString(KugouCookieCodec.Get(cookie, "userid") ?? string.Empty)}&timestamp={ClockStamp()}",
                cookie);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;
            foreach (var fieldName in new[] { "nick_name", "nickname", "nickName", "user_name", "username" })
            {
                if (TryFindStringProperty(root, fieldName, depth: 0, out var nickname))
                    return nickname;
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Kugou nickname check failed");
            return null;
        }
    }

    /// <summary>读取会员诊断。接口形状未命中时只返回不可用，不臆造会员到期时间。</summary>
    public async Task<KugouAccountSnapshot> GetAccountSnapshotAsync(
        string cookie,
        CancellationToken cancellationToken = default)
    {
        var nickname = await GetNicknameAsync(cookie, cancellationToken);
        var vipEndpointAvailable = await CheckVipDetailAsync(cookie, cancellationToken);
        _ = int.TryParse(KugouCookieCodec.Get(cookie, "vip_type"), out var vipType);
        return new KugouAccountSnapshot(
            cookie,
            nickname,
            vipType,
            !string.IsNullOrWhiteSpace(KugouCookieCodec.Get(cookie, "vip_token")),
            !string.IsNullOrWhiteSpace(KugouCookieCodec.Get(cookie, "auth")),
            vipEndpointAvailable);
    }

    private async Task<string?> BuildCookieAsync(JsonElement data, CancellationToken cancellationToken)
    {
        var token = GetFlexibleString(data, "token");
        var userid = GetFlexibleString(data, "userid");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(userid))
            return null;

        var basic = KugouCookieCodec.Merge(null,
            new KeyValuePair<string, string?>("token", token),
            new KeyValuePair<string, string?>("userid", userid));
        return await RefreshCredentialAsync(
            basic,
            forceDeviceRegistration: true,
            forceSessionRefresh: true,
            cancellationToken);
    }

    private async Task<string?> TryRefreshLoginTokenAsync(
        string cookie,
        CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await SendAuthorizedJsonAsync(
                $"{_baseUrl}/login/token?timestamp={ClockStamp()}", cookie, cancellationToken);
            var root = doc.RootElement;
            if (GetFlexibleInt32(root, "status") != 1 ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                Log.Information("Kugou complete-session refresh was rejected");
                return null;
            }

            var updates = new List<KeyValuePair<string, string?>>();
            foreach (var name in SessionFields)
            {
                var value = GetFlexibleString(data, name);
                if (value != null)
                    updates.Add(new KeyValuePair<string, string?>(name, value));
            }

            return updates.Count == 0 ? null : KugouCookieCodec.Merge(cookie, updates.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Kugou complete-session refresh failed");
            return null;
        }
    }

    private async Task<string?> TryGetUserAuthAsync(string cookie, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await SendAuthorizedJsonAsync(
                $"{_baseUrl}/user/verify?timestamp={ClockStamp()}", cookie, cancellationToken);
            return doc.RootElement.TryGetProperty("data", out var data)
                ? GetFlexibleString(data, "auth")
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Kugou user auth refresh failed");
            return null;
        }
    }

    private async Task<bool> CheckVipDetailAsync(string cookie, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await SendAuthorizedJsonAsync(
                $"{_baseUrl}/user/vip/detail?timestamp={ClockStamp()}", cookie, cancellationToken);
            var root = doc.RootElement;
            return GetFlexibleInt32(root, "status") == 1 || GetFlexibleInt32(root, "error_code") == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Kugou VIP detail check failed");
            return false;
        }
    }

    /// <summary>获取 dfid；注册使用当前完整 Cookie，避免补齐时丢掉会话字段。</summary>
    private async Task<string?> GetOrCreateDfidAsync(string cookie, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await SendAuthorizedJsonAsync(
                $"{_baseUrl}/register/dev?timestamp={ClockStamp()}", cookie, cancellationToken);
            return doc.RootElement.TryGetProperty("data", out var data)
                ? GetFlexibleString(data, "dfid")
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Kugou dfid registration failed");
            return null;
        }
    }

    private async Task<JsonDocument> SendAuthorizedJsonAsync(
        string url,
        string cookie,
        CancellationToken cancellationToken)
    {
        using var request = BuildAuthorizedRequest(url, cookie);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static HttpRequestMessage BuildAuthorizedRequest(string url, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", cookie);
        return request;
    }

    private static string? GetFlexibleString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null
        };
    }

    private static int? GetFlexibleInt32(JsonElement element, string name)
    {
        var text = GetFlexibleString(element, name);
        return int.TryParse(text, out var value) ? value : null;
    }

    private static bool TryFindStringProperty(
        JsonElement element,
        string name,
        int depth,
        out string? result)
    {
        result = null;
        if (depth > 4)
            return false;

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                result = value.GetString();
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindStringProperty(property.Value, name, depth + 1, out result))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindStringProperty(item, name, depth + 1, out result))
                    return true;
            }
        }

        return false;
    }

    private static long ClockStamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed record KugouAccountSnapshot(
    string Cookie,
    string? Nickname,
    int VipType,
    bool HasVipToken,
    bool HasAuth,
    bool VipEndpointAvailable);
