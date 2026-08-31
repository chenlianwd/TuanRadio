using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>酷狗 20028 风控挑战（本地代理在响应体附加 ssaCode 等字段）。</summary>
/// <param name="EventId">验证事件 ID（ssaCode），用于开启会话桥；裸 status=2 等疑似形状拿不到，为 null，交由探测确认。</param>
/// <param name="Hash">触发挑战的曲目 hash，验证后按它探测恢复。</param>
public sealed record KugouChallenge(string? EventId, string Hash);

/// <summary>/song/url 探测结果。</summary>
public enum KugouProbeStatus
{
    /// <summary>拿到播放地址：当前身份无需验证。</summary>
    Playable,

    /// <summary>命中 20028 风控；<see cref="KugouProbeResult.Challenge"/> 含事件 ID。</summary>
    Challenge,

    /// <summary>非风控型不可播（VIP/版权/无数据），验证无法解决。</summary>
    NoPermission,

    /// <summary>网络或解析失败。</summary>
    Error,
}

public sealed record KugouProbeResult(KugouProbeStatus Status, KugouChallenge? Challenge = null);

/// <summary>验证流程结局。</summary>
public enum KugouVerifyOutcome
{
    /// <summary>验证通过且可播（或本就无需验证）。</summary>
    Verified,

    /// <summary>风控已解除但探测曲目仍不可播（可能需要 VIP 或重新登录）。</summary>
    VerifiedButUnavailable,

    /// <summary>上游要求重新登录确认身份（v_type=38），滑块解决不了。</summary>
    NeedsRelogin,

    /// <summary>等待验证超时。</summary>
    Timeout,

    /// <summary>流程失败（拿不到验证会话/网络异常等）。</summary>
    Failed,
}

/// <summary>
/// 酷狗滑块验证编排（DI 单例）：
/// 检测 20028 风控挑战 → 经本地代理会话桥换取一次性 sessionId（登录态不进 URL）→
/// 打开浏览器验证页 → 轮询 /song/url 直到恢复。
/// 挑战由 KugouMusicService 写入；自动触发由 MainWindowViewModel 订阅；手动入口在设置页。
/// </summary>
public sealed class KugouVerificationService
{
    private const string ProxyBase = "http://127.0.0.1:37251";

