# TuanRadio 在线音源播放可靠性专项改造方案

> 日期：2026-09-01
> 状态：已评审；Release A 已实施，后续阶段待办
> 优先级：P0
> 范围：酷狗登录与权益链路、跨源播放恢复、音源健康治理、连续跳歌保护
> 关联文档：[音源架构演进计划](./2026-08-25-music-source-architecture-evolution-plan.md)

---

## 1. 执行结论

当前五源聚合方案可以继续作为实验性在线音源层，但不应再被视为稳定播放能力本身。近期“大量歌曲无法播放并连续切换下一首”不是单一版权问题，而是以下问题叠加后的必然结果：

1. 当前 `/login/qr/check` 只返回基础 `token`、`userid`；桌面端没有继续调用 `/login/token` 刷新完整会话，因此从未取得 `vip_type`、`vip_token`、`t1` 等字段。
2. 酷狗代理的 GUID 默认随进程启动重新生成，MID 又由 GUID 派生，但桌面端跨启动复用旧 `dfid`，设备身份可能不一致。
3. 酷狗仍使用兼容保留的旧 `/song/url`，没有接入上游较新的 Auth 播放链路。
4. 设置页把“存在 Cookie”显示为“已登录”，没有验证账号、会员、设备和实际播放权限。
5. 酷我和咪咕依赖的非官方网页接口已出现稳定性失效；网易云大量候选只有试听流；YouTube 又经常被 8 秒整体预算提前截断。
6. 播放恢复失败后直接调用 `Next()`，缺少连续失败上限、队列预检和故障源熔断，最终形成肉眼可见的连续跳歌。

本专项改造采用以下决策：

- **先修酷狗完整身份链和实际权益检测，再调整跨源策略。**
- **酷狗优先接入 `/song/url/auth/merge`；不把返回加密音频的 `/song/url/new` 作为主播放接口。**
- **最多允许连续自动跳过 3 首；超过上限暂停并展示具体失败报告。**
- **酷我、咪咕在适配器恢复并通过 canary 前默认降级为关闭的实验源。**
- **本发布切片先把 YouTube 移出自动跨源恢复；显式播放仍给独立慢源预算。后台候选代理完成后再恢复自动兜底。**
- **本地曲库是稳定主源；第三方私有接口只提供尽力而为的在线补充。**

### 1.1 设计评审修正

实施前核对当前代码和上游 `bcf4b8af1c4c514b8c15fc1233b03d5b4377aab5` 后，已修正以下原方案假设：

- 二维码确认接口不直接返回完整 VIP 字段，必须追加 `/login/token` 刷新。
- MID 是 GUID 的派生值，不应再维护一份独立持久化状态。
- WEBGL 是十进制 uint64 字符串，不是任意 32 位十六进制值。
- 固定商业歌曲 canary 会随版权变化，不进入公共硬编码；播放诊断由用户选择曲目触发。
- 在没有请求版本隔离的后台候选代理前，YouTube 不能异步回写当前曲目，本轮先退出自动回退。

### 1.2 Release A 实施边界

本轮已经实施：完整酷狗会话刷新、稳定设备和 dfid 归属校验、本地代理设备摘要握手、Auth merge 主播放链、昵称/VIP 诊断请求、连续 3 首失败阻断、覆盖搜索与播放回退的传输故障熔断、酷我/咪咕默认关闭、显式/自动搜索意图区分、YouTube 退出自动回退及对应测试。

以下属于后续 Release B/C，本文保留其目标设计但不纳入本轮完成声明：全 Provider 的 `MediaResolutionResult` 接口迁移、后续 3 首队列预检与逐首状态、完整音源诊断页、网易云代理 vendor 升级、带请求版本隔离的 YouTube 后台候选代理。

## 2. 现场证据

### 2.1 最新运行样本

日志：`%APPDATA%\AIRadio\logs\airadio-20260901.log`

在 2026-09-01 08:53:30 至 08:57:08 的一次运行中，记录到以下事件：

| 事件 | 次数 | 典型表现 |
|---|---:|---|
| 酷狗没有可用播放数据 | 25 | HTTP 200，但 `data` 缺失，或 `status=2/errorCode=-1` |
| 网易云仅返回试听流 | 17 | `trial-only stream`，程序继续换源 |
| 酷我搜索业务失败 | 3 | 业务码 `-1` |
| 咪咕搜索业务失败 | 3 | 返回非 JSON 门户页 |
| YouTube 超时或预算耗尽 | 4 | 超时，或尚未执行就被整体 deadline 跳过 |
| 播放恢复失败后自动下一首 | 3 | `Advancing after playback recovery failed` |
| 记录到 `Playing` | 1 | 仅表示进入播放调用，不证明整首持续播放成功 |

