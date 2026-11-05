using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 封装底层 Velopack UpdateManager 操作，供单元测试替身注入。
/// </summary>
internal interface IVelopackUpdateClient
{
    bool IsInstalled { get; }
    string? CurrentVersion { get; }
    Task<object?> CheckForUpdatesAsync();
    string? GetTargetVersion(object updateInfo);
    Task DownloadUpdatesAsync(object updateInfo, Action<int>? progress);
    void ApplyUpdatesAndRestart(object updateInfo);
}

internal sealed class VelopackUpdateClient : IVelopackUpdateClient
{
    private readonly UpdateManager? _manager;

    public VelopackUpdateClient(string repoUrl)
    {
        try
        {
            var source = new GithubSource(repoUrl, accessToken: null, prerelease: false);
            _manager = new UpdateManager(source);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Velopack UpdateManager initialization skipped (portable or dev mode)");
            _manager = null;
        }
    }

    public bool IsInstalled => _manager?.IsInstalled == true;

    public string? CurrentVersion => _manager?.CurrentVersion?.ToString();

    public async Task<object?> CheckForUpdatesAsync()
    {
        if (_manager is not { IsInstalled: true })
            return null;
        return await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
    }

    public string? GetTargetVersion(object updateInfo)
        => (updateInfo as UpdateInfo)?.TargetFullRelease?.Version?.ToString();

    public async Task DownloadUpdatesAsync(object updateInfo, Action<int>? progress)
    {
        if (_manager is not { IsInstalled: true } || updateInfo is not UpdateInfo info)
            throw new InvalidOperationException("In-app update download requires an installed Velopack instance.");

        await _manager.DownloadUpdatesAsync(info, progress).ConfigureAwait(false);
    }

    public void ApplyUpdatesAndRestart(object updateInfo)
    {
        if (_manager is not { IsInstalled: true } || updateInfo is not UpdateInfo info)
            throw new InvalidOperationException("In-app update apply requires an installed Velopack instance.");

        _manager.ApplyUpdatesAndRestart(info);
    }
}

public sealed class AppUpdateService : IAppUpdateService
{
    public const string RepositoryUrl = "https://github.com/chenlianwd/TuanRadio";
    public const string ReleasesPageUrl = "https://github.com/chenlianwd/TuanRadio/releases";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/chenlianwd/TuanRadio/releases/latest";

    private readonly HttpClient _httpClient;
    private readonly IVelopackUpdateClient _velopack;
    private readonly Action? _beforeRestart;
    private readonly string _fallbackVersion;

    public AppUpdateService(HttpClient httpClient, Action? beforeRestart = null)
        : this(httpClient, new VelopackUpdateClient(RepositoryUrl), beforeRestart, ResolveAssemblyVersion())
    {
    }

    internal AppUpdateService(
        HttpClient httpClient,
        IVelopackUpdateClient velopack,
        Action? beforeRestart = null,
        string? fallbackVersion = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _velopack = velopack ?? throw new ArgumentNullException(nameof(velopack));
        _beforeRestart = beforeRestart;
        _fallbackVersion = string.IsNullOrWhiteSpace(fallbackVersion)
            ? ResolveAssemblyVersion()
            : fallbackVersion!;
    }

    public bool IsInstalled => _velopack.IsInstalled;