    /// <summary>自动弹验证页的冷却：期间即使再次命中风控也不再自动触发。</summary>
    internal TimeSpan AutoTriggerCooldown { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>验证完成轮询的整体预算；首延迟给用户留出注意到弹窗的时间。测试可调快。</summary>
    internal TimeSpan VerifyInitialDelay { get; set; } = TimeSpan.FromSeconds(8);
    internal TimeSpan VerifyPollInterval { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan VerifyWaitBudget { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>无挑战记录时的默认探测 hash（来自真实失败日志，仅用于触发风控判定）。</summary>
    public const string FallbackProbeHash = "EFC98A4B36BE04F144BEFDABF14654B5";

    /// <summary>v_type=38 表示上游要求重新登录确认（kg-login），滑块解决不了。</summary>
    public const int ReloginVerifyType = 38;

    private readonly HttpClient _httpClient;
    private readonly object _gate = new();
    private KugouChallenge? _lastChallenge;
    private DateTimeOffset? _lastAutoTrigger;
    private bool _verificationInProgress;

    /// <summary>最近一次记录的风控挑战（手动验证优先复用它的 hash 探测）。</summary>
    public KugouChallenge? LastChallenge
    {
        get
        {
            lock (_gate)
            {
                return _lastChallenge;
            }
        }
    }

    /// <summary>命中风控挑战时触发（KugouMusicService 解析响应后调用 RecordChallenge）。</summary>
    public event Action<KugouChallenge>? ChallengeDetected;

    public KugouVerificationService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>记录挑战并广播。</summary>
    public void RecordChallenge(KugouChallenge challenge)
    {
        Action<KugouChallenge>? handlers;
        lock (_gate)
        {
            _lastChallenge = challenge;
            handlers = ChallengeDetected;
        }

        if (handlers is null)
            return;

        // 逐个订阅者调用并隔离异常：任一订阅者抛错不得中断其余订阅者，也不得回灌到调用链
        foreach (Action<KugouChallenge> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(challenge);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Kugou challenge subscriber failed");
            }
        }
    }

    /// <summary>自动触发资格：无进行中的验证，且从未触发过或距上次触发已超过冷却。锁内检查并置位。</summary>
    public bool TryBeginAutoTrigger()
    {
        lock (_gate)
        {
            if (_verificationInProgress)
                return false;

            var now = DateTimeOffset.UtcNow;
            if (_lastAutoTrigger != null && now - _lastAutoTrigger < AutoTriggerCooldown)
                return false;

            _lastAutoTrigger = now;
            _verificationInProgress = true;
            return true;
        }
    }

    /// <summary>手动流程开始：绕过冷却，但仍与其它验证互斥。</summary>
    public bool TryBeginManual()
    {
        lock (_gate)
        {
            if (_verificationInProgress)
                return false;

            _verificationInProgress = true;
            return true;
        }
    }

    /// <summary>结束一次验证（无论结局），释放互斥。</summary>
    public void EndVerification()
    {
        lock (_gate)
        {
            _verificationInProgress = false;
        }
    }

    /// <summary>探测 /song/url，判定当前登录态的风控状态。</summary>
    public async Task<KugouProbeResult> DetectChallengeAsync(
        string? cookie,
        string? hash,
        CancellationToken cancellationToken)
    {
        var probeHash = string.IsNullOrWhiteSpace(hash) ? FallbackProbeHash : hash;
        try
        {
            var url = $"{ProxyBase}/song/url?hash={Uri.EscapeDataString(probeHash)}" +
                      $"&quality=128&timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(cookie))
                request.Headers.TryAddWithoutValidation("Authorization", cookie);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);

            var shape = ClassifyPlayUrlResponse(doc.RootElement, out var eventId, out _);
            return shape switch
            {
                KugouPlayUrlShape.Playable => new KugouProbeResult(KugouProbeStatus.Playable),
                KugouPlayUrlShape.Challenge when eventId != null =>
                    new KugouProbeResult(KugouProbeStatus.Challenge, new KugouChallenge(eventId, probeHash)),
                KugouPlayUrlShape.Challenge => new KugouProbeResult(KugouProbeStatus.Challenge),
                _ => new KugouProbeResult(KugouProbeStatus.NoPermission),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Kugou verification probe failed");
            return new KugouProbeResult(KugouProbeStatus.Error);
        }
    }

    /// <summary>
    /// 完整验证编排：探测 → 会话桥 → （v_type=38 直接判需重登）→ 打开验证页 → 轮询恢复。
    /// 调用方须先通过 TryBeginAutoTrigger/TryBeginManual 取得互斥，结束后调用 EndVerification。
    /// </summary>
    /// <param name="cookie">完整酷狗登录态（token/userid/dfid 组合串）。</param>
    /// <param name="hash">探测/恢复用的曲目 hash；空则用最近挑战或内置默认值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="onPageOpened">验证页打开成功后的回调（设置页用于更新状态文案）。</param>
    public async Task<KugouVerifyOutcome> RunVerificationAsync(
        string? cookie,
        string? hash,
        CancellationToken cancellationToken,
        Action? onPageOpened = null)
    {
        if (string.IsNullOrEmpty(cookie))
            return KugouVerifyOutcome.Failed;

        var probeHash = string.IsNullOrWhiteSpace(hash) ? LastChallenge?.Hash : hash;
        try
        {
            var probe = await DetectChallengeAsync(cookie, probeHash, cancellationToken);
            if (probe.Status == KugouProbeStatus.Playable)
                return KugouVerifyOutcome.Verified;
            if (probe.Challenge?.EventId == null)
            {
                // 疑似形状（裸 status=2 等）探测后仍拿不到事件 ID：滑块流程无从开启，
                // 快速失败交回冷却；日志写明原因，避免被当成可修复的验证故障排查
                Log.Information(
                    "Kugou verification aborted: probe status {Status} carries no challenge event id",
                    probe.Status);
                return KugouVerifyOutcome.Failed;
            }

            var eventId = probe.Challenge.EventId;
            var sessionId = await StartBridgeSessionAsync(cookie, eventId, cancellationToken);
            if (string.IsNullOrEmpty(sessionId))
            {
                Log.Information("Kugou verify bridge session could not be started for event {EventId}",
                    SensitiveDataSanitizer.Sanitize(eventId));
                return KugouVerifyOutcome.Failed;
            }

            var verifyType = await GetVerifyTypeAsync(sessionId, cancellationToken);
            if (verifyType == ReloginVerifyType)
                return KugouVerifyOutcome.NeedsRelogin;

            var url = VerifyPageUrl(sessionId!);
            if (TryOpenInBrowser(url))
            {
                onPageOpened?.Invoke();
            }
            else
            {
                // 自动打开失败时把 URL 留在日志里供手动访问；手动入口由设置页展示
                Log.Warning("Kugou verify page could not be opened automatically; manual URL: {Url}", url);
            }

            return await WaitUntilVerifiedAsync(cookie, probe.Challenge.Hash, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return KugouVerifyOutcome.Failed;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kugou verification flow failed");
            return KugouVerifyOutcome.Failed;
        }
    }

    /// <summary>轮询探测直到风控解除、明确不可播或超时。</summary>
    public async Task<KugouVerifyOutcome> WaitUntilVerifiedAsync(
        string cookie,
        string hash,
        CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.UtcNow;
        await Task.Delay(VerifyInitialDelay, cancellationToken);
        while (true)
        {
            var probe = await DetectChallengeAsync(cookie, hash, cancellationToken);
            if (probe.Status == KugouProbeStatus.Playable)
                return KugouVerifyOutcome.Verified;
            if (probe.Status == KugouProbeStatus.NoPermission)
                return KugouVerifyOutcome.VerifiedButUnavailable;

            if (DateTimeOffset.UtcNow - start >= VerifyWaitBudget)
                return KugouVerifyOutcome.Timeout;

            await Task.Delay(VerifyPollInterval, cancellationToken);
        }
    }

    /// <summary>会话桥：用完整登录态换一次性 sessionId（Authorization 头进代理，token 不进 URL）。</summary>
    public async Task<string?> StartBridgeSessionAsync(
        string cookie,
        string eventId,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{ProxyBase}/verify/bridge/start?eventid={Uri.EscapeDataString(eventId)}" +
                      $"&timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("Authorization", cookie);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("data", out var data) &&
                   data.TryGetProperty("sessionId", out var id) &&
                   id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Kugou verify bridge start failed");
            return null;
        }
    }

    /// <summary>查询验证类型；<see cref="ReloginVerifyType"/> 表示上游要求重新登录确认。</summary>
    public async Task<int?> GetVerifyTypeAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{ProxyBase}/verify/bridge/info?sessionId={Uri.EscapeDataString(sessionId)}" +
                      $"&timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var body = await _httpClient.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("data", out var data) &&
                   data.TryGetProperty("v_type", out var type) &&
                   type.ValueKind == JsonValueKind.Number &&
                   type.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Kugou verify type query failed");
            return null;
        }
    }

