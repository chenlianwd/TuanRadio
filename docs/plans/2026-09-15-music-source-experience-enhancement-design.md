# 音源体验增强设计（播放回退择优 + 失败可读分类）

日期：2026-09-15
状态：已实施（2026-09-15 按方案两阶段落地：枚举/七处标注/聚合透传/状态行渲染/回退择优 + 单测；文档同步。503/503 测试通过。实施与设计一致，一处实现层发现：AddSearchReport 脱敏时重建 record 必须显式携带 FailureKind，否则分类在入库时丢失——已修复并成为设计约束写入 §3.3）
结论来源：用户三项决策（评分=仅播放回退择优、分类=结构化枚举、展示=搜索状态行）；代码调研确认回退候选两遍 FirstOrDefault 无质量排序（有目标时长未用）、`MusicSourceBusinessException` 无结构化类型（UI 显示技术拼接文本）、酷狗风控自动验证只覆盖播放路径不覆盖搜索路径。

## 1. 目标与非目标

**目标**

- 播放回退候选按质量择优：身份匹配两遍次序不变，遍内按时长接近度选最优（截断/live 版误选概率下降）。
- 音源失败结构化分类：`MusicSourceFailureKind` 枚举，业务异常携带类型，聚合层透传，搜索状态行按类型渲染用户可读文案 + 恢复建议。
- 分类文案诚实联动现状：酷狗风控自动验证只覆盖播放路径，搜索路径的风控提示给手动指引（不虚报"已自动打开验证"）。

**非目标（本轮不做）**

- 搜索结果列表整体重排：各源已有自身相关性排序，跨源重排破坏源优先级语义且无收益信号。
- 搜索跨源去重择优：信号弱（无目标时长），维持源优先级现状。
- 设置页逐源连接诊断不属于本轮；已于 2026-09-15 的后续产品化清理中实施，现由 `MusicSourceBroker.DiagnoseAsync` 提供。
- 试听片段检测前移到搜索阶段：需解析全部候选 URL，成本不可接受；播放期"明确试听流跨源回退"已兜底。
- **播放 URL 解析路径的失败提示改造**：播放回退的业务异常会携带 Kind（异常对象上），但其用户提示走 `PlaybackRecovery` 既有链路（`HasPlaybackRecoveryFailure`/`PlaybackRecoveryMessage`），本轮不动——搜索状态行（用户决策范围）与播放提示是两条展示链路，避免顺手扩大。

## 2. 播放回退候选择优

**现状**：`TryResolveFallbackCandidateAsync`（`MultiSourceMusicService.cs:413`）两遍匹配——第一遍 `IsSameSongLoose` 精确宽松、第二遍 `StripTitleDecorations` 剥修饰——每遍 `FirstOrDefault` 取首个。源内候选顺序 = 各源自身相关性排序，但同一遍内无质量比较。

**评分设计**（只改遍内选择，两遍次序与源间优先级不变）：

```
durationScore(candidate)：
  track.DurationMs <= 0（目标无时长）→ 全体同分，取首个（与现状一致）
  candidate.DurationMs <= 0（候选缺时长）→ −1
  否则 → max(0, 1 − |candidate.DurationMs − target| / target)
```

每遍在通过该遍匹配判定的候选中取 `durationScore` 最高者；并列取源内原始顺序首个（保留源相关性排序为 tie-breaker）。示例：目标 4:00，候选完整版 4:05（0.98）胜过 live 版 5:30（0.625）。

**跨源不比时长（有意取舍）**：外层按源优先级逐个等待、"高优先级源命中即停"是预算效率设计（不必等低优先级挂起源跑满超时）。因此低时长接近度的高优先级源候选仍会胜过高接近度的低优先级源候选——评分只在**源内**生效，不做"等全源搜索完成再全局选最优"（那会让每次回退都顶满预算）。实施时不得改变外层命中即停的结构。

