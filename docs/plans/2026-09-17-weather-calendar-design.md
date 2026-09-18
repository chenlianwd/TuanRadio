# 天气与日历设计（时钟舞台角落指示器）

日期：2026-09-17
状态：已实施（DurabilityTests 六用例 + 天气/日历全链路落地，541/541 测试通过。实施中发现并规避一个 Avalonia 11.3 编译器陷阱：`DataContext="{Binding WeatherVM}"` 重定向与编译绑定组合会静默破坏整个程序集的 XAML 预编译层（ChatAreaMicButtonTests 报"找不到预编译 XAML"）——指示器子树必须用 `{Binding WeatherVM.X}` 路径绑定，已在 ClockStage 注释与本节锚定）
结论来源：用户四项决策（Open-Meteo+IP 定位、时钟舞台角落图标、农历+节气+节日、悬停 Tooltip）+「简洁至上：默认只有图标，悬停才见详情」的产品要求。

## 1. 目标与非目标

**目标**

- ClockStage 时钟图层角落两个常驻小指示器：天气图标（阴晴雨雪）+ 日历徽标（公历日号，当天有节气/节日时高亮）。
- 悬停 Tooltip 显示详情：天气（描述+温度+定位城市）；日历（公历+农历+节气+节日+距周末天数）。
- 天气零配置：IP 粗定位 → Open-Meteo；失败或用户在设置页填了城市则城市优先（Open-Meteo geocoding 转坐标）。
- 天气是纯展示增强：任何失败只隐藏/灰化图标，绝不打扰播放主流程（与歌词同一降级哲学）。

**非目标**

- 点击展开面板、多日预报、天气背景动效。
- 天气预警推送、日历日程/订阅。
- 定位持久化为可选缓存（内存即可，重启重新定位成本可接受）。

## 2. 日历（纯本地）

`Services/ChineseCalendar.cs` 静态类：

- 农历：`System.Globalization.ChineseLunisolarCalendar`（BCL，1901-2100 范围）取农历月/日/闰月，格式化为「冬月十七」「腊月廿三」式文本。
- 节气：寿星近似公式（`day = floor(Y*0.2422+C) − L`，`Y=year%100`；一二月 `L=(Y-1)/4`，三月起 `L=Y/4`，均整除；例外见评审记录），日期有效性按年计算；测试锚定已知节气（如冬至 2024-12-21）+ 各节气日期落在公认区间。
- 节日：公历固定表（元旦/劳动节/国庆等）+ 农历节日（春节/元宵/端午/七夕/中秋/重阳，闰月不重复）。
- 输出 `CalendarDayInfo { LunarText, SolarTerm, Festival, DaysToWeekend }`；`Festival`/`SolarTerm` 非空时徽标高亮。

## 3. 天气

`Services/IWeatherService.cs` + `WeatherService`：

- **定位链**：设置页城市（非空时）→ Open-Meteo geocoding（`geocoding-api.open-meteo.com/v1/search`）取坐标；否则 ip-api.com（`ip-api.com/json?fields=status,lat,lon,city`）IP 粗定位。任一步失败 → null（图标隐藏）。
- **取数**：`api.open-meteo.com/v1/forecast?current=weather_code,temperature_2m&timezone=auto`；WMO weather_code 映射到 `WeatherKind`（Sunny/Cloudy/Overcast/Rain/Snow/Thunder/Fog）与本地化描述。
- **节流**：内存缓存 30 分钟（含定位结果）；整体 8 秒超时；全部失败静默（Log.Debug）。
- **隐私注**：IP 定位会把 IP 发给 ip-api.com（免费接口）；填城市后不再发 IP。

## 4. UI 与接线

