using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 电台查询结果（包含电台列表与是否为离线兜底标记）。
/// </summary>
public sealed record RadioStationQueryResult(
    IReadOnlyList<RadioStation> Stations,
    bool IsOfflineFallback);

/// <summary>
/// 全球开源网络电台服务契约（Radio-Browser API 与离线精选库）。
/// </summary>
public interface IRadioBrowserService
{
    /// <summary>
    /// 获取指定流派分类的电台列表（支持 15 分钟内存缓存）。
    /// </summary>
    Task<RadioStationQueryResult> GetStationsByCategoryAsync(
        string category,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 模糊搜索电台（按电台名称、流派标签或国家）。
    /// </summary>
    Task<RadioStationQueryResult> SearchStationsAsync(
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析指定电台的最新有效播放流地址（离线或在线）。
    /// </summary>
    Task<string?> ResolveStationUrlAsync(
        string stationUuid,
        CancellationToken cancellationToken = default);
}