    /// <summary>浏览器验证页地址（URL 仅含一次性 sessionId，无任何登录态）。</summary>
    public static string VerifyPageUrl(string sessionId)
        => $"{ProxyBase}/verify_auto.html?session={Uri.EscapeDataString(sessionId)}";

    /// <summary>用系统默认浏览器打开验证页。</summary>
    public static bool TryOpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open browser for kugou verification");
            return false;
        }
    }

    /// <summary>/song/url 响应体的风控分类。</summary>
    public enum KugouPlayUrlShape
    {
        /// <summary>status=1 且 data 内含可用播放地址。</summary>
        Playable,

        /// <summary>命中 20028 风控（含代理附加的 ssaCode，或 errcode=20028 但未附带事件）。</summary>
        Challenge,

        /// <summary>其余失败（status=1 无数据 / status=2 带非 20028 错误 / status=0）。</summary>
        Unavailable,
    }

    /// <summary>
    /// 分类 /song/url 响应体。兼容四种已观测形状：
    /// 1) 正常成功（status=1 + data 内 URL）；
    /// 2) 20028 挑战且代理附加了 ssaCode/sid/edt（eventid 可得）；
    /// 3) errcode=20028 但无 ssaCode（事件 ID 缺失，只能等待探测确认）；
    /// 4) 裸 status=2（无 error 无 data，疑似挑战但无事件）与非 20028 失败。
    /// </summary>
    /// <param name="root">响应体根元素。</param>
    /// <param name="eventId">命中挑战时输出事件 ID（ssaCode），可能为 null（疑似形状）。</param>
    /// <param name="playUrl">可播时输出播放地址。</param>
    public static KugouPlayUrlShape ClassifyPlayUrlResponse(JsonElement root, out string? eventId, out string? playUrl)
    {
        eventId = null;
        playUrl = null;

        var status = TryGetInt32(root, "status") ?? -1;
        var errcode = TryGetInt32(root, "errcode", "error_code", "code");

        // 代理在上游返回 ssa-code 头时附加的字段；errcode=20028 为风控挑战本体
        if (root.TryGetProperty("ssaCode", out var ssa) && ssa.ValueKind == JsonValueKind.String)
            eventId = ssa.GetString();
        if (eventId != null || errcode == 20028)
            return KugouPlayUrlShape.Challenge;

        if (status == 2 && !HasErrorText(root) && errcode == null && !root.TryGetProperty("data", out _))
            return KugouPlayUrlShape.Challenge; // 裸 status=2：疑似风控变体，事件 ID 缺失，交由探测确认

        if (status != 1)
            return KugouPlayUrlShape.Unavailable;

        if (root.TryGetProperty("data", out var data))
        {
            // v5/url 成功响应的 data 为数组（或单对象），与 KugouMusicService 的提取口径一致
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    playUrl = KugouMusicService.ExtractPlayUrl(item);
                    if (playUrl != null)
                        break;
                }
            }
            else
            {
                playUrl = KugouMusicService.ExtractPlayUrl(data);
            }
        }

        return playUrl != null ? KugouPlayUrlShape.Playable : KugouPlayUrlShape.Unavailable;
    }

    private static bool HasErrorText(JsonElement root)
    {
        foreach (var name in new[] { "error", "error_msg", "message", "msg" })
        {
            if (root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(value.GetString()))
                return true;
        }

        return false;
    }

    private static int? TryGetInt32(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var number))
                return number;
        }

        return null;
    }
}
