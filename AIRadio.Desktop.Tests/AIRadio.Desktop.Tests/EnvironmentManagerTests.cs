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

    [Fact]
    public async Task EnsureNodeJsAsync_ReturnsPath()
    {
        // 隔离防护：仅在 node 已可用（系统安装或托管副本已下载）时验证查找逻辑，
        // 否则跳过——绝不允许"单元测试"触发 30MB 真实下载写进用户 AppData
        if (!IsNodeAlreadyAvailable())
            return;

        var path = await EnvironmentManager.EnsureNodeJsAsync();
        Assert.False(string.IsNullOrWhiteSpace(path));
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
