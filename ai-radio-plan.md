# AIRadio 实施计划

## 产品定位

AIRadio 的方向已经收敛为“复古 AI 电台”：

- AI DJ 角色：名称、声音、人设提示、轻量头像动画。
- 音乐能力：本地播放、多平台搜索、收藏、在线播放 URL 刷新。
- 电台能力：根据用户意图、当前歌曲、收藏和排除列表生成 3-5 首节目单。
- 视觉反馈：星空、频谱、头像状态动效、统一状态文本。

## 已完成

- Avalonia + ReactiveUI 桌面框架。
- LibVLCSharp 播放核心和 NAudio TTS 播放。
- MiniMax 对话、串场和 TTS。
- Whisper 本地语音识别入口。
- 多音源搜索：网易云、酷我、酷狗、咪咕。
- Radio Mode 自动续播和播放列表同步。
- 收藏持久化与旧数据迁移。
- DJ 角色配置和设置覆盖。
- 推荐服务 v1 模型与服务骨架。
- JSON DJ 控制块解析，兼容旧文本尾标。

## 当前开发阶段

### P0 清理

- 删除旧静态资源、静态服务和 Web 依赖。
- 修复乱码文案、角色提示、默认 fallback 文本。
- 统一视觉反馈命名为 DJ visual cue。
- 移除业务代码里的未完成异常。
- 更新文档到新产品定位。

### P1 推荐闭环

- `RecommendationService` 根据用户输入、当前歌曲、收藏和排除列表生成节目单。
- 搜索结果去重、获取播放 URL、标记可播放状态。
- 用户反馈动作影响当前会话推荐。
- Radio Mode 优先消耗当前节目单，节目单耗尽后再生成新节目单。

### P2 产品化

- 节目单 UI 区分当前节目单、收藏、搜索。
- 状态机覆盖 `Idle`、`Curating`、`Searching`、`Speaking`、`Playing`、`Error`。
- 推荐理由放在 DJ 气泡，卡片只保留短标签。
- UI 绑定和状态更新补充测试。

### P3 增强

- Song Story v1：单曲 3-5 句 DJ 讲述脚本。
- 真实 FFT 可行性评估。
- 外部音源失败原因、超时和格式变化测试。
- 文档同步已完成/未完成能力。

## 构建与测试

```bash
dotnet build AIRadio.Desktop\AIRadio.Desktop.csproj -v:minimal --no-restore
dotnet test AIRadio.Desktop.Tests\AIRadio.Desktop.Tests\AIRadio.Desktop.Tests.csproj -v:minimal --no-restore /p:UseSharedCompilation=false
```
