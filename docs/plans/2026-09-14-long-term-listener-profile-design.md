# 长期用户画像设计（收听画像 + 推荐注入）

日期：2026-09-14
状态：已实施（2026-09-14 三阶段落地：①ListeningProfileService + 采集接线 + 设置页开关/清除；②RecommendationService 画像注入 + digest；③文档同步。实施后代码评审三轮（见 §11 第 3-5 轮），488/488 测试通过。实施与设计一致，仅一处偏差：画像黑名单不走冷启动门槛——Dislike 是显式信号，少量事件即应排除）
结论来源：用户四项决策（构建机制=混合、采集跳过信号、只影响推荐链路、控制=开关+重置）；代码调研确认 `RecommendationService` 会话态全部内存级、`Track`/`OnlineTrack` 无流派/语言/年代元数据、`TrackChanged` 在 `AudioService` 有 8 处触发点（载入/删除/重试/恢复均触发，见 `AudioService.cs` `NotifyTrackChanged` 调用点）、持久化基建（`%APPDATA%\AIRadio` + 临时文件原子替换）可直接复用。

## 1. 目标与非目标

**目标**

- 把现有会话级推荐信号（播放/反馈/氛围）持久化为跨会话的本地收听画像，重启后推荐延续口味。
- 画像 = 确定性本地统计（歌手亲和度、曲目黑名单、氛围历史）+ LLM 周期归纳的口味摘要（digest），LLM 未配置/失败时降级为纯统计，不阻塞推荐。
- 采集跳过信号：一首歌播了不足一小段被手动切歌，记为轻负反馈。
- 设置页提供「学习我的口味」开关与「清除收听画像」重置按钮。

**非目标（本期不做）**

- 跨设备同步、任何云端上传：画像纯本地 JSON。
- DJ 聊天链路注入画像（本轮决策只影响推荐）。
- 画像内容查看 UI（开关 + 重置即可）。
- 流派/语言/年代 tag 体系：元数据层没有这些字段，风格维度全部由 LLM digest 表达。
- 收藏事件进画像：收藏已有独立持久化（`FavoriteIds`）且已通过 `RecommendationRequest.Favorites` 注入推荐，不重复建设。
- 多歌手合作曲拆分聚合（"周杰伦/费玉清" 记为独立歌手键）：与现有 `PickDiversifiedTrack` 的精确 Artist 匹配行为保持一致，拆分留待后续。

## 2. 决策记录

| 决策项 | 结论 |
| --- | --- |
| 构建机制 | 混合：本地统计 + LLM 归纳 digest（用户拍板） |
| 跳过信号 | 采集，<30% 进度切歌记轻负反馈（用户拍板） |
| 作用范围 | 只影响推荐链路：节目单/续播（用户拍板） |
| 用户控制 | 设置页开关 + 重置（用户拍板） |
| 画像归属 | 用户级，跨 DJ 角色共享（DJ 角色只影响人设，不影响口味） |

**实现层默认参数**（编码时可调，验收不改语义）：事件上限 1000 条、衰减半衰期 30 天、Dislike 黑名单 180 天过期/上限 100 条、跳过判定阈值 30% 且已播 ≥10s 且曲目时长 ≥60s、digest 刷新水位 25 条新增或 7 天老化、digest 连续失败 3 次退避 24 小时、落盘防抖 30 秒、画像注入门槛 30 条事件且 3 个歌手。

## 3. 数据模型与存储

### 3.1 文件：`%APPDATA%\AIRadio\listener-profile.json`

与 `settings.json`/`playlist.json` 同目录。写入复用 `PlaylistViewModel.TrySaveAsync` 的模式（`SemaphoreSlim` 保存门 + 同目录 `.tmp` 原子替换，见 `PlaylistViewModel.cs:535`）。**只存事件与 digest，不存派生统计**——统计在加载后由事件重算（1000 条重算是毫秒级），好处：衰减策略调整可追溯生效、单一事实来源、文件更小。

