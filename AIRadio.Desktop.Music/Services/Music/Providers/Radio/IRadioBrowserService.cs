using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 全球开源网络电台服务契约（Radio-Browser API 与公开电台兜底库）。
/// </summary>
public interface IRadioBrowserService
{
    /// <summary>
    /// 获取指定流派分类的电台列表（支持 15 分钟内存缓存）。
    /// </summary>
    Task<IReadOnlyList<RadioStation>> GetStationsByCategoryAsync(
        string category,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 模糊搜索电台（按电台名称、流派标签或国家）。
    /// </summary>
    Task<IReadOnlyList<RadioStation>> SearchStationsAsync(
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析指定电台的最新有效播放流地址。
    /// </summary>
    Task<string?> ResolveStationUrlAsync(
        string stationUuid,
        CancellationToken cancellationToken = default);
}