    public string CurrentVersion =>
        !string.IsNullOrWhiteSpace(_velopack.CurrentVersion)
            ? _velopack.CurrentVersion!
            : _fallbackVersion;

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_velopack.IsInstalled)
        {
            var updateObj = await _velopack.CheckForUpdatesAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (updateObj != null)
            {
                var targetVersion = _velopack.GetTargetVersion(updateObj) ?? CurrentVersion;
                var releaseUrl = $"{ReleasesPageUrl}/tag/v{targetVersion}";
                return new AppUpdateCheckResult(
                    HasUpdate: true,
                    LatestVersion: targetVersion,
                    ReleasePageUrl: releaseUrl,
                    CanInstallInApp: true,
                    InternalUpdateInfo: updateObj);
            }

            return new AppUpdateCheckResult(
                HasUpdate: false,
                LatestVersion: CurrentVersion,
                ReleasePageUrl: ReleasesPageUrl,
                CanInstallInApp: true);
        }

        return await CheckGithubLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadUpdatesAsync(
        AppUpdateCheckResult update,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();

        if (!update.CanInstallInApp || update.InternalUpdateInfo == null)
            throw new InvalidOperationException("Current build does not support in-app update download.");

        await _velopack.DownloadUpdatesAsync(update.InternalUpdateInfo, progress).ConfigureAwait(false);
    }

    public void ApplyUpdatesAndRestart(AppUpdateCheckResult update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (!update.CanInstallInApp || update.InternalUpdateInfo == null)
            throw new InvalidOperationException("Current build does not support in-app update restart.");

        try
        {
            _beforeRestart?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pre-restart cleanup encountered an error; proceeding with update restart");
        }

        _velopack.ApplyUpdatesAndRestart(update.InternalUpdateInfo);
    }

    private async Task<AppUpdateCheckResult> CheckGithubLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("TuanRadio", CurrentVersion));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tagName = root.TryGetProperty("tag_name", out var tagProp)
            ? tagProp.GetString()
            : null;
        var htmlUrl = root.TryGetProperty("html_url", out var urlProp) && !string.IsNullOrWhiteSpace(urlProp.GetString())
            ? urlProp.GetString()!
            : ReleasesPageUrl;

        var cleanLatest = NormalizeVersionString(tagName);
        if (string.IsNullOrWhiteSpace(cleanLatest))
        {
            return new AppUpdateCheckResult(
                HasUpdate: false,
                LatestVersion: CurrentVersion,
                ReleasePageUrl: htmlUrl,
                CanInstallInApp: false);
        }

        var isNewer = CompareVersionStrings(cleanLatest!, CurrentVersion) > 0;
        return new AppUpdateCheckResult(
            HasUpdate: isNewer,
            LatestVersion: isNewer ? cleanLatest : CurrentVersion,
            ReleasePageUrl: htmlUrl,
            CanInstallInApp: false);
    }

    internal static string? NormalizeVersionString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[1..].Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// 比较两个语义化版本字符串（忽略 -preview 等后缀做主版本段比较，主版本相同且一方无后缀时正式版优先）。
    /// </summary>
    internal static int CompareVersionStrings(string leftRaw, string rightRaw)
    {
        var (leftCore, leftPre) = SplitPreRelease(NormalizeVersionString(leftRaw) ?? "0.0.0");
        var (rightCore, rightPre) = SplitPreRelease(NormalizeVersionString(rightRaw) ?? "0.0.0");

        if (Version.TryParse(PadVersionComponents(leftCore), out var leftVer) &&
            Version.TryParse(PadVersionComponents(rightCore), out var rightVer))
        {
            var cmp = leftVer.CompareTo(rightVer);
            if (cmp != 0)
                return cmp;

            // 1.0.0 > 1.0.0-beta.1
            if (string.IsNullOrEmpty(leftPre) && !string.IsNullOrEmpty(rightPre))
                return 1;
            if (!string.IsNullOrEmpty(leftPre) && string.IsNullOrEmpty(rightPre))
                return -1;
            return string.Compare(leftPre, rightPre, StringComparison.OrdinalIgnoreCase);
        }

        return string.Compare(leftRaw, rightRaw, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Core, string PreRelease) SplitPreRelease(string version)
    {
        var plusIdx = version.IndexOf('+');
        if (plusIdx >= 0)
            version = version[..plusIdx];
        var dashIdx = version.IndexOf('-');
        return dashIdx >= 0
            ? (version[..dashIdx], version[(dashIdx + 1)..])
            : (version, string.Empty);
    }

    private static string PadVersionComponents(string core)
    {
        var parts = core.Split('.');
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => core
        };
    }

    private static string ResolveAssemblyVersion()
    {
        var ver = typeof(AppUpdateService).Assembly.GetName().Version;
        if (ver == null)
            return "1.0.0";
        return $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}";
    }
}