```csharp
public class ListenerProfileData
{
    public int Version { get; set; } = 1;
    public long TotalEventsIngested { get; set; }   // 单调递增，裁剪不失效；digest 水位对齐它
    public List<ListeningEventData> Events { get; set; } = new();
    public TasteDigestData? Digest { get; set; }
}

public class ListeningEventData
{
    public ListeningEventType Type { get; set; }
    public string Title { get; set; } = string.Empty;   // 反范式存储，供 digest 生成
    public string Artist { get; set; } = string.Empty;
    public string? SourceId { get; set; }               // 仅审计用途，匹配走音乐身份
    public DateTime Time { get; set; }
}

public enum ListeningEventType
{
    Played,      // 进入 Playing 态（曝光）
    Completed,   // 自然播完（TrackEnded）
    Skipped,     // 手动切歌且进度 <30%（轻负）
    Like, Dislike, Similar, Calmer, Energetic,  // 对齐现有 MusicFeedbackAction
    MoodSet      // 仅聊天 change_mood 指令（按钮氛围走 Calmer/Energetic 事件）
}

public class TasteDigestData
{
    public string Text { get; set; } = string.Empty;
    public string Language { get; set; } = "zh";        // 生成时 AppLanguage，不符视为过期
    public DateTime GeneratedAt { get; set; }
    public long EventWatermark { get; set; }            // 生成时已覆盖的 TotalEventsIngested
    public int ConsecutiveFailures { get; set; }
}
```

**曲目身份与黑名单键**：黑名单存原始 `Title`/`Artist` 对，匹配走 `MusicIdentity.IsSameMusicIdentity` 现有语义（跨音源身份匹配的既有先例是 `RecommendationService.IsExcluded`，`RecommendationService.cs:543`）。**不**降级为纯归一化字符串键——`IsSameMusicIdentity` 的空歌手通配语义（单边歌手缺失仍可判同曲）会随归一化键丢失。序列化**绝不包含** `CoverArt` 等大二进制字段。

**损坏处理**：解析失败记 `Log.Warning` 后以空画像启动并允许正常覆写——画像数据可再积累，价值低于歌单。**未来版本（Version > 1）**：按空画像运行且**本会话拒绝保存**（对齐 playlist 的 `_futureFormatSkipped` 先例，防降级运行销毁新版数据）。

### 3.2 内存统计（加载时重算，事件到达时增量更新）

```csharp
public class ListenerProfileSnapshot
{
    public IReadOnlyList<ArtistAffinity> TopArtists;       // 按 score 降序
    public IReadOnlyList<ArtistAffinity> AvoidArtists;     // score 显著为负的歌手
    public IReadOnlyList<(string Title, string Artist)> DislikeBlacklist; // 未过期 dislike 曲目
    public IReadOnlyDictionary<string, int> MoodUsage;     // mood → 近 90 天出现次数
    public TasteDigestData? Digest;
}

public class ArtistAffinity
{
    public string Artist;
    public double Score;        // 衰减加权累计，见 §4.2
    public int PlayCount;
    public DateTime LastPlayedAt;
}
```

快照不可变、内部锁保护；`Enabled == false` 时 `GetSnapshot()` 返回空快照。

## 4. 信号采集

### 4.1 事件源与接线（全部在 MainWindowViewModel，沿现有订阅点）

| 事件 | 触发点（现状） | 画像动作 |
| --- | --- | --- |
| Played | `_playbackHistorySub`：`StateChanged == Playing`（`MainWindowViewModel.cs:279`） | 喂 `RecordEvent(Played)`；同曲目 10 分钟窗口去重。注意该订阅在**暂停后恢复**时也会触发，去重窗口即为此而设（超 10 分钟的长曲暂停恢复会多记一次 +0.2，可忽略） |
| Completed | `_trackEndedSub`：`TrackEnded`（仅自然播完链路，`AudioService.cs:519-557`） | 清除跳过待判标记 + `RecordEvent(Completed)` |
| Skipped | 手动切歌检测（见 §4.3） | `RecordEvent(Skipped)` |
| 按钮反馈 | `RecordCurrentTrackFeedback`（`MainWindowViewModel.cs:539`） | 在现有 `RecordFeedback` 旁追加喂画像（Calmer/Energetic 事件本身即 mood 信号，**不再补记 MoodSet**，防双记） |
| MoodSet | 仅 `ChatViewModel.cs:1197` 的 change_mood 路径 | `RecordEvent(MoodSet)`（值为 NormalizeMood 归一化结果） |