以上为请求事件数，不等同于唯一歌曲数，但足以确认故障不是少量偶发版权限制。

### 2.2 实施前已确认事实与待验证推断

下表记录促成本方案的实施前基线，不代表 Release A 完成后的当前代码状态。

| 结论 | 类型 | 证据 |
|---|---|---|
| 酷狗扫码后没有刷新完整会话 | 已确认 | `/login/qr/check` 只返回 `token/userid`；`KugouAccountService.BuildCookieAsync` 未继续调用 `/login/token` |
| 酷狗昵称查询未携带登录头 | 已确认 | `GetNicknameAsync` 只把 `userid` 放进查询参数，运行日志中的 `/user/detail` 返回 20018 |
| UI 没有验证酷狗会员权益 | 已确认 | 设置页仅按 Cookie 和昵称显示“已登录”，未调用 `/login/token` 或 `/user/vip/detail` |
| 酷狗播放仍走旧 `/song/url` | 已确认 | `KugouMusicService.BuildPlayUrl` 固定构造旧接口 |
| 内置酷狗代理落后于上游 | 已确认 | 本地 `1.6.0`，上游当前 `1.6.2`，本地缺少 Auth URL 模块 |
| 酷狗设备身份可能跨启动漂移 | 高可信推断 | 代理启动时随机生成 GUID/MID，而桌面端只持久化 dfid |
| 看广告权益可以直接被第三方代理复用 | 未证实 | 官方描述为限时免费听任务；当前程序未读取或验证该活动会话 |
| 所有 `status=2` 都由同一个字段缺失导致 | 未证实 | 还可能包含版权、会员类型、设备、地区和上游风控差异 |

## 3. 目标与非目标

### 3.1 本专项目标

- 登录成功必须能区分“凭据存在”“账号有效”“会员有效”“设备有效”“探测歌曲可播”。
- 酷狗播放请求使用完整且跨启动一致的身份上下文。
- 导入歌单后，不再把“有搜索元数据”误认为“可完整播放”。
- 任一故障源不会让播放器无限连续跳歌。
- 用户能够看到具体失败类别，而不是统一显示“无法播放”。
- 在线凭据、临时 URL、签名和用户标识不进入普通日志或歌单文件。
- 各阶段保持现有 `AudioService`、LibVLC、AI DJ 和 TTS 状态机可用。

### 3.2 非目标

- 不绕过会员、数字专辑、地区或版权限制。
- 不解密 `/song/url/new` 返回的受保护音频。
- 不承诺通过第三方接口播放酷狗官方客户端中的全部歌曲。
- 不在本专项中完成整个 Provider 插件化、OpenSubsonic 或开放曲库实现。
- 不把音源重试和评分逻辑继续加入 `MainWindowViewModel`。

## 4. 目标播放路径

```text
本地曲目
  └─ 直接交给 AudioService

在线曲目
  ├─ 读取稳定 ProviderTrackRef
  ├─ 检查 Provider 登录状态、健康状态和熔断状态
  ├─ 首选源解析
  │    ├─ 成功：写入短期内存缓存并播放
  │    └─ 失败：返回结构化失败原因
  ├─ 快速替代源并行匹配
  ├─ 显式慢源解析（YouTube，可选；不参与本版自动恢复）
  └─ 全部失败
       ├─ 连续失败少于 3 首：尝试下一首
       └─ 连续失败达到 3 首：暂停并展示失败摘要
```

核心原则是把“搜索到歌曲”“账号已登录”“账号有权益”“当前候选可完整播放”拆成四个不同状态。

## 5. 阶段一：修复酷狗完整身份与权益链

> 目标：让扫码登录产生的身份、设备和会员信息真正进入播放请求，并能给出可验证的账号状态。

### 5.1 引入结构化登录模型

新增内部模型，建议命名为 `KugouCredentialContext`：

```csharp
internal sealed record KugouCredentialContext(
    string Token,
    string UserId,
    string? T1,
    string? VipToken,
    int? VipType,
    string? Dfid,
    string DeviceGuid,
    string? Auth,
    long Version);
```

要求：

