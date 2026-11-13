using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services.Music;
using Xunit;

namespace AIRadio.Desktop.Tests;

public sealed class CustomRadioStorageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storageFile;

    public CustomRadioStorageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", "CustomRadioStorage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _storageFile = Path.Combine(_tempDir, "custom_stations.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task CrudLifecycle_WorksEndToEnd()
    {
        using var storage = new CustomRadioStorage(_storageFile);

        var station = new RadioStation
        {
            StationUuid = RadioStation.CreateCustomUuid(),
            Name = "Campus Jazz",
            StreamUrl = "https://radio.example.com/jazz.mp3",
            Tags = "Jazz, Campus",
            Codec = "MP3",
            Bitrate = 192
        };

        Assert.True(await storage.AddStationAsync(station));
        var loaded = await storage.LoadStationsAsync();
        Assert.Single(loaded);
        Assert.Equal("Campus Jazz", loaded[0].Name);
        Assert.True(loaded[0].IsCustom);

        var updated = new RadioStation
        {
            StationUuid = station.StationUuid,
            Name = "Campus Jazz HD",
            StreamUrl = "https://radio.example.com/jazz-hd.aac",
            Tags = "Jazz, HD",
            Codec = "AAC",
            Bitrate = 320
        };
        Assert.True(await storage.UpdateStationAsync(updated));
        loaded = await storage.LoadStationsAsync();
        Assert.Single(loaded);
        Assert.Equal("Campus Jazz HD", loaded[0].Name);
        Assert.Equal("AAC", loaded[0].Codec);
        Assert.Equal(320, loaded[0].Bitrate);

        Assert.True(await storage.RemoveStationAsync(station.StationUuid));
        loaded = await storage.LoadStationsAsync();
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task AddStation_DeduplicatesNormalizedStreamUrl()
    {
        using var storage = new CustomRadioStorage(_storageFile);

        var first = new RadioStation
        {
            StationUuid = RadioStation.CreateCustomUuid(),
            Name = "Station A",
            StreamUrl = "https://stream.example.com/live/"
        };
        var duplicate = new RadioStation
        {
            StationUuid = RadioStation.CreateCustomUuid(),
            Name = "Station A Copy",
            StreamUrl = "HTTPS://STREAM.EXAMPLE.COM/LIVE"
        };

        Assert.True(await storage.AddStationAsync(first));
        Assert.False(await storage.AddStationAsync(duplicate));

        var batchAdded = await storage.AddStationsAsync(
        [
            duplicate,
            new RadioStation { StationUuid = RadioStation.CreateCustomUuid(), Name = "Station B", StreamUrl = "https://stream.example.com/b" },
            new RadioStation { StationUuid = RadioStation.CreateCustomUuid(), Name = "Station B Dup", StreamUrl = "https://stream.example.com/b/" }
        ]);

        Assert.Equal(1, batchAdded);
        var loaded = await storage.LoadStationsAsync();
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public async Task LoadStations_HealsCorruptJsonFileGracefully()
    {
        await File.WriteAllTextAsync(_storageFile, "{ invalid json !!!");
        using var storage = new CustomRadioStorage(_storageFile);

        var loaded = await storage.LoadStationsAsync();
        Assert.Empty(loaded);

        var backups = Directory.GetFiles(_tempDir, "custom_stations.json.corrupt.*");
        Assert.Single(backups);
    }
}
