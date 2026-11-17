# 局域网 Web 遥控器（Web Remote）设计方案

**日期**：2026-11-17  
**状态**：已实现并通过全量回归（专项 23/23 绿，全量 1374 通过 / 3 跳过 / 0 失败；Release 标准与精简构建 0 警 0 错）

---

## 1. 目标与选型确认

在保留 Windows 桌面端 WASAPI 真实频谱、SMTC 媒体集成、Edge TTS 与 FM 调频音效等核心体验的前提下，提供零安装、跨设备（手机/平板浏览器）的局域网无线遥控能力：

1. **启停与安全策略**：默认关闭，按需开启并支持配置记忆；启动后生成随机访问令牌，扫码链接直接携带令牌（`?token=...`）实现免手输直连，并支持随时一键刷新令牌即时作废旧连接。
2. **手机端能力（v1 完整遥控台）**：
   - 实时曲目信息、音源与音质标签、专辑/电台/播客状态识别
   - 播放/暂停、上一首、下一首、红心收藏、随机/循环模式切换、音量调节、可拖拽进度条（直播流禁用）
   - 场景模式快捷切换（专注伴学 / 睡眠定时预设）与 DJ 反馈（喜爱/跳过/相似/舒缓/欢快/故事）
   - 实时多行滚动歌词展示（支持点击歌词跳转进度）
   - 实时 AI DJ 广播/聊天记录查看与手机端文字点歌/对话输入
3. **桌面端入口**：主界面品牌头新增遥控器快捷图标（运行中高亮并展示连接数），点击弹出带高清二维码与快捷操作（复制链接、刷新令牌、启停服务、跳至设置）的浮层；设置页同步提供开关、端口设置、令牌管理与二维码预览。

---

## 2. 技术选型（零新增第三方依赖）

- **嵌入式 HTTP/1.1 + WebSocket 服务**：直接基于 .NET 10 BCL `TcpListener`（绑定 `IPAddress.Any`）+ `WebSocket.CreateFromStream` 实现，而非 `HttpListener`（非管理员环境下绑定局域网端口会触发 `Access Denied` / URLACL 限制）或 Kestrel（会显著膨胀独立发布包体积），同时保持 `AIRadio.Desktop.csproj` NuGet 锁文件与第三方声明零变更。
- **内置标准 QR 码生成器**：纯 C# 实现 ISO/IEC 18004 Byte 模式（ECC Level M，Version 1~10）矩阵生成，直接渲染 4 倍静默白边的高对比度 `WriteableBitmap`（`BitmapInterpolationMode.None`），兼容暗色界面并可被微信、相机扫码稳定识别。
- **单文件离线移动 SPA**：内嵌全量响应式 HTML/CSS/Vanilla JS，无任何外链字体或 CDN 脚本，完全断外网的纯局域网环境下亦可打开使用，界面文本随桌面端当前语言（中/英）自动同步。

---

## 3. 安全与连接治理

- **认证机制**：
  - `GET /` 静态壳页面不携带任何用户歌单、播放状态、聊天记录或令牌敏感数据。
  - 所有数据与控制端点（`GET /api/state`、`POST /api/command`、`GET /ws`）均校验随机 128-bit URL 安全令牌（支持 `Authorization: Bearer`、`X-Remote-Token` 或 WebSocket 握手 `?token=`），使用常量时间比对 `CryptographicOperations.FixedTimeEquals`。
  - 未授权请求返回 `401 Unauthorized` / 关闭 WebSocket（自定义关闭码 `4001`），手机界面随即展示手动配对输入页。
- **防攻击与资源上限**：
  - HTTP 请求头上限 8KB、请求体上限 32KB、读超时 5s；WebSocket 并发客户端上限 8、单帧上限 16KB。
  - 禁用 `Access-Control-Allow-Origin: *` 并响应 `Cache-Control: no-store` 与 `X-Frame-Options: DENY`，阻止网页跨站/DNS 重绑定偷控。
  - 端口被占用时在 `preferredPort .. preferredPort + 5` 区间自动顺延或回传友好错误，不中断主程序启动。
  - 局域网地址发现（`LanAddressResolver`）优先选取活跃物理网卡（Wi-Fi / Ethernet）RFC 1918 私网 IPv4，剔除 `169.254.*` 链路本地与 WSL/Hyper-V/Docker 等虚拟网卡；无局域网时回退 `127.0.0.1` 便于本机测试。

---

## 4. 宿主集成与线程模型

- 所有网络收发与 WebSocket 广播运行在后台线程；面向 ViewModel/AudioService 的指令执行与快照采样统一调度回主 UI 线程（`RxApp.MainThreadScheduler` / `Dispatcher.UIThread`），通过节流与事件驱动推送快照（位置每 ~250ms 由移动端平滑插值、切曲/状态/聊天/歌词切行即时推播）。
- `MainWindowViewModel` 构造可选注入 `IWebRemoteService?`，默认关闭（`web_remote_enabled = false`），确保未启用与既有单元测试不监听任何真实端口。
- `SettingsView.axaml` 的外观页签遵循既有 6 行对齐测试约束（使用 `DockPanel` 组织 Web Remote 配置区块，不引入干扰 `Grid`）。
- 退出链路在 `App.OnShutdownRequested` 与 `MainWindowViewModel.Dispose()` 中及时断开所有在线遥控会话并释放监听端口。
