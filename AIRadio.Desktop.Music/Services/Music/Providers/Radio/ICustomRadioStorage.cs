using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 用户自定义网络电台本地持久化存储服务。
/// </summary>
public interface ICustomRadioStorage
{
    Task<IReadOnlyList<RadioStation>> LoadStationsAsync(CancellationToken ct = default);
    Task SaveStationsAsync(IReadOnlyList<RadioStation> stations, CancellationToken ct = default);
    Task<bool> AddStationAsync(RadioStation station, CancellationToken ct = default);
    Task<int> AddStationsAsync(IEnumerable<RadioStation> stations, CancellationToken ct = default);
    Task<bool> UpdateStationAsync(RadioStation station, CancellationToken ct = default);
    Task<bool> RemoveStationAsync(string stationUuid, CancellationToken ct = default);
}
