using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 酷狗账号扫码登录，走本地 KuGouMusicApi 代理：
/// /login/qr/key（返回 qrcode + qrcode_img）+ /login/qr/check（status 0 过期 / 1 待扫码 / 2 已扫码 / 4 成功）。
/// 播放接口还要求 dfid（/register/dev 注册设备获得），登录成功时一并写入组合 cookie。
/// </summary>
public sealed class KugouAccountService
{
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
            // timestamp 破缓存：本地代理对 200 响应按 URL 缓存 2 分钟，二维码会话必须每次拿新数据
            var json = await _httpClient.GetStringAsync(
                $"{_baseUrl}/login/qr/key?timestamp={ClockStamp()}", cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("qrcode", out var qrcodeEl))
                return null;

            var key = qrcodeEl.GetString();
            if (string.IsNullOrEmpty(key))
                return null;

            if (!data.TryGetProperty("qrcode_img", out var imgEl))
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
            // 轮询接口同样要破缓存，否则第一次的状态响应会被缓存 2 分钟
            var json = await _httpClient.GetStringAsync(
                $"{_baseUrl}/login/qr/check?key={Uri.EscapeDataString(key)}&timestamp={ClockStamp()}",
                cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("status", out var statusEl) ||
                !statusEl.TryGetInt32(out var status))
                return new QrPollResult(QrState.Failed);

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

    /// <summary>查询昵称（best-effort，字段名随接口版本可能变化，失败仅影响展示）。</summary>
    public async Task<string?> GetNicknameAsync(string cookie, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _httpClient.GetStringAsync(
                $"{_baseUrl}/user/detail?userid={Uri.EscapeDataString(ExtractCookieValue(cookie, "userid"))}&timestamp={ClockStamp()}",
                cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var section in new[] { root, root.TryGetProperty("data", out var d) ? d : root })
            {
                foreach (var fieldName in new[] { "nick_name", "nickname", "nickName" })
                {
                    if (section.ValueKind == JsonValueKind.Object &&
                        section.TryGetProperty(fieldName, out var nick) &&
                        nick.ValueKind == JsonValueKind.String)
                    {
                        var value = nick.GetString();
                        if (!string.IsNullOrEmpty(value))
                            return value;
                    }
                }
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

    private async Task<string?> BuildCookieAsync(JsonElement data, CancellationToken cancellationToken)
    {
        // 酷狗的 token 是字符串、userid 是数字——按字段类型兼容解析
        var token = GetFlexibleString(data, "token");
        var userid = GetFlexibleString(data, "userid");
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(userid))
            return null;

        var dfid = await GetOrCreateDfidAsync(token, userid, cancellationToken);
        return string.IsNullOrWhiteSpace(dfid)
            ? $"token={token};userid={userid}"
            : $"token={token};userid={userid};dfid={dfid}";
    }

    /// <summary>
    /// 为历史登录态补齐设备标识。旧版本保存的 Cookie 可能只有 token/userid，
    /// 而酷狗播放接口要求 dfid；成功后返回可直接替换保存的组合 Cookie。
    /// </summary>
    public async Task<string?> EnsureDfidCookieAsync(
        string cookie,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookie))
            return null;

        if (HasUsableDfid(cookie))
            return cookie;

        var token = ExtractCookieValue(cookie, "token");
        var userid = ExtractCookieValue(cookie, "userid");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(userid))
            return null;

        var dfid = await GetOrCreateDfidAsync(token, userid, cancellationToken);
        return string.IsNullOrWhiteSpace(dfid)
            ? null
            : $"token={token};userid={userid};dfid={dfid}";
    }

    private static string? GetFlexibleString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var value) ? value.ToString() : el.GetRawText(),
            _ => null
        };
    }

    /// <summary>获取 dfid；酷狗播放接口没有它会返回"本次请求需要验证"。</summary>
    private async Task<string?> GetOrCreateDfidAsync(
        string token,
        string userid,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_baseUrl}/register/dev?timestamp={ClockStamp()}");
            // register/dev 需要登录身份参与设备注册；不能把凭据放进 URL，
            // 通过 Authorization 让本地代理合并成酷狗请求 Cookie。
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                $"token={token};userid={userid}");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
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

    private static long ClockStamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string ExtractCookieValue(string? cookie, string name)
    {
        if (string.IsNullOrEmpty(cookie))
            return string.Empty;

        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }
        return string.Empty;
    }

    private static bool HasUsableDfid(string cookie)
    {
        var dfid = ExtractCookieValue(cookie, "dfid");
        return !string.IsNullOrWhiteSpace(dfid) &&
               !string.Equals(dfid, "-", StringComparison.Ordinal) &&
               !string.Equals(dfid, "0", StringComparison.Ordinal);
    }
}