- 扫码确认先取得 `token/userid`，再调用 `/login/token` 获取并保留完整必要字段；不能假设二维码接口本身返回 VIP 字段。
- 凭据整体写入现有 Windows 安全存储；日志只记录非敏感状态和递增的 `Version`。
- `DeviceGuid` 首次创建后持久化；MID 由代理按固定 GUID 派生，不重复持久化两个事实来源。
- `dfid` 必须和当前 DeviceGuid/MID 一起注册、保存和更新。
- 单独保存 dfid 所属的 DeviceGuid；启动时归属缺失或不匹配就移除旧 dfid 并重新注册，覆盖安全存储部分写入场景。
- 现有 `token/userid/dfid` 旧数据兼容读取；优先用 `/login/token` 原地刷新，只有刷新明确返回登录失效时才提示重新扫码。
- 账号切换或重新登录时递增 `Version`，使旧播放 URL 和验证缓存立即失效。

第一版实施保持 `MusicAccountStore.KugouCookie` 的兼容外观，使用确定性 Cookie 合并器保存新增字段；稳定设备身份单独以版本化 JSON 写入安全存储。后续如需公开结构化模型，再在不破坏现有歌单/验证服务的前提下迁移。

### 5.2 统一代理设备环境

修改 `MusicApiServer` 启动酷狗代理时的环境变量：

- 显式传入持久化的 `KUGOU_API_GUID`。
- 由代理统一派生 `KUGOU_API_MID`，桌面端不传入另一份可能漂移的 MID。
- 持久化并显式传入 `KUGOU_API_WEBGL`，避免它只在单次 Node 进程内稳定。
- 禁止在无持久化设备上下文时复用历史 dfid。

### 5.3 登录后执行四级诊断

登录成功和应用启动时依次执行：

1. `/login/token`：刷新完整会话，并确认基础 token 仍有效。
2. `/user/detail`：携带 `Authorization` 头，确认账号和昵称可读取。
3. `/user/verify`：刷新当前 auth 和风控状态。
4. `/user/vip/detail`：在接口形状经过样本验证后展示会员类型和有效期；此前只展示 `/login/token` 返回的会员类型编号，不能臆造有效期。
5. 用户主动选择的诊断曲目：验证当前账号是否能取得完整 128K URL。公共版本不硬编码商业歌曲 canary。

建议的 UI 状态：

| 状态 | UI 文案示例 | 是否允许自动节目单使用酷狗 |
|---|---|---:|
| `CredentialMissing` | 未登录 | 否 |
| `CredentialExpired` | 登录已失效，请重新扫码 | 否 |
| `RiskVerificationRequired` | 需要完成滑块验证 | 否 |
| `AuthenticatedNoVip` | 已登录，未识别到音乐会员 | 只允许已确认免费曲目 |
| `AuthenticatedVip` | 已登录，检测到音乐会员类型 N | 是 |
| `CanaryUnavailable` | 已登录，但播放验证失败 | 否，等待用户诊断 |
| `Ready` | 酷狗音源可用 | 是 |

删除或改写当前“看广告领取的会员在这里同样有效”的确定性承诺。建议改为：

> 酷狗的免费听、会员和数字专辑权益由官方账号、设备和活动规则决定；本应用会显示实际检测结果，但不能保证活动权益可由第三方接口复用。

### 5.4 接入 Auth 播放接口

对内置 KuGouMusicApi 做**选择性 vendor 升级**，不能直接覆盖本地已有的滑块会话桥和日志脱敏改动。

需要引入或对齐：

- `/user/verify`
- `/song/auth`
- `/song/url/auth`
- `/song/url/auth/merge`
- 上游与 Auth 链路相关的签名、设备和 Cookie 处理

播放优先级：

1. 对主 hash、`hash_std`、`hash_128` 依次尝试 `/song/url/auth/merge?quality=128`
2. 全部 Auth 候选失败时，仅对主 hash 尝试一次旧 `/song/url?quality=128`
3. 两者都失败时返回结构化原因，不调用 `/song/url/new` 播放加密音频

每次请求必须带：

- `token`
- `userid`
- `t1`（上游需要时）
- `vip_type`
- `vip_token`
- `dfid`
- 稳定 GUID（MID 由代理派生）
- `album_id`、`album_audio_id` 等曲目身份元数据

### 5.5 阶段一验收