- `ViewModels/WeatherViewModel`：持有天气状态与当日日历快照（日期变更时随 1s 时钟推进重算）；暴露 `IsWeatherVisible`、`WeatherTooltip`、`CalendarDayBadge`、`CalendarTooltip`、`IsCalendarHighlighted`、`WeatherIconKind`（供 ClockStage 选 Path）。
- ClockStage 时钟图层角落：日历徽标（日号 TextBlock，高亮走主题强调色）+ 天气 Path 图标；`ToolTip.Tip` 绑定文本；歌词模式随时钟图层整体隐藏（互斥已存在）。
- 设置页：`weather_city` 文本框（「城市名（如：上海），留空自动定位」），输入变化经 600ms 节流后重新取数，保存设置时持久化城市。
- DI：`IWeatherService` 单例；`WeatherViewModel` 由 MainWindowViewModel 组合并传入 ClockStage 绑定上下文（挂在 MainWindowViewModel，ClockStage 使用 `{Binding WeatherVM.X}` 路径绑定，避免重定向 DataContext 破坏 XAML 预编译）。

## 5. 测试计划

- `ChineseCalendarTests`：农历锚点（如 2026-02-17 为春节/正月初一——以 BCL 为源交叉验证格式化）、节气锚点与区间、公历/农历节日、距周末计算。
- `WeatherServiceTests`（DelegateHandler 伪造）：城市 geocoding→forecast 链、IP 定位链、WMO 码映射、失败返回 null 不抛。
- 耐久：天气 30 分钟节流由缓存测试覆盖（第二次调用不发请求）。

## 6. 实施拆分

1. ChineseCalendar + 测试；2. WeatherService + 测试；3. WeatherViewModel + ClockStage UI + 设置项 + DI；4. 文档同步与全量验证。

## 7. 评审记录

**第 1 轮（实施后代码评审，修正 4 处 + 补闰月锚定）**：设置页城市输入框逐键更新源会每个按键触发一次 geocoding 取数——城市订阅加 600ms 节流（Taskpool 节流 + 回 UI 线程）；WeatherService 失败日志从 Debug.WriteLine 统一为 Serilog Log.Debug；英文日历 Tooltip 缺日期（只有星期名），zh/en 均含日期；补 2025 闰六月锚定测试（初版测试日期选在正六月，代码行为正确、测试日期有误已修正——闰六月初一为 2025-07-25，锚定日改用 2025-08-01）。其余核对通过：寿星公式锚点（冬至/清明/立春/秋分）、缓存与失败静默链路、DI/释放顺序、语言重建回调、耐久测试断言确定性。

**实施期已抓（记录在案）**：Avalonia 编译器陷阱（DataContext 重定向 + 编译绑定 → 程序集 XAML 预编译层静默击穿，经还原对照与逐元素二分定位，指示器子树改路径绑定）；耐久测试误用 UI 亲和 API（生产链路经 Dispatcher 编组核实无误）；测试辅助 HttpClient 自伤释放；聊天历史首轮下界断言；定时器 lambda 变量遮蔽。

**2026-09-18 回归审查**：修复旧城市请求晚到覆盖新天气；请求代次与释放状态在 UI 调度器应用结果前再次核对，旧请求成功或失败均不得覆盖新城市。周期刷新也经 UI 调度器，关闭后的晚到结果丢弃。新增乱序成功/失败、忽略取消和 UI 派发回归测试。

日历修正：一二月仅扣除此前已发生的闰日（`(Y-1)/4`，`Y=year%100`），三月起仍按 `Y/4`；2026 年雨水校正为 2 月 18 日；闰月不重复农历节日，公历节日仍正常显示。寿星公式仍是近似算法，本次锚点验证不代表逐日验证 2001–2099 全部天文日期。参考香港天文台 [2024 年历](https://www.hko.gov.hk/tc/gts/time/calendar/pdf/files/2024.pdf)、[2026 年历](https://www.hko.gov.hk/tc/gts/time/calendar/pdf/files/2026.pdf)、[2028 年历](https://www.hko.gov.hk/tc/gts/time/calendar/pdf/files/2028.pdf)。
