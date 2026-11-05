using System;
using System.Threading;
using System.Threading.Tasks;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 应用更新检查结果。
/// <paramref name="CanInstallInApp"/> 为 true 时表示当前为 Setup.exe 安装版，支持应用内直接下载并重启替换；
/// 为 false 时表示当前为免安装 .zip 便携版或开发态，引导用户打开 GitHub Release 页面下载。
/// </summary>
public sealed record AppUpdateCheckResult(
    bool HasUpdate,
    string? LatestVersion,
    string ReleasePageUrl,
    bool CanInstallInApp,
    object? InternalUpdateInfo = null);

public interface IAppUpdateService
{
    string CurrentVersion { get; }
    bool IsInstalled { get; }

    Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    Task DownloadUpdatesAsync(
        AppUpdateCheckResult update,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default);

    void ApplyUpdatesAndRestart(AppUpdateCheckResult update);
}
