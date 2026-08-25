using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// yt-dlp 安装治理测试：固定版本清单、SHA256 校验失败不替换、取消不留半成品、版本比较。
/// 均为本地文件操作，零联网。
/// </summary>
public class YtdlpManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));

    private string TargetPath => Path.Combine(_dir, "yt-dlp.exe");
    private string VersionFilePath => Path.Combine(_dir, "yt-dlp.version");

    public YtdlpManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MemoryStream BytesOf(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string Sha256Of(byte[] bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task InstallVerifiedAsync_MatchingHash_ReplacesTargetAndWritesVersion()
    {
        var content = Encoding.UTF8.GetBytes("fake exe body");
        await File.WriteAllTextAsync(TargetPath, "old insecure exe");

        using var input = BytesOf("fake exe body");
        await YtdlpManager.InstallVerifiedAsync(
            input, TargetPath, VersionFilePath, Sha256Of(content), "2026.08.19", CancellationToken.None);

        Assert.Equal("fake exe body", await File.ReadAllTextAsync(TargetPath));
        Assert.Equal("2026.08.19", (await File.ReadAllTextAsync(VersionFilePath)).Trim());
        Assert.False(File.Exists(TargetPath + ".tmp"));
    }

    [Fact]
    public async Task InstallVerifiedAsync_HashMismatch_KeepsExistingExeAndLeavesNoTemp()
    {
        await File.WriteAllTextAsync(TargetPath, "existing install");

        using var input = BytesOf("tampered body");
        await Assert.ThrowsAsync<YtdlpVerificationException>(() =>
            YtdlpManager.InstallVerifiedAsync(
                input, TargetPath, VersionFilePath, new string('0', 64), "2026.08.19", CancellationToken.None));

        Assert.Equal("existing install", await File.ReadAllTextAsync(TargetPath));
        Assert.False(File.Exists(TargetPath + ".tmp"));
        Assert.False(File.Exists(VersionFilePath));
    }

    [Fact]
    public async Task InstallVerifiedAsync_Cancelled_KeepsExistingExeAndLeavesNoTemp()
    {
        await File.WriteAllTextAsync(TargetPath, "existing install");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        using var input = BytesOf("half downloaded body");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            YtdlpManager.InstallVerifiedAsync(
                input, TargetPath, VersionFilePath, Sha256Of(Encoding.UTF8.GetBytes("half downloaded body")),
                "2026.08.19", cancelled.Token));

        Assert.Equal("existing install", await File.ReadAllTextAsync(TargetPath));
        Assert.False(File.Exists(TargetPath + ".tmp"));
    }

    [Fact]
    public void BuildDownloadUrl_UsesPinnedVersionReleaseAsset()
    {
        var url = YtdlpManager.BuildDownloadUrl("2026.08.19");
        Assert.Contains("releases/download/2026.08.19/yt-dlp.exe", url, StringComparison.Ordinal);
        Assert.DoesNotContain("latest", url, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("2025.01.01", "2025.02.01", -1)]
    [InlineData("2026.08.19", "2026.08.19", 0)]
    [InlineData("2026.08.19", "2025.08.19", 1)]
    [InlineData("2025.01.15.232815", "2025.01.15", 1)]   // 日内修订号更高
    [InlineData(null, "2025.01.01", -1)]                  // 未知版本视为最低
    [InlineData("", "2025.01.01", -1)]
    [InlineData("garbage", "2025.01.01", -1)]             // 无法解析的段按 0 处理
    public void CompareVersions_NumericSegments(string? left, string? right, int expectedSign)
    {
        var actual = YtdlpManager.CompareVersions(left, right);
        Assert.Equal(expectedSign, Math.Sign(actual));
    }

    [Fact]
    public void PinnedManifest_HasVersionAndSha256()
    {
        // 固定版本清单完整性：版本与哈希必须随应用一起固定，不能留空或占位
        Assert.Matches(@"^\d{4}\.\d{2}\.\d{2}", YtdlpManager.PinnedVersion);
        Assert.Equal(64, YtdlpManager.PinnedSha256.Length);
        Assert.DoesNotContain("REPLACE", YtdlpManager.PinnedSha256, StringComparison.OrdinalIgnoreCase);
        // 固定版本必须满足最低支持版本
        Assert.True(YtdlpManager.CompareVersions(YtdlpManager.PinnedVersion, YtdlpManager.MinimumSupportedVersion) >= 0);
    }
}