**边界**：`DurationMs` 是元数据时长（试听只是播放流截断，元数据仍是完整时长），因此时长接近度防的是"截断版/live 版/错曲"而非试听——与播放期回退兜底职责不重叠。

## 3. 音源失败结构化分类

### 3.1 枚举与异常（`IMusicSearchService.cs`，与现有异常同处）

```csharp
public enum MusicSourceFailureKind
{
    None,          // 非业务失败（超时/传输故障走现有 timeout/failed 状态，不分类）
    NotSignedIn,   // 未登录（酷狗搜索/歌单）
    AuthExpired,   // 业务码异常：登录态或本地代理可能失效（网易 code≠200、酷狗 status≠1 非 20028）
    RiskControl,   // 酷狗风控验证（error_code 20028）
    ApiBroken,     // 网页接口/工具链失效（酷我、咪咕、YouTube yt-dlp）
    Unknown
}

public class MusicSourceBusinessException : Exception
{
    public MusicSourceFailureKind Kind { get; }
    public MusicSourceBusinessException(string message,
        MusicSourceFailureKind kind = MusicSourceFailureKind.Unknown) : base(message)
    {
        Kind = kind;
    }
}
```

### 3.2 抛出点标注（全部既有 throw 处，仅加第二参）

| 抛出点 | Kind |
| --- | --- |
| `KugouMusicService` 未登录（:62） | NotSignedIn |
| `KugouMusicService` status≠1（:87） | error_code==20028 → RiskControl，否则 AuthExpired |
| `NeteaseMusicService` code≠200（:55） | AuthExpired |
| `KuwoMusicService`（:50）/ `MiguMusicService`（:43） | ApiBroken |
| `KugouPlaylistService` 未登录（:218） | NotSignedIn |
| `KugouPlaylistService` status≠1（:274） | error_code==20028 → RiskControl，否则 AuthExpired |
| `YouTubeMusicService`（:58/:102，包装异常消息） | ApiBroken |

### 3.3 聚合层透传（`MultiSourceMusicService`）

`SourceSearchStatus` 追加 `MusicSourceFailureKind FailureKind = MusicSourceFailureKind.None`（默认值保住既有构造调用点）。`SearchWithFallback` 的 `catch (Exception ex)` 里：`ex is MusicSourceBusinessException b → FailureKind = b.Kind`（非业务异常保持 None）。

**实现约束**：`AddSearchReport` 入库前会脱敏重建 record——重建时必须显式携带 `status.FailureKind`，否则分类在入库环节被静默丢弃（实施时已踩过此坑，测试锚定）。

### 3.4 UI 渲染（`PlaylistViewModel.FormatSourceStatus`）

`failed` 分支按 Kind 渲染（zh/en 双语，原文 `Error` 保留在 record 供日志）：

| Kind | 文案（zh） |
| --- | --- |
| NotSignedIn | {源}未登录，请到设置的音源账号扫码登录 |
| AuthExpired | {源}登录态或本地服务异常，请到设置检查音源账号后重试 |
| RiskControl | {源}触发风控验证，请到设置点击「滑块验证」；播放时会尝试自动弹出验证页（约 10 分钟内不重复弹） |
| ApiBroken | {源}接口暂时不可用，已跳过 |
| Unknown/None | 现状：{源}失败:{Error} |

**文案措辞约束**：`AuthExpired` 同时覆盖"酷狗登录态失效"（重新扫码可解）与"网易 code≠200"（常见原因是本地 Node 代理未就绪，扫码无效）两类场景——同 Kind 同文案，必须取两者皆不误导的措辞，不得写"重新扫码"这类只对一半场景有效的精确动作。**诚实性约束**：自动滑块验证（`RecordChallenge` → 弹浏览器）只挂在播放 URL 路径（`KugouMusicService.cs:250-258`），搜索路径的 20028 不触发自动验证；播放路径的自动弹窗也有约 10 分钟冷却——风控文案以手动指引为主，自动行为描述必须带频率限制说明。

## 4. 测试计划