- 新扫码结果持久化后，重新启动应用仍使用相同 GUID、派生 MID 和 dfid 组合；崩溃遗留或外部代理的设备摘要不匹配时禁止复用。
- 设置页不再仅凭 Cookie 显示“已登录且可播放”。
- 免费曲目、普通会员曲目、数字专辑/额外付费曲目能返回不同结果类别。
- Auth 接口成功时能完整播放测试歌曲；失败时旧接口回退只执行一次。
- Cookie、token、vip token、auth、dfid、MID 不出现在 URL、普通日志和异常文本中。
- 旧凭据迁移不会伪造 VIP 状态；缺失关键字段时明确要求重新扫码。

## 6. 阶段二：可播预检、熔断与连续跳歌保护

> 目标：即使部分音源继续不稳定，也不能演变为无限连续切歌。

### 6.1 引入结构化解析结果

替换 `string?` 所表达的所有失败状态：

```csharp
public enum PlaybackFailureKind
{
    None,
    NotFound,
    PreviewOnly,
    AuthRequired,
    SubscriptionRequired,
    PurchaseRequired,
    RegionRestricted,
    RiskVerificationRequired,
    SourceUnavailable,
    Timeout,
    TransportRejected
}

public sealed record MediaResolutionResult(
    ResolvedMedia? Media,
    PlaybackFailureKind Failure,
    string ProviderId,
    string? DiagnosticCode,
    bool IsRetryable);
```

诊断码进入逐源报告；敏感上游响应只能保留脱敏摘要。

### 6.2 队列预检

- 当前曲目播放后，在后台预检后续最多 3 首。
- 只解析稳定身份和短期 URL，不提前启动媒体播放。
- 预检受生命周期取消控制；用户换歌单、退出或重新登录时全部取消。
- 预检结果按 `ProviderId + TrackId + AuthContextVersion` 缓存在内存。
- 试听流不能被标记为“完整可播”。
- 导入酷狗歌单时允许先展示全部元数据，但 UI 必须区分“未检查、可播、受限、失败”。

### 6.3 连续失败上限

修改 `AudioService.ScheduleNextTrack` 的策略：

- 单首解析失败仍可进入下一首。
- 记录连续自动跳过计数；用户主动播放成功或曲目稳定播放达到阈值后清零。
- 连续失败达到 3 首时停止自动切换；第一版通过独立的恢复阻断事件通知 UI，避免贸然扩展所有 `PlaybackState` 消费者。
- UI 显示最近三个曲目的失败摘要，并提供：
  - 重试当前歌曲
  - 跳过受限歌曲
  - 打开音源诊断
  - 暂时禁用故障源

### 6.4 音源熔断

在 `SourceHealthRegistry` 中分别记录：

- 网络超时
- 上游 5xx
- 畸形/非 JSON 响应
- 认证失败
- 权益限制
- 最近成功时间和滚动成功率

第一版规则：

- 网络/接口类连续 3 次失败：熔断 60 秒；普通搜索、播放 URL 和播放时跨源搜索使用同一健康状态。
- 认证失败：保持 `AuthRequired`，直到账号版本变化或用户主动重试。
- 权益限制：只标记当前曲目，不惩罚整个音源。
- 系统性协议失败，例如酷我持续业务码 `-1`、咪咕持续返回门户页：本次会话禁用并显示原因。

### 6.5 阶段二验收

- 连续 20 首不可播候选不会产生无限切歌；第 3 首失败后暂停。
- 已熔断音源不会在每一首歌曲上重复消耗超时预算。
- 导入歌单能显示可播状态，不把搜索成功等同于播放成功。
- 账号重新登录后，认证熔断和旧解析缓存立即清除。
- 预检任务不会阻塞 UI，也不会在关闭应用后继续运行。

## 7. 阶段三：重新划分音源优先级

> 目标：减少“名义上五个源、实际上多数不可用”带来的虚假覆盖率。

### 7.1 默认策略

| 音源 | 新定位 | 默认状态 | 处理 |
|---|---|---:|---|
| 本地曲库 | 稳定主源 | 启用 | 始终优先 |
| 酷狗 | 已登录实验主源 | 用户登录后启用 | 完整身份、权益检测、Auth URL |
| 网易云 | 已登录实验主源 | 用户登录后启用 | 保留试听识别，升级并固定代理版本 |
| YouTube | 显式慢源 | 可选 | 独立预算；本版不参与自动跨源恢复 |
| 酷我 | 故障实验源 | 默认关闭 | 适配器恢复并通过 canary 后再开放 |
| 咪咕 | 故障实验源 | 默认关闭 | 适配器恢复并通过 canary 后再开放 |

