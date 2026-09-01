using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIRadio.Desktop.Services;

/// <summary>酷狗代理跨进程复用的设备身份；MID 由代理根据 DeviceGuid 统一派生。</summary>
public sealed record KugouDeviceIdentity(
    int Version,
    string DeviceGuid,
    string DeviceDev,
    string DeviceWebGl)
{
    private const int CurrentVersion = 1;
    private const string ProxyServiceName = "tuanradio-kugou-proxy";
    private const int ProxyProtocolVersion = 1;
    private static readonly Regex Hex32 = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex Dev10 = new("^[0-9A-F]{10}$", RegexOptions.Compiled);
    private static readonly Regex UnsignedDecimal = new("^[0-9]{1,20}$", RegexOptions.Compiled);

    public static KugouDeviceIdentity Create()
        => new(
            CurrentVersion,
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            CreateWebGlFingerprint());

    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>
    /// 本地代理身份握手使用的非敏感摘要。代理和桌面端必须基于同一组稳定设备字段计算，
    /// 防止复用崩溃遗留或外部启动、且设备身份不同的 37251 进程。
    /// </summary>
    public string ComputeProxyIdentityHash()
    {
        var payload = $"{DeviceGuid}|{DeviceDev.ToUpperInvariant()}|{DeviceWebGl}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
    }

    public bool MatchesProxyIdentityResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 16 * 1024)
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("status", out var status) &&
                   status.TryGetInt32(out var statusCode) &&
                   statusCode == 1 &&
                   root.TryGetProperty("service", out var service) &&
                   string.Equals(service.GetString(), ProxyServiceName, StringComparison.Ordinal) &&
                   root.TryGetProperty("protocol", out var protocol) &&
                   protocol.TryGetInt32(out var protocolVersion) &&
                   protocolVersion == ProxyProtocolVersion &&
                   root.TryGetProperty("device_hash", out var deviceHash) &&
                   string.Equals(
                       deviceHash.GetString(),
                       ComputeProxyIdentityHash(),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryDeserialize(string? json, out KugouDeviceIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<KugouDeviceIdentity>(json);
            if (parsed == null ||
                parsed.Version != CurrentVersion ||
                !Hex32.IsMatch(parsed.DeviceGuid) ||
                !Dev10.IsMatch(parsed.DeviceDev) ||
                !UnsignedDecimal.IsMatch(parsed.DeviceWebGl))
            {
                return false;
            }

            identity = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CreateWebGlFingerprint()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes).ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// 兼容现有分号 Cookie 的确定性合并器。凭据只用于 Authorization 头，禁止拼进 URL。
/// </summary>
internal static class KugouCookieCodec
{
    public static string? Get(string? cookie, string name)
    {
        if (string.IsNullOrWhiteSpace(cookie))
            return null;

        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return pair[1].Trim();
        }

        return null;
    }

    public static bool HasUsableDfid(string? cookie)
    {
        var dfid = Get(cookie, "dfid");
        return !string.IsNullOrWhiteSpace(dfid) && dfid != "-" && dfid != "0";
    }

    public static bool NeedsSessionRefresh(string? cookie)
        => string.IsNullOrWhiteSpace(Get(cookie, "t1")) ||
           string.IsNullOrWhiteSpace(Get(cookie, "vip_type"));

    public static string Merge(string? cookie, params KeyValuePair<string, string?>[] updates)
    {
        var ordered = Parse(cookie);
        foreach (var (key, value) in updates)
        {
            var index = ordered.FindIndex(item =>
                item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(value))
            {
                if (index >= 0)
                    ordered.RemoveAt(index);
                continue;
            }

            var replacement = new KeyValuePair<string, string>(key, value.Trim());
            if (index >= 0)
                ordered[index] = replacement;
            else
                ordered.Add(replacement);
        }

        return string.Join(';', ordered.Select(item => $"{item.Key}={item.Value}"));
    }

    public static string Remove(string? cookie, string name)
        => Merge(cookie, new KeyValuePair<string, string?>(name, null));

    private static List<KeyValuePair<string, string>> Parse(string? cookie)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(cookie))
            return result;

        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = pair[0].Trim();
            var value = pair.Length == 2 ? pair[1].Trim() : string.Empty;
            if (key.Length == 0 || value.Length == 0)
                continue;

            var index = result.FindIndex(item =>
                item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            var entry = new KeyValuePair<string, string>(key, value);
            if (index >= 0)
                result[index] = entry;
            else
                result.Add(entry);
        }

        return result;
    }
}