**MultiSourceMusicServiceTests（扩展）**
- 回退择优：同遍多候选选时长最接近者；目标时长缺失取首个（现状不变）；候选缺时长劣后；第一遍命中优先于第二遍（次序不变）；并列取源内首个。
- 分类透传：fake 源抛带 Kind 的 `MusicSourceBusinessException` → `SourceSearchStatus.FailureKind` 透传。

**FormatSourceStatus 渲染（internal static 可直测）**
- 各 Kind 的 zh/en 文案；Unknown 回退到现状"失败:{Error}"；Kind=None 的 failed（非业务异常）维持现状。

**KugouMusicServiceTests（扩展）**
- status≠1 且 error_code=20028 → RiskControl；非 20028 → AuthExpired；未登录 → NotSignedIn。

**NeteaseMusicService / Kuwo / Migu**：既有测试文件按现状扩展断言 Kind（视现有覆盖酌情）。

## 5. 实施拆分

1. **阶段 1（单轮可交付）**：枚举 + 异常扩展 + 七处抛出点标注 + 聚合透传 + FormatSourceStatus 渲染 + 回退择优 + 单测 + build/test。
2. **阶段 2**：文档同步（README、ai-radio-plan、AGENTS.md、本设计文档状态）。

## 6. 风险与对策

| 风险 | 对策 |
| --- | --- |
| 时长接近度误伤（专辑版/单曲版时长本就略差） | 评分只做同遍内择优，两遍匹配次序不变；并列回退源内顺序 |
| 跨源评分误解为全局择优 | §2 明确"跨源不比时长"取舍与理由，实施不得改变外层命中即停结构 |
| 分类文案与实际恢复动作不符（风控自动验证未覆盖搜索路径、播放弹窗有冷却） | 文案给手动指引，自动行为描述带频率限制；AuthExpired 取两类场景皆不误导的措辞 |
| 上游业务码语义变化导致分类错标 | Unknown 兜底显示原文案，分类错误只影响提示语不影响熔断/播放行为 |
| `SourceSearchStatus` 加字段破坏既有调用 | 带默认值参数，record 位置参数兼容（既有 `new SourceSearchStatus(name, status, count, error)` 不变） |

## 7. 评审记录

**第 1 轮（对照代码核实 + 设计推演，修正 4 处）**：事实性声明全部核实通过（七处抛出点、酷狗 error_code 可用性、record 默认参数兼容、自动验证只挂播放路径）。补"跨源不比时长、只源内择优"的有意取舍说明——外层"高优先级源命中即停"是预算效率设计，等全源再全局选最优会让每次回退顶满预算，实施不得改变该结构。AuthExpired 文案从"请到设置重新扫码"改为"登录态或本地服务异常，请到设置检查"——该 Kind 同时覆盖酷狗登录态失效（扫码可解）与网易 code≠200（常见为本地代理未就绪，扫码无效），同 Kind 同文案必须两者皆不误导。风控文案的自动弹窗描述补约 10 分钟冷却说明。播放 URL 解析路径的失败提示走 PlaybackRecovery 既有链路，明确划出本轮范围（非目标），防实施时顺手扩大。

**第 2 轮（实施后代码评审，无缺陷）**：9/9 异常构造点携带显式 Kind；择优语义与设计逐条一致（严格大于保留并列首个、目标无时长全体同分退化为现状、候选缺时长记 −1、两遍次序与外层命中即停未动）；`AddSearchReport` 脱敏重建保 `FailureKind`（实施期由分类透传测试当场抓出的丢失问题，已修复并写入 §3.3 约束）；`AnnotateReport` 的 `with` 表达式天然保留新字段；KugouPlaylistService 的 `errorCode == 20028` 为 `int?` 提升比较（null→false）正确；文案渲染的 Unknown/None 回退与语言守卫均有测试锚定。实施期另修一处测试辅助方法的自伤缺陷（helper 内 `using` HttpClient 会在返回后释放导致请求失败）。
