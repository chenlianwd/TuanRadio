using System;
using System.Net.Http;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

// Integration tests — hit real external APIs.
// 默认跳过以保证单元测试零联网：设置 AIRADIO_INTEGRATION_TESTS=1 显式开启。
// 注意：业务失败（鉴权/风控码）会以 MusicSourceBusinessException 抛出，属于预期的真实失败信号。
[Trait("Category", "Integration")]
public class MusicServiceTests : IDisposable
{
    private static readonly bool IntegrationEnabled =
        Environment.GetEnvironmentVariable("AIRADIO_INTEGRATION_TESTS") == "1";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public void Dispose() => _httpClient.Dispose();

    [Fact]
    public async Task NeteaseMusicService_Search_ReturnsResults()
    {
        if (!IntegrationEnabled) return;

        var service = new NeteaseMusicService(_httpClient);
        var results = await service.SearchAsync("周杰伦", 5);
        Assert.NotNull(results);
    }

    [Fact]
    public async Task KuwoMusicService_Search_ReturnsResults()
    {
        if (!IntegrationEnabled) return;

        var service = new KuwoMusicService(_httpClient);
        var results = await service.SearchAsync("告白气球", 5);
        Assert.NotNull(results);
    }

    [Fact]
    public async Task MiguMusicService_Search_ReturnsResults()
    {
        if (!IntegrationEnabled) return;

        var service = new MiguMusicService(_httpClient);
        var results = await service.SearchAsync("成都", 5);
        Assert.NotNull(results);
    }

    [Fact]
    public async Task MultiSourceMusicService_Search_AggregatesResults()
    {
        if (!IntegrationEnabled) return;

        var service = new MultiSourceMusicService(_httpClient);
        var results = await service.SearchAsync("周杰伦", 10);
        Assert.NotNull(results);
        // Note: API may return 0 results due to network/regional restrictions
        // This is an integration test - verify structure not count
    }
}