### 7.2 YouTube 调整

- 当前固定 yt-dlp 版本与哈希机制继续保留。
- 显式搜索和 URL 解析使用独立的 20–30 秒预算。
- 本发布切片不让 YouTube 参与自动跨源恢复，避免 8 秒前台 deadline 与 30 秒子进程预算互相抵消。
- 后续增加带请求版本校验的后台候选代理后，快速源失败才可异步提交慢源候选；旧请求结果不得回写新曲目。
- 浏览器 Cookie 仍由用户显式启用。
- 是否随应用集成 PO Token Provider 必须先完成许可证、供应链、进程和隐私评估；不能静默下载并运行未经固定和校验的插件。
- 如果没有满足当前 YouTube 风控要求，状态必须显示“需要额外验证配置”，不能伪装成普通超时。

### 7.3 网易云调整

- 将 `NeteaseCloudMusicApi` 从锁定的 4.31.0 升级到经过回归验证的当前固定版本。
- 继续拒绝把试听流冒充完整歌曲。
- 账号状态必须区分免费账号、会员账号、Cookie 失效和接口不可用。
- 代理升级失败时保持可回滚的旧版本包，不在普通构建中自动追随 latest。

### 7.4 阶段三验收

- 默认配置不再请求已知持续故障的酷我、咪咕接口。
- 快速播放恢复不被 YouTube 子进程拖住。
- 显式选择 YouTube 曲目时能够使用独立预算；自动恢复不会启动 yt-dlp 子进程。
- 禁用全部实验性在线源后，本地播放、AI DJ、TTS 和频谱仍正常。

## 8. 阶段四：诊断与用户体验

### 8.1 音源诊断页

每个 Provider 显示：

- 是否启用
- 账号状态
- 权益状态
- 设备/风控状态
- 最近一次搜索成功时间
- 最近一次播放解析成功时间
- 最近 20 次请求成功率
- 熔断状态和剩余冷却时间
- 当前版本和可用更新

提供一键“运行音源诊断”，但不得在诊断日志中输出凭据或完整签名 URL。

### 8.2 播放失败文案

| 失败类型 | 用户文案 |
|---|---|
| `PreviewOnly` | 当前账号只获得了试听片段 |
| `SubscriptionRequired` | 该歌曲需要对应音乐会员 |
| `PurchaseRequired` | 该歌曲或数字专辑需要单独购买 |
| `AuthRequired` | 登录已失效，请重新登录 |
| `RiskVerificationRequired` | 音源要求完成安全验证 |
| `RegionRestricted` | 当前地区不可播放 |
| `SourceUnavailable` | 音源接口当前不可用，已暂时停用 |
| `Timeout` | 音源响应超时 |

UI 不展示上游 token、URL、原始 JSON 或堆栈。

## 9. 测试方案

### 9.1 单元和 fixture 测试

- 扫码确认后的 `/login/token` 刷新结果中，`vip_type`、`vip_token`、`t1` 不丢失。
- 旧三字段 Cookie 迁移后不会误判为会员可用。
- GUID、派生 MID、WEBGL、dfid 在重启模拟中保持一致。
- `/user/detail`、`/user/verify`、`/user/vip/detail` 各种成功和失败形状。
- Auth merge 成功、旧 URL 回退、会员限制、数字专辑限制、地区限制。
- 连续失败计数、播放成功清零、手动切歌与自动切歌的区分。
- 网络失败触发熔断，权益限制不触发整源熔断；播放回退搜索同样遵守熔断。
- 预检任务的取消、缓存隔离和账号版本失效。
- YouTube 不参与自动推荐或自动回退；显式搜索/播放使用独立且有界的慢源预算。
- 日志、歌单、设置文件中敏感字段命中数为 0。

### 9.2 联网 canary

联网测试只在 `AIRADIO_INTEGRATION_TESTS=1` 时运行，并使用用户本机登录态：

- 酷狗免费曲目一首。
- 当前账号在官方客户端确认可播放的会员曲目一首。
- 已知需要额外购买的曲目一首。
- 网易云完整可播与试听曲目各一首。
- YouTube 匿名、浏览器 Cookie 和可选验证配置分别测试。

canary 只记录分类和耗时，不记录曲目 URL 或账号信息。商业歌曲是否始终存在不能作为公共 CI 的固定断言。

### 9.3 每阶段验证命令