会话级语义（`_moodBias`、`_recentlyPlayed`、`_returnedTrackIds`）**保持不变**：画像不替换会话态，只在推荐消费侧叠加。

### 4.2 统计权重与衰减

`weight(event) = baseWeight × 0.5^(ageDays / 30)`：

| 事件 | baseWeight | 说明 |
| --- | --- | --- |
| Played | +0.2 | 曝光 |
| Completed | +1 | 自然播完（与 Played 叠计：完整听一首对歌手贡献 +1.2，被跳过的净 −0.6） |
| Skipped | −0.8 | 轻负，衰减自然恢复 |
| Like | +2 | |
| Dislike | −3 | 且进曲目黑名单 |
| Similar | +1 | "多来点这种"是对当前曲歌手的正信号 |
| Calmer / Energetic / MoodSet | 0 | 纯 mood 信号，不入歌手分 |

歌手分 clamp 到 ≥ −5（单曲 Dislike 走曲目级黑名单，不连坐拉黑歌手）。`AvoidArtists` = score < −1 的歌手，仅用于 digest 输入与提示词「回避」提示，**不硬性排除**（防误伤）。`MoodUsage` 从 Calmer→calm、Energetic→energetic、MoodSet→其值 三类事件统计（近 90 天）。

### 4.3 跳过检测

`TrackChanged` 在 `AudioService` 有 8 处触发点（载入歌单、删除曲目、音源 URL 重试、恢复重放等都会发，见 `MainWindowViewModel.cs:277` 注释与 `NotifyTrackChanged` 调用点），**不能把「收到 TrackChanged」直接当手动切歌**。判定需同时满足：

1. **前曲处于 Playing 态**：`StateChanged == Playing` 时记录 `_lastPlayingTrack`（含开始时间）；`TrackEnded`/`PlaybackState` 离开播放态时清除。载入/删除触发的 TrackChanged 发生在非播放态，不满足此条。
2. **新曲身份 ≠ 前曲身份**：`MusicIdentity.IsSameMusicIdentity` 判同则跳过不记（覆盖 URL 失效后恢复重放同曲的场景）。
3. **进度样本绑定本曲**：订阅 `IAudioService.PositionChanged`（`IObservable<TimeSpan>`，500ms 粒度，歌词模式已在使用）缓存 `(曲目身份 → 最近进度)`；TrackChanged 后样本缓存按新曲身份重置，**无本曲样本不判跳过**（防把上一首的残余样本记到本曲头上；≥10s 阈值本身要求 ≥20 个样本，该约束无副作用）。
4. **阈值**：`进度 / 时长 < 30%` 且已播 ≥10s 且时长 ≥60s → `Skipped`。

选择 TrackChanged 方案而非在 Next/Previous 命令埋点，是为了覆盖所有切歌来源（命令、快捷键、聊天 DJ 指令）。

## 5. LLM 口味摘要（digest）

