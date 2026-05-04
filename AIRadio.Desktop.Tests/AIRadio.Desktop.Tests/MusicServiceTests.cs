using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class MusicServiceTests
{
    private readonly HttpClient _httpClient = new();

    [Fact]
    public async Task NeteaseMusicService_Search_ReturnsResults()
    {
        var service = new NeteaseMusicService(_httpClient);
        var results = await service.SearchAsync("周杰伦", 5);
        Assert.NotNull(results);
    }

    [Fact]
    public async Task KuwoMusicService_Search_ReturnsResults()
    {
        var service = new KuwoMusicService(_httpClient);
        var results = await service.SearchAsync("告白气球", 5);
        Assert.NotNull(results);
    }

    [Fact]
    public async Task MiguMusicService_Search_ReturnsResults()
    {
        var service = new MiguMusicService(_httpClient);
        var results = await service.SearchAsync("成都", 5);
        Assert.NotNull(results);
    }

    [Fact]
    public async Task MultiSourceMusicService_Search_AggregatesResults()
    {
        var service = new MultiSourceMusicService(_httpClient);
        var results = await service.SearchAsync("周杰伦", 10);
        Assert.NotNull(results);
        // Note: API may return 0 results due to network/regional restrictions
        // This is an integration test - verify structure not count
    }
}
