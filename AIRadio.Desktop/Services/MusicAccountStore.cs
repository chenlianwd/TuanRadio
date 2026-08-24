using System;
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

    private readonly ISecureStorage _storage;

    public string? NeteaseCookie { get; private set; }
    public string? KugouCookie { get; private set; }

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
    }

    public async Task SetNeteaseCookieAsync(string? cookie)
    {
        NeteaseCookie = Sanitize(cookie);
        if (NeteaseCookie == null)
            _storage.DeleteApiKey(NeteaseCredentialService);
        else
            await _storage.SaveApiKeyAsync(NeteaseCredentialService, NeteaseCookie);
    }

    public async Task SetKugouCookieAsync(string? cookie)
    {
        KugouCookie = Sanitize(cookie);
        if (KugouCookie == null)
            _storage.DeleteApiKey(KugouCredentialService);
        else
            await _storage.SaveApiKeyAsync(KugouCredentialService, KugouCookie);
    }

    private static string? Sanitize(string? cookie)
        => string.IsNullOrWhiteSpace(cookie) ? null : cookie.Trim();
}