- **触发**：节目单生成路径内 fire-and-forget 调 `RefreshDigestAsync`（不 await，不阻塞当次推荐——当次用旧 digest，刷新后下次生效）。满足任一才真正调用 LLM：`TotalEventsIngested − watermark ≥ 25`、digest 老化 ≥7 天、Language ≠ 当前 `AppLanguage`；且 `LLMService.IsConfigured()`。
- **防并发/退避**：内部 `SemaphoreSlim(1,1)` + 进行中标志；连续失败 ≥3 次 24 小时内不再尝试（记 `ConsecutiveFailures`）。fire-and-forget 任务自身全异常内吞（`Log.Warning`），不留 unobserved task。
- **语言切换节流**：Language 不匹配触发的重生成**每会话最多一次**（反复切语言不重复烧 LLM 调用，等水位/老化条件自然再生成）。
- **调用**：`ChatRawAsync`（无人设——沿用「DJ 人设会污染结构化输出」的既有教训，`RecommendationService.cs:580`）。输入：TopArtists/AvoidArtists、近 30 首去重曲目（title - artist）、MoodUsage 高频项；输出：3-5 句口味摘要。超时 15 秒。
- **与 Reset 的竞态**：画像持有代次令牌（epoch），`Reset()` 递增代次；归纳完成回写 digest 前校验代次，不匹配则丢弃结果（防过期摘要写回已清空的画像）。
- **失败降级**：digest 缺失/过期时纯统计照常工作，推荐不依赖 digest 存在。

## 6. 推荐链路注入（RecommendationService）

构造函数追加可选依赖，保持单测现有构造兼容：

```csharp
public RecommendationService(
    ILLMService llm,
    IMusicSearchService musicSearch,
    IListeningProfileService? profile = null)
```

DI 注册单例 `IListeningProfileService` 并在 `App.axaml.cs:230` 的 `IRecommendationService` 工厂处传入。`profile == null` 或 `Enabled == false` 或快照为空 → 行为与现状完全一致（回归安全网）。

### 6.1 搜索词生成（`GenerateQueriesAsync`，`RecommendationService.cs:284`）

**注入门槛（冷启动防护）**：快照累计事件 ≥30 且歌手 ≥3 个时才注入画像段，避免基于头几首歌过早下结论污染提示词。门槛未达时 prompt 与现状完全一致。

达标后 prompt 追加（有则注入）：

```
听众口味画像：{digest.Text}
长期常听歌手：{TopArtists 前 5}；长期回避歌手：{AvoidArtists 前 3}
```

并在中英文 prompt 中要求：**3 个关键词中至少 1 个尝试画像之外的相近新方向**（反信息茧房，见 §9；该要求仅在画像段注入时生效，否则提示词自相矛盾）。`BuildFallbackQueries` 同步扩展：`recentHistory` 为空且画像达标时，用 TopArtists 生成「{artist} 相似歌曲」兜底。

### 6.2 排除逻辑（`BuildExcludedTracks` / `IsDisliked`）

在现有会话级 dislike 之上，拼入画像黑名单（`IsSameMusicIdentity` 匹配、未过期项）。已播排除仍以会话级 `_recentlyPlayed` + request 为准，**不用画像长期排除已播歌手**（电台重复播喜欢的歌是合理行为）。

### 6.3 氛围语义

`_moodBias` 会话覆盖语义不变；画像 mood 只作为 digest 文本素材（「常在深夜听安静的歌」），不参与结构化 mood 判定链路。

## 7. 设置页与生命周期

- `SettingsViewModel` 增加 `[Reactive] bool ListenerProfileEnabled`（默认 true），随 `settings.json` 的 `listener_profile_enabled` 持久化（模式同 `ShowLyricsInStage`，`SettingsViewModel.cs:78/420/970`）；**初值在启动加载 settings 完成后同步给 service**（DI 构造顺序不保证 settings 先于画像加载，靠加载完成回调推送，不靠构造时读取）。
- **开关语义**：关闭 = 停止采集（`RecordEvent` no-op）+ 推荐立即回到无画像行为（`GetSnapshot()` 返回空快照）；**已积累数据保留**，重开即恢复。
- **重置按钮**：设置页「清除收听画像」，确认弹层后清内存 + 删 `listener-profile.json` + 递增代次令牌作废在途归纳。
- **落盘时机**：事件到达标 dirty，30 秒防抖批量写；`Dispose` 时有界（2 秒）同步快照写（沿用 SettingsViewModel 关闭给在途保存留短窗口的先例）。
- **加载时机**：DI 构造后异步加载（失败为空画像，不阻塞启动）。**启动推荐可能早于画像加载完成**：首次节目单可能无画像注入——与 digest 滞后刷新同级的可接受降级，不为此同步阻塞启动。

