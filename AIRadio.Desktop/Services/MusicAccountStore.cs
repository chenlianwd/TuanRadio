using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 各在线音源的账号登录态（cookie/token）中心。
/// 凭据本体存 Windows 凭据管理器（ISecureStorage），内存副本供音源服务请求时直接引用；
/// 未登录时属性为 null，音源按匿名/禁用处理。
/// </summary>
public sealed class MusicAccountStore
{
    public const string NeteaseCredentialService = "netease-cookie";
    public const string KugouCredentialService = "kugou-cookie";
    public const string KugouDeviceIdentityService = "kugou-device-v1";
    public const string KugouDfidOwnerService = "kugou-dfid-owner-v1";

    private readonly ISecureStorage _storage;

    public string? NeteaseCookie { get; private set; }
    public string? KugouCookie { get; private set; }
    public KugouDeviceIdentity KugouDevice { get; private set; } = KugouDeviceIdentity.Create();
    public bool IsLoaded { get; private set; }
    public event EventHandler? KugouCredentialChanged;

    /// <summary>yt-dlp --cookies-from-browser 的浏览器标识；空表示不使用浏览器 cookies。</summary>
    public string YtdlpCookieBrowser { get; set; } = "";

    public MusicAccountStore(ISecureStorage storage)
    {
        _storage = storage;
    }

    public async Task LoadAsync()
    {
        try
        {
            NeteaseCookie = await _storage.GetApiKeyAsync(NeteaseCredentialService);
            KugouCookie = await _storage.GetApiKeyAsync(KugouCredentialService);
        }
        catch (Exception ex)
        {
            // 凭据读取失败只降级为匿名，不影响启动
            Log.Warning(ex, "Failed to load music account cookies");
        }

        try
        {
            var serialized = await _storage.GetApiKeyAsync(KugouDeviceIdentityService);
            if (!KugouDeviceIdentity.TryDeserialize(serialized, out var identity))
            {
                identity = KugouDeviceIdentity.Create();
                await _storage.SaveApiKeyAsync(KugouDeviceIdentityService, identity.Serialize());
            }

            KugouDevice = identity;
            var dfidOwner = await _storage.GetApiKeyAsync(KugouDfidOwnerService);
            // 旧版本没有 owner 标记；设备迁移或部分写入后 owner 不匹配时，旧 dfid 不能复用。
            if (KugouCookieCodec.HasUsableDfid(KugouCookie) &&
                !string.Equals(dfidOwner, identity.DeviceGuid, StringComparison.Ordinal))
            {
                var migrated = KugouCookieCodec.Remove(KugouCookie, "dfid");
                await _storage.SaveApiKeyAsync(KugouCredentialService, migrated);
                KugouCookie = migrated;
            }
        }
        catch (Exception ex)
        {
            // 设备身份保存失败时仍允许应用启动，但本进程只使用内存身份且不会复用旧 dfid。
            KugouCookie = KugouCookieCodec.Remove(KugouCookie, "dfid");
            Log.Warning(ex, "Failed to load or persist stable Kugou device identity");
        }
        finally
        {
            IsLoaded = true;
        }
    }

    /// <summary>供酷狗 Node 代理启动时读取；不包含账号 token 或用户标识。</summary>
    public IReadOnlyDictionary<string, string> GetKugouProxyEnvironment()
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KUGOU_API_GUID"] = KugouDevice.DeviceGuid,
            ["KUGOU_API_DEV"] = KugouDevice.DeviceDev,
            ["KUGOU_API_WEBGL"] = KugouDevice.DeviceWebGl
        };

    public async Task SetNeteaseCookieAsync(string? cookie)
    {
        var sanitized = Sanitize(cookie);
        if (sanitized == null)
        {
            _storage.DeleteApiKey(NeteaseCredentialService);
            NeteaseCookie = null;
        }
        else
        {
            // 先持久化再更新内存副本：持久化失败时异常上抛、内存保持旧值，
            // 避免“界面显示已登录、重启后登录态丢失”的不一致状态
            await _storage.SaveApiKeyAsync(NeteaseCredentialService, sanitized);
            NeteaseCookie = sanitized;
        }
    }

    public async Task SetKugouCookieAsync(string? cookie)
    {
        var sanitized = Sanitize(cookie);
        if (sanitized == null)
        {
            _storage.DeleteApiKey(KugouCredentialService);
            _storage.DeleteApiKey(KugouDfidOwnerService);
            KugouCookie = null;
        }
        else
        {
            await _storage.SaveApiKeyAsync(KugouCredentialService, sanitized);
            if (IsLoaded && KugouCookieCodec.HasUsableDfid(sanitized))
            {
                try
                {
                    await _storage.SaveApiKeyAsync(KugouDfidOwnerService, KugouDevice.DeviceGuid);
                }
                catch (Exception ex)
                {
                    // Cookie 已成功持久化；owner 缺失只会让下次启动安全地重新注册 dfid。
                    Log.Warning(ex, "Failed to persist Kugou dfid device ownership marker");
                }
            }
            KugouCookie = sanitized;
        }

        NotifyKugouCredentialChanged();
    }

    private void NotifyKugouCredentialChanged()
    {
        var handlers = KugouCredentialChanged;
        if (handlers == null)
            return;

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // 凭据已经成功持久化；单个观察者的清理失败不能把登录结果回滚成失败。
                Log.Warning(ex, "Kugou credential change observer failed");
            }
        }
    }

    private static string? Sanitize(string? cookie)
        => string.IsNullOrWhiteSpace(cookie) ? null : cookie.Trim();
}
