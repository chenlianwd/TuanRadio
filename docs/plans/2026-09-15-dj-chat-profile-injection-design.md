# DJ 聊天注入收听画像设计

日期：2026-09-15
状态：已实施（2026-09-15 按方案两阶段落地：DJService 快照注入 + DI 传参 + DJServiceTests 八个新用例；文档同步。实施后代码评审一轮（见 §9 第 3 轮），496/496 测试通过，与设计一致，无偏差）
结论来源：用户四项决策（范围=仅聊天对话、内容=摘要+歌手+氛围+避雷、表达=可自然主动提及、开关=跟随学习开关）；代码调研确认 `DJService.GenerateChatResponseAsync` 每次调用对 `_chatHistory` 做快照传 LLM，快照副本的 system 消息是干净注入点。

## 1. 目标与非目标

**目标**

- DJ 聊天对话获得长期画像上下文：LLM 口味摘要、常听/回避歌手、常见氛围。
- DJ 在聊天中的点歌/推荐建议避开 Dislike 黑名单曲目。
- DJ 可自然提及听众口味（有明确克制约束），体验为"懂你的老朋友"。
- 开关跟随现有「学习我的口味」：关闭即聊天回到无画像行为，无新设置 UI。

**非目标（本轮不做）**

- 串场词（`GenerateTrackIntroductionAsync`）注入：40 字硬约束下画像有挤占歌名风险（歌名截断刚修复，不做回归冒险）。
- SongStory / 单首推荐兜底注入：讲当前歌画像价值边际；推荐主路径（RecommendationService）已注入画像。
- 独立开关、DJ 播报画像统计（明确禁止罗列）。
- 聊天点歌指令对黑名单的硬拦截：DJ 避雷是提示词级约束；用户点名要听的歌仍照常执行（用户意图优先）。

## 2. 注入机制

`DJService` 构造函数追加可选依赖（保持现有单测构造兼容）：

```csharp
public DJService(ILLMService llm, ITtsService? tts = null, IMusicSearchService? musicSearch = null,
    IListeningProfileService? profile = null)
```

DI 注册处（`App.axaml.cs` 的 `IDJService` 工厂）传入 `IListeningProfileService` 单例。

`GenerateChatResponseAsync` 在取历史快照后、调 LLM 前，对**快照副本**的首条 system 消息拼接画像段：

```csharp
var snapshot = _chatHistory.ToList();
var profileBlock = BuildProfileContextBlock();   // null = 不注入
if (profileBlock != null && snapshot.Count > 0 && snapshot[0].Role == MessageRole.System)
    snapshot[0] = new ChatMessage { Role = MessageRole.System, Content = snapshot[0].Content + profileBlock };
```

选择该机制的依据（调研核实）：

- **每次调用取最新画像**：快照副本即时拼接，不依赖 `Initialize`（其会清空聊天历史），画像更新即刻生效；角色切换时 `Initialize` 重建全新 system，天然无画像残留。
- **不污染持久化历史**：`_chatHistory` 存原版 system；画像段不随历史落账、不参与裁剪计数。
- **裁剪安全**：`LLMService.BuildMessages` 超 20 条时 `history.Take(1).Concat(tail)` 恒保首条 system，画像段长对话不丢失。
- **system 数量不变**：`BuildMessages` 恒定前置内置"小音"system，生产请求本就是双 system（内置 + 人设），Anthropic 路径的 `systemParts` 合并是现状常态；注入拼在人设 system 内容尾部，**不新增第三条 system**，合并行为与现状一致。
- **落点顺序**：画像段追加在人设的 commandRules 与语言规则之后，作为"听众事实"补充段，与指令规则无顺序冲突。
- **空历史防御**：`Initialize` 未调用或历史为空（`snapshot.Count == 0`）或首条非 System 时不注入。
- **线程安全**：拼接发生在 `GenerateChatResponseAsync` 的 `_chatGate` 持门段内，无需额外锁。

## 3. 注入内容与提示词

画像段跟随 DJ `_profile.Language`（与 DJService 其余 prompt 一致），而非 AppLanguage。中文样例：

```
\n\n听众长期口味（内部参考，不要像档案一样罗列）：
{digest.Text（有则加）}
常听歌手：{TopArtists 前 5}；回避歌手：{AvoidArtists 前 3}；常见氛围：{MoodUsage 按计数降序前 2}
聊到音乐时可以自然地结合这些口味（比如偶尔提到对方常听的风格），但不要每句都提、不要播报清单。
不要主动推荐这些歌（听众明确不喜欢）：{DislikeBlacklist 最近 5 首 title - artist}。
```

英文段结构相同（`Listener long-term taste (internal reference, don't recite it like a dossier)` …），文案整体跟随 `_profile.Language`。**排序确定性**：MoodUsage 按计数降序取前 2（并列时任取，不影响行为）；黑名单"最近 5 首"= 快照 `DislikeBlacklist`（构建时已按 Dislike 时间降序）的 `Take(5)`。