## 8. 测试计划

**ListeningProfileServiceTests（新增）**

- 统计聚合：各事件权重表（含 Calmer/Energetic/MoodSet 零权重）、30 天半衰期衰减、歌手分下限 clamp、Completed+Played 叠计。
- 黑名单：Dislike 入表、180 天过期、上限 FIFO、重置清空。
- 跳过判定：30%/10s/60s 三阈值边界；**同曲身份 TrackChanged 不记跳过**（URL 恢复重放）；非播放态 TrackChanged 不记跳过（载入/删除）；无本曲位置样本不判跳过。
- 事件去重：同曲目 10 分钟窗口 Played 只记一次（含暂停恢复场景）；事件上限 1000 裁剪后 `TotalEventsIngested` 仍单调。
- digest：水位（对齐 TotalEventsIngested 而非列表长度）/老化/语言不匹配触发；语言不匹配每会话只触发一次；连续失败退避；并发刷新只跑一次；**Reset 后在途归纳结果被代次令牌丢弃**。
- 持久化：roundtrip 一致、损坏 JSON 降级空画像不抛、未来版本空画像且拒写。
- Enabled=false：RecordEvent no-op、GetSnapshot 返回空。

**RecommendationServiceTests（扩展）**

- 注入 fake 画像（达标快照）后：生成 prompt 含 digest 与常听歌手、排除集含画像黑名单、第 3 关键词带探索性。
- **冷启动门槛**：事件 <30 或歌手 <3 的快照不注入任何画像段。
- `profile == null`：与现状行为一致（既有用例全部不改即回归）。

## 9. 风险与对策

| 风险 | 对策 |
| --- | --- |
| 信息茧房（画像越推越窄） | 关键词生成强制 1 个探索方向；AvoidArtists 只提示不硬排除；歌手分下限防误杀 |
| LLM 摘要污染搜索词（历史教训：人设台词被当搜索词） | digest 走 ChatRawAsync 无人设；digest 只进提示词、不直接成为搜索词；`SanitizeSearchQueries` 兜底不变 |
| 跳过误判（临时有事切歌） | 轻权重（−0.8）+ 30 天衰减自然恢复；Dislike 才进黑名单 |
| 冷启动噪声（头几首歌定调） | §6.1 注入门槛：≥30 事件且 ≥3 歌手 |
| 画像文件损坏/膨胀 | 损坏降级空画像；事件上限 1000 + 防抖落盘 |
| 关闭窗口丢最后一段事件 | Dispose 有界同步写；画像事件本身低价值，丢一批可接受 |
| 多歌手合作曲聚合失真（"A/B" 记一整体） | 已知限制（§1 非目标），与现有精确 Artist 匹配行为一致 |

## 10. 实施拆分

1. **阶段 1（可独立交付）**：模型 + `ListeningProfileService`（采集/统计/持久化/开关/重置）+ MainWindowViewModel 事件接线（含跳过检测）+ 设置页 UI + 单测。此时画像开始积累但尚不消费，风险最低。
2. **阶段 2**：推荐注入（prompt/兜底关键词/黑名单/探索方向）+ digest 后台生成 + 推荐侧单测。
3. **阶段 3**：文档同步（README「当前能力/已知技术债」、`ai-radio-plan.md` P3 勾销、AGENTS.md Recent Work）。

每阶段完成跑 `dotnet build` + 全量测试后再进入下一阶段。

## 11. 评审记录

