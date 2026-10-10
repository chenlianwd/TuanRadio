using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services.Music;

/// <summary>重定向探测结论。</summary>
public enum RedirectWalkResult
{
    /// <summary>跟随完重定向链到达 2xx，全程每跳校验通过。</summary>
    Verified,
    /// <summary>某跳目标未过策略校验或跳数超上限——解析必须拒绝（TransportRejected 口径）。</summary>
    Rejected,
    /// <summary>探测不可得（4xx/5xx、连接异常、3xx 缺 Location）：无法证实违规，放行原 URL。</summary>
    Inconclusive
}

/// <summary>
/// 播放 URL 重定向逐跳复检（MediaUriPolicy 的收尾补丁）：解析期主动跟随 3xx 链，
/// 每一跳经委托校验（公网源走 MediaUriPolicy、私有源走同源校验），观察到的违规跳转即拒绝解析。
/// 只做校验不替换播放 URL——LibVLC 仍拿原始地址。
/// 探测是"观察型校验"而非门禁：超时、连接异常、4xx/5xx、缺 Location 一律放行原 URL
/// （Inconclusive）——探测结果依赖网络状况，不能因探测不可得惩罚播放；残余风险是
/// 恶意源停滞探测规避 + 探测后改变跳转行为的 TOCTOU 窗口（文档已明示）。
/// 探测请求 GET + Range: bytes=0-0 + ResponseHeadersRead，响应头到手即弃；
/// 整体由调用方以独立短预算（WaitAsync + CancelAfter）约束，不拖慢解析主链路。
/// </summary>
public sealed class PlaybackRedirectGuard
{
    private const int MaxRedirectHops = 5;

    private readonly HttpClient _client;

    /// <param name="client">必须 AllowAutoRedirect=false（跟随逻辑由本类手动完成）。</param>
    public PlaybackRedirectGuard(HttpClient client) => _client = client;

    public async Task<RedirectWalkResult> VerifyAsync(
        Uri initial,
        Func<Uri, CancellationToken, Task<bool>> hopValidator,
        CancellationToken cancellationToken)
    {
        var current = initial;
        try
        {
            for (var hops = 0; ; hops++)
            {
                if (hops > MaxRedirectHops)
                {
                    Log.Warning("Redirect walk exceeded {Max} hops for {Url}", MaxRedirectHops, initial);
                    return RedirectWalkResult.Rejected;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                // Range 单字节：演进计划已实测 Audius 302→内容节点 Range 206 先例，负载最小
                request.Headers.Range = new RangeHeaderValue(0, 0);
                using var response = await _client.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return RedirectWalkResult.Verified;

                var code = (int)response.StatusCode;
                if (code is < 300 or > 399)
                {
                    // 4xx/5xx：CDN 可能拒绝裸 GET，探测不可得不构成拒绝依据
                    Log.Debug("Redirect probe got {Code} for {Url}; allowing original", code, initial);
                    return RedirectWalkResult.Inconclusive;
                }

                var location = response.Headers.Location;
                if (location == null)
                {
                    Log.Debug("Redirect {Code} without Location for {Url}; allowing original", code, initial);
                    return RedirectWalkResult.Inconclusive;
                }

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!await hopValidator(next, cancellationToken).ConfigureAwait(false))
                {
                    Log.Warning("Redirect hop {Hop} to {Target} rejected by policy for {Url}",
                        hops + 1, next, initial);
                    return RedirectWalkResult.Rejected;
                }

                current = next;
            }
        }
        catch (OperationCanceledException)
        {
            // 探测预算超时：观察不到跳转，不构成拒绝依据（恶意源停滞探测的规避是已接受的残余风险）
            Log.Debug("Redirect probe timed out for {Url}; allowing original", initial);
            return RedirectWalkResult.Inconclusive;
        }
        catch (Exception ex)
        {
            // 连接被拒/DNS 失败等传输异常：探测不可得，放行原 URL
            Log.Debug(ex, "Redirect probe failed for {Url}; allowing original", initial);
            return RedirectWalkResult.Inconclusive;
        }
    }
}
