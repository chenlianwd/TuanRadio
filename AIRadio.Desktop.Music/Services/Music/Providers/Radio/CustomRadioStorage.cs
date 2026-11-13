using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 自定义网络电台本地 JSON 持久化实现（线程安全、原子写盘、损坏自愈与 URL 去重）。
/// </summary>
public sealed class CustomRadioStorage : ICustomRadioStorage, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string DefaultStorageFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIRadio",
        "custom_stations.json");

    public CustomRadioStorage(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? DefaultStorageFile : filePath;
    }

    public async Task<IReadOnlyList<RadioStation>> LoadStationsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadStationsCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveStationsAsync(IReadOnlyList<RadioStation> stations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stations);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveStationsCoreAsync(stations, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> AddStationAsync(RadioStation station, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(station);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = (await LoadStationsCoreAsync(ct).ConfigureAwait(false)).ToList();
            var normalizedUrl = NormalizeStreamUrl(station.StreamUrl);
            if (string.IsNullOrEmpty(normalizedUrl))
                return false;

            if (list.Any(s => string.Equals(NormalizeStreamUrl(s.StreamUrl), normalizedUrl, StringComparison.OrdinalIgnoreCase)))
                return false;

            list.Insert(0, EnsureCustomIdentity(station));
            await SaveStationsCoreAsync(list, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> AddStationsAsync(IEnumerable<RadioStation> stations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stations);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = (await LoadStationsCoreAsync(ct).ConfigureAwait(false)).ToList();
            var existingUrls = new HashSet<string>(
                list.Select(s => NormalizeStreamUrl(s.StreamUrl)).Where(u => u.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            var addedList = new List<RadioStation>();
            foreach (var station in stations)
            {
                var normalized = NormalizeStreamUrl(station.StreamUrl);
                if (string.IsNullOrEmpty(normalized) || !existingUrls.Add(normalized))
                    continue;

                addedList.Add(EnsureCustomIdentity(station));
            }

            if (addedList.Count == 0)
                return 0;

            list.InsertRange(0, addedList);
            await SaveStationsCoreAsync(list, ct).ConfigureAwait(false);
            return addedList.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UpdateStationAsync(RadioStation station, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(station);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = (await LoadStationsCoreAsync(ct).ConfigureAwait(false)).ToList();
            var index = list.FindIndex(s => string.Equals(s.StationUuid, station.StationUuid, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;

            list[index] = EnsureCustomIdentity(station);
            await SaveStationsCoreAsync(list, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveStationAsync(string stationUuid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stationUuid))
            return false;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = (await LoadStationsCoreAsync(ct).ConfigureAwait(false)).ToList();
            var removed = list.RemoveAll(s => string.Equals(s.StationUuid, stationUuid, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return false;

            await SaveStationsCoreAsync(list, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<RadioStation>> LoadStationsCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return [];

            var records = JsonSerializer.Deserialize<List<CustomStationRecord>>(json, JsonOptions);
            if (records == null)
                return [];

            return records
                .Where(r => !string.IsNullOrWhiteSpace(r.StreamUrl))
                .Select(r => new RadioStation
                {
                    StationUuid = string.IsNullOrWhiteSpace(r.StationUuid)
                        ? RadioStation.CreateCustomUuid()
                        : (r.StationUuid.StartsWith(RadioStation.CustomUuidPrefix, StringComparison.OrdinalIgnoreCase)
                            ? r.StationUuid
                            : RadioStation.CustomUuidPrefix + r.StationUuid),
                    Name = string.IsNullOrWhiteSpace(r.Name) ? r.StreamUrl : r.Name,
                    StreamUrl = r.StreamUrl.Trim(),
                    Tags = r.Tags ?? string.Empty,
                    Country = r.Country ?? string.Empty,
                    Codec = string.IsNullOrWhiteSpace(r.Codec) ? "MP3" : r.Codec,
                    Bitrate = Math.Max(0, r.Bitrate)
                })
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Corrupt custom stations file at {FilePath}; backing up and starting fresh.", _filePath);
            TryBackupCorruptFile();
            return [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load custom stations from {FilePath}", _filePath);
            return [];
        }
    }

    private async Task SaveStationsCoreAsync(IReadOnlyList<RadioStation> stations, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var records = stations.Select(s => new CustomStationRecord(
            EnsureCustomIdentity(s).StationUuid,
            s.Name,
            s.StreamUrl,
            s.Tags,
            s.Country,
            s.Codec,
            s.Bitrate)).ToList();

        var json = JsonSerializer.Serialize(records, JsonOptions);
        var tempPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Move(tempPath, _filePath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(25 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            var backup = $"{_filePath}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(_filePath, backup, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not backup corrupt custom stations file");
        }
    }

    private static RadioStation EnsureCustomIdentity(RadioStation station)
    {
        var uuid = string.IsNullOrWhiteSpace(station.StationUuid)
            ? RadioStation.CreateCustomUuid()
            : (station.StationUuid.StartsWith(RadioStation.CustomUuidPrefix, StringComparison.OrdinalIgnoreCase)
                ? station.StationUuid
                : RadioStation.CustomUuidPrefix + station.StationUuid);

        if (string.Equals(uuid, station.StationUuid, StringComparison.Ordinal))
            return station;

        return new RadioStation
        {
            StationUuid = uuid,
            Name = station.Name,
            StreamUrl = station.StreamUrl,
            Tags = station.Tags,
            Country = station.Country,
            CountryCode = station.CountryCode,
            Codec = station.Codec,
            Bitrate = station.Bitrate,
            Votes = station.Votes,
            ClickCount = station.ClickCount,
            IsFavorite = station.IsFavorite
        };
    }

    internal static string NormalizeStreamUrl(string? url)
        => string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim().TrimEnd('/');

    public void Dispose()
    {
        _gate.Dispose();
    }

    private sealed record CustomStationRecord(
        string StationUuid,
        string Name,
        string StreamUrl,
        string Tags,
        string Country,
        string Codec,
        int Bitrate);
}