**第 1 轮（对照代码核实事实性声明，修正 7 处）**：跳过判定增加「前曲播放态 + 新曲身份不同 + 位置样本绑定本曲」三重前置（`NotifyTrackChanged` 有 8 处触发点，载入/删除/恢复都会发）；CALM/FIRE 按钮的 mood 信号不再双记 MoodSet；明确 Calmer/Energetic/MoodSet 歌手分权重为 0；digest 水位从列表长度改为持久化单调计数 `TotalEventsIngested`（防 1000 条裁剪后错位）；未来版本画像本会话拒写（对齐 playlist 先例）；黑名单键保留 `IsSameMusicIdentity` 的空歌手通配语义；写明暂停恢复会重复触发 Playing 态。

**第 2 轮（设计层推演，修正 5 处）**：冷启动注入门槛（≥30 事件且 ≥3 歌手，否则不注入任何画像段）；语言切换触发的 digest 重生成每会话最多一次；Reset 与在途归纳的竞态用代次令牌隔离；启动推荐可能早于画像加载，明确为可接受降级；多歌手合作曲不拆分聚合记为已知限制。

**第 3 轮（实施后代码评审，修正 5 处）**：`ScheduleSave` 的 Timer CAS 竞争失败方会对已释放实例调 `Change` 抛 `ObjectDisposedException`（UI 线程崩溃风险），改为复用胜者 + 捕获释放竞态；`NotifyTrackSwitched` 原挂在 `ObserveOn(MainThread)` 订阅里，派发延迟期间新曲进度样本会覆盖旧样本导致漏记跳过，改为直连订阅同步评估（判定在服务内加锁，`IsCurrentFavorite` 仍走 UI 线程订阅）；`IsDisliked` 误用冷启动门槛快照与 `BuildExcludedTracks` 口径不一致，统一走 `GetBlacklistSnapshot`；移除只写不读的 `_playingStartedAt`（≥10s 判定以进度样本为准，暂停不计时更准确）；空歌手 Dislike 不入黑名单（`IsSameMusicIdentity` 空歌手通配会仅凭标题排除所有同名曲，"未知艺术家"是具体字面值不受影响）。

**第 4 轮（实施后代码评审，修正 2 处）**：清除画像的二次确认命令原用 `CreateFromTask` 且方法内 `await Task.Delay(5s)`——ReactiveCommand 执行中会忽略后续 Execute，5 秒确认窗口内的第二次点击被命令层吞掉、重置永远无法执行；改为同步命令体 + Timer 延时解除武装（计时用默认调度器，`RxApp.MainThreadScheduler` 在测试环境可能被替换为 Immediate 导致 dueTime 被忽略，仅回调切回 UI 线程），并新增两次点击流回归测试。`Reset()` 删文件与在途防抖写盘并发时，写盘方快照的是清除前状态、会在删除后把旧画像复活到磁盘；删文件前有界（2 秒）等在途写盘完成。

**第 5 轮（实施后终审，多轮编辑叠加后的整体一致性）**：完整通读服务/接线/设置三个文件的最终状态，确认锁序、门语义、事件顺序（自然结束先结算再续播、切歌判定与发射同步、Playing 后置）与设计一致；无功能缺陷，仅清理 3 处编辑残留（跳过判定字段缩进错位、方法间缺失空行、两处测试里无意义的 `"（Live）".Replace(...)` 内联表达式）。

**2026-09-18 持久化回归审查**：复现并修复写盘期间新增事件被清除 dirty 后漏存，以及 UI 退出同步等待捕获 UI 上下文的保存续体导致 2 秒超时。事件与摘要变更均递增保存版本；旧快照写完后，仅在版本一致时清除 dirty，否则继续补写新快照。保存门和文件 I/O 使用不捕获 UI 上下文的续体，退出仍维持 2 秒有界等待。

清空保护替代第 4 轮的有界等待：Reset 在状态锁内递增代次并删除已发布文件；保存方写完临时文件后，在同一把锁内核对代次再发布，旧代次直接丢弃。这样即使磁盘写入超过 2 秒，也不会复活旧画像，清空后新增的事件仍能保存。新增可控制写入时序的回归测试，覆盖并发采集、清空与新事件、UI 上下文退出；全部使用临时文件。