```powershell
dotnet build AIRadio.Desktop\AIRadio.Desktop.csproj -v:minimal
dotnet test AIRadio.Desktop.Tests\AIRadio.Desktop.Tests\AIRadio.Desktop.Tests.csproj -v:minimal "/p:UseSharedCompilation=false"
git diff --check
```

## 10. 全专项完成标准

本专项只有同时满足以下条件才算完成：

- 酷狗扫码后能显示账号、会话会员类型和设备状态；播放诊断由用户选择曲目触发。
- 重启应用不会改变酷狗稳定设备身份，也不会复用不匹配的 dfid。
- 酷狗主路径使用 Auth merge，旧接口只作为有界回退。
- 导入的酷狗歌单能够逐首显示可播状态。
- 连续不可播不会无限跳歌，最多自动跳过 3 首。
- 酷我和咪咕系统性故障不会重复拖慢每次播放。
- 自动恢复不再启动注定会被 8 秒 deadline 截断的 YouTube 子进程；显式播放使用独立预算。
- 用户能看到真实失败类别，不再把所有问题归结为“未登录”或“没会员”。
- build、全量测试、`git diff --check` 全部通过。
- 账号凭据和临时播放地址不落日志、不落普通配置、不落歌单。

## 11. 发布与回滚

- 稳定设备身份使用版本化安全存储；现有 Cookie 格式保持读写兼容并允许原地补齐字段。
- Auth 播放链使用功能开关；出现上游回归时可回到旧 `/song/url`，但不能回退为丢弃完整凭据。
- Provider 默认启用状态随应用版本迁移一次，不在每次启动覆盖用户选择。
- 新连续失败保护出现误判时，可调整阈值，但不能恢复无限自动下一首。
- 代理 vendor 升级单独提交，保留来源 commit、上游版本和本地补丁清单。

## 12. 推荐实施与提交顺序

1. `test:补齐酷狗登录权益与播放失败基线`
2. `fix:持久化完整酷狗凭据与稳定设备身份`
3. `feat:增加酷狗账号权益与播放可用性诊断`
4. `refactor:升级酷狗代理并接入Auth播放链`
5. `feat:增加曲目可播预检与结构化失败原因`
6. `fix:限制连续不可播歌曲的自动切换`
7. `feat:增加音源健康熔断与会话停用`
8. `refactor:调整在线音源默认优先级与YouTube后台预算`
9. `docs:更新音源账号提示与发布边界`

每个提交必须独立通过 build/test；酷狗代理升级、凭据迁移和播放恢复状态机不得合并成一个不可回滚的大提交。

## 13. 主要涉及文件

- `AIRadio.Desktop/Services/KugouAccountService.cs`
- `AIRadio.Desktop/Services/KugouMusicService.cs`
- `AIRadio.Desktop/Services/KugouVerificationService.cs`
- `AIRadio.Desktop/Services/MusicAccountStore.cs`
- `AIRadio.Desktop/Services/MusicApiServer.cs`
- `AIRadio.Desktop/Services/MultiSourceMusicService.cs`
- `AIRadio.Desktop/Services/AudioService.cs`
- `AIRadio.Desktop/Services/NeteaseMusicService.cs`
- `AIRadio.Desktop/Services/YouTubeMusicService.cs`
- `AIRadio.Desktop/ViewModels/SettingsViewModel.cs`
- `AIRadio.Desktop/ViewModels/PlaylistViewModel.cs`
- `AIRadio.Desktop/server-kugou/`
- `AIRadio.Desktop.Tests/`

## 14. 参考资料

- [KuGouMusicApi 上游仓库](https://github.com/MakcRe/KuGouMusicApi)
- [KuGouMusicApi 播放与 Auth 接口文档](https://github.com/MakcRe/KuGouMusicApi/blob/main/docs/README.md)
- [酷狗免费听活动页面](https://h5.kugou.com/vipfreemode/v-35ffb015/index.html?should_append_gdt_ua=1)
- [酷狗音乐会员服务协议](https://vip.kugou.com/recharge/agreement?type=1&ver=8391)
- [NeteaseCloudMusicApi 仓库](https://github.com/Binaryify/NeteaseCloudMusicApi)
- [yt-dlp PO Token 指南](https://github.com/yt-dlp/yt-dlp/wiki/PO-Token-Guide)
- [yt-dlp FAQ](https://github.com/yt-dlp/yt-dlp/wiki/FAQ)