- **冷启动门槛**：口味段（digest/歌手/氛围）沿用推荐链路门槛（≥30 事件且 ≥3 歌手），未达时不注入口味段。
- **黑名单避雷不走门槛**：与推荐链路口径一致——Dislike 是显式信号，画像冷启动期也要避雷；黑名单为空时整行省略。
- **digest 缺失降级**：未配置 LLM 摘要或尚未生成时，口味段退化为纯统计行（歌手+氛围），门槛达标即注入。
- **语言混用说明**：digest 按 AppLanguage 生成，可能与人设语言（`_profile.Language`）不一致；digest 是内部参考内容非输出语言指令，输出语言仍由人设规则控制，接受混用不另做翻译。

## 4. 开关语义

- `Enabled=false` → `GetSnapshot()` 返回空快照 → 门槛不达且黑名单为空 → 整段不注入，聊天行为与现状逐字一致（回归安全网）。
- 关闭只暂停采集与使用，数据保留、重开即恢复——与推荐侧语义完全一致，设置页无新增控件。

## 5. 令牌预算

画像段约 100-200 tokens（digest ≤400 字符 + 歌手/氛围行 + 黑名单 5 条）；聊天链路现为人设 system + 最多 20 条历史，增量可承受。黑名单样本固定取最近 5 条防膨胀。

## 6. 测试计划

**DJServiceTests（扩展）**

- 注入生效：门槛达标快照 → LLM 收到的 history 首条 system 含 digest 文本与常听歌手。
- **持久历史干净**：两次调用之间更换画像内容（fake 服务换快照），第二次调用捕获的 system 只含新画像段、不残留旧段——证明拼接只发生在快照副本上。
- **角色切换无残留**：`Initialize` 重建人设后，下一次调用的 system 为全新人设 + 当时画像段，无历史拼接残留。
- 回退路径：`profile == null` / `Enabled=false` / 门槛未达且黑名单空 → system 内容与现状逐字一致；`Initialize` 未调用（空历史）不注入且不抛。
- 避雷独立于门槛：门槛未达但黑名单非空 → 避雷行存在、口味段不存在。
- 语言跟随 `_profile.Language`（zh/en 两套段落文案）。
- 历史满 20 条裁剪后画像段仍在。

## 7. 风险与对策

| 风险 | 对策 |
| --- | --- |
| DJ 过度提及口味（每句都提） | 提示词明确克制约束：不每句提、不播报清单 |
| 提示词膨胀挤占对话质量 | 段落紧凑、黑名单限最近 5 条、无画像时整段省略 |
| 与人设 prompt 冲突 | 人设管角色气质与输出语言，画像管听众事实，正交；画像段标注"内部参考" |
| 画像未加载/冷启动噪声 | 门槛与推荐链路一致，未达时不注入 |
| 外部歌名/歌手文本进入 system 的提示词注入 | 与现状同信任级：推荐链路 prompt 已注入收藏/最近播放等外部标题，聊天用户消息本就是同等注入面；不做消毒（会破坏歌名匹配语义），记为已知接受风险 |

## 8. 实施拆分

1. **阶段 1（单轮可交付）**：`DJService` 注入 + DI 传参 + `DJServiceTests` 扩展 + build/test。
2. **阶段 2**：文档同步（README「当前能力」、`ai-radio-plan.md` 已完成清单、AGENTS.md Recent Work、本设计文档状态）。

## 9. 评审记录

**第 1 轮（对照代码核实，修正 4 处）**：原文"仍是单条 system、Anthropic 合并不触发"表述错误——`LLMService.BuildMessages` 恒定前置内置"小音"system，生产请求本就是双 system（内置 + 人设），`systemParts` 合并是现状常态；注入拼在人设 system 内容尾部、不新增第三条，合并行为与现状一致。补空历史/未 Initialize 防御说明（快照为空或首条非 System 不注入）。补注入落点：人设的 commandRules 与语言规则之后，无顺序冲突。测试计划补两条残留断言：画像更新后第二次调用不残留旧段（证明持久历史干净）、角色切换 Initialize 重建无残留。

**第 2 轮（设计推演，修正 3 处）**：外部歌名/歌手文本进入 system 的提示词注入面记为已知接受风险（推荐链路 prompt 已注入收藏/最近播放同信任级文本，消毒会破坏歌名匹配语义）；MoodUsage"前 2"与黑名单"最近 5 首"补确定性排序定义（计数降序 / 快照构建时已按时间降序的 Take(5)）；段落样例补英文对照说明，并明确快照拼接发生在 `_chatGate` 持门段内无需额外锁。

**第 3 轮（实施后代码评审，补 1 处测试缺口）**：代码核对全部通过——注入位于 `_chatGate` 持门段内 LLM 调用前、条件三重守卫（非 null/非空历史/首条 System）、禁用时 Empty 快照经 sections 空判定自然不注入、锁序为 `_chatGate`→画像服务内部 gate 且无反向调用（无死锁）、`_profile`（DJ 人设）与 `_listeningProfile`（画像服务）命名隔离、黑名单条目因画像侧"空歌手不入表"修复保证格式良好。发现设计 §6 承诺的「Initialize 未调用（空历史）不注入且不抛」用例漏写，已补（画像达标 + 未 Initialize → LLM 收到空历史、无画像段、无异常）。
