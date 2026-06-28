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
        // Should either find existing node or download it
        var path = await EnvironmentManager.EnsureNodeJsAsync();
        Assert.False(string.IsNullOrWhiteSpace(path));
    }
}
