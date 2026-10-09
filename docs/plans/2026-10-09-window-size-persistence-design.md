# 窗口尺寸记忆 — 设计记录（2026-10-09）

## 背景 / 目标

用户反馈：调完主窗口大小，下次打开就回到默认 780×560。目标是把主窗口的尺寸（和最大化状态）记到 `settings.json`，重启后原样恢复。全程**不新增设置界面**——README 的原则是「以后再出现必须配置的东西，优先自动决定，而不是增加设置项」，窗口尺寸正是「自动决定」的范畴。

## 做了什么

- `AppSettings` 新增三个字段：`WindowWidth` / `WindowHeight`（`double?`，DIP，null = 从未记录过）、`WindowMaximized`（bool）。随现有 `SettingsService` JSON 落盘，老 `settings.json` 缺这三个键时反序列化自然回退到 null/false，无需迁移。
- `MainWindow`：
  - 构造时按持久化值恢复 `Width`/`Height`（钳到 axaml 的 Min 尺寸），`WindowMaximized` 为真则把 `WindowState` 设为 Maximized——`WindowState` 是 styled property，Show 之前赋值经 `CreatePlatformImplBinding` 在属性赋值当下推给平台（PlatformImpl 在构造器就存在），实测 11.3 源码确认能生效。
  - `OnPropertyChanged` 监听 `Visual.BoundsProperty`，**仅在 `WindowState == Normal` 且尺寸 ≥ Min 时**记入 `_lastNormalWidth/_lastNormalHeight`。
  - `Closed` 时把 `_lastNormal*` 写回 `_settings.WindowWidth/Height`，`WindowMaximized` = 关闭时是否处于 Maximized。
  - `Opened` 时按所在屏幕工作区钳制 `Width`/`Height`（外接显示器拔掉后不再开出超出屏幕的窗口）。
- `WireSettings` 的 switch 加上三个 `Window*` 属性的 case：无需应用任何东西，落到既有的 `SettingsService.Save` 即可。MainWindow 只改共享实例，持久化仍归 wiring 管——测试里未接线的实例只动内存，永不写真实用户文件（沿用「测试永不触碰真实用户数据」铁律）。

## 关键依据：为什么必须自己记录 Normal 尺寸

查 11.3 的 `Window.HandleResized` 源码：平台每次 resize（**包括最大化**）都会无条件执行 `Width = clientSize.Width; Height = clientSize.Height;`，没有 `WindowState == Maximized` 的豁免；`Window` 类也不暴露 RestoreBounds。也就是说最大化期间 `Width/Height`/`Bounds` 都被污染成最大化尺寸，关闭时不能直接读 `Width`——必须靠 Normal 态的持续追踪兜住「用户最后一次亲手选的尺寸」。

## 评估过、不做的

- **窗口位置不记**：位置必须对着屏幕工作区校验（显示器拔掉后窗口会开在屏幕外，经典 bug），校验逻辑和跨平台差异（多屏、DPI、Linux WM 行为）换来的收益很小——用户抱怨的是尺寸，且每次查询都是全窗口操作，位置无关紧要。真要记时一并做「位置+尺寸」的完整 restore-bounds 机制。
- **不记每会话中间过程、只记关闭时刻**：进程被杀时丢最后一次调整，可接受；不做 resize 防抖落盘（写盘频繁、收益为零）。
- **FullScreen 不当 Maximized 存**：应用没有全屏入口，macOS 绿灯全屏属于边缘用法，按「非 Normal」处理——尺寸取 last-normal，状态不还原。
- **不支持改窗口尺寸的设置项**：与 README 原则一致，记忆本身就是全部功能。

## 测试

`WindowBoundsTests`：默认尺寸不覆盖持久值、恢复尺寸、恢复最大化状态、Normal 关闭存尺寸、**最大化关闭存 last-normal 尺寸而非最大化尺寸**（钉住 HandleResized 污染行为的对策）、经 `WireSettings(savePath)` 全链路落盘。`SettingsServiceTests` 补序列化往返与缺键回退。
