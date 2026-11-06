using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class EnvironmentManagerTests
{
    [Fact]
    public void NodeJsPath_ReturnsNonEmptyString()
    {
        var path = EnvironmentManager.NodeJsPath;
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.Contains("node", path, System.StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task EnsureNodeJsAsync_ReturnsPath()
    {
        // 隔离防护：仅在 node 已可用（系统安装或托管副本已下载）时验证查找逻辑，
        // 否则显式跳过——绝不允许"单元测试"触发 30MB 真实下载写进用户 AppData
        Skip.If(!IsNodeAlreadyAvailable(), "Node.js not already available; skipping to avoid a 30MB real download");

        var path = await EnvironmentManager.EnsureNodeJsAsync();
        Assert.False(string.IsNullOrWhiteSpace(path));
    }

    [Fact]
    public void FindBundledNode_ReturnsPath_WhenPackaged()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bundled-node-" + Guid.NewGuid().ToString("N"));
        try
        {
            var nodeDir = Path.Combine(dir, "runtime", "node");
            Directory.CreateDirectory(nodeDir);
            var exe = Path.Combine(nodeDir, "node.exe");
            File.WriteAllText(exe, "stub");

            Assert.Equal(exe, EnvironmentManager.FindBundledNode(dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void FindBundledNode_ReturnsNull_WhenMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bundled-node-" + Guid.NewGuid().ToString("N"));
        Assert.Null(EnvironmentManager.FindBundledNode(dir));
    }

    [Fact]
    public void NodeDownloadUrls_OfficialFirst_ThenMirror_SameVersion()
    {
        Assert.Equal(2, EnvironmentManager.NodeDownloadUrls.Length);
        Assert.StartsWith("https://nodejs.org/", EnvironmentManager.NodeDownloadUrls[0]);
        Assert.StartsWith("https://registry.npmmirror.com/", EnvironmentManager.NodeDownloadUrls[1]);
        Assert.All(EnvironmentManager.NodeDownloadUrls, u => Assert.Contains("v20.18.3", u));
    }

    private static bool IsNodeAlreadyAvailable()
    {
        if (File.Exists(EnvironmentManager.NodeJsPath))
            return true;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir.Trim('"'), "node.exe")));
    }
}
