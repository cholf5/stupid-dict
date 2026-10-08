# 设置窗口在副屏上按主屏缩放渲染（对话框 DPI）— 修复记录

## 背景 / 症状

Windows 双显示器（主屏 4K @150%，副屏 1080p @100%）：主窗口放在副屏时，打开设置窗口（`ShowDialog`）会按主屏的 150% 缩放渲染——控件全部过大、超出 1080p 高度看不全。主窗口自身拖到副屏则正常。上游 Avalonia issue #18921 与此症状一字不差（125%→100% 复现），#18903 同根因。

## 根因链（上游）

1. Win32 `WindowImpl.CreateWindow` 用 `CW_USEDEFAULT` 创建 HWND——对话框在 `new SettingsWindow()` 时就落在主屏，初始 `_dpi` = 主屏的 144（1.5x）。
2. `ShowDialog(owner)` 把窗口挪到 owner 中心（副屏）时，`WM_DPICHANGED` 在**窗口尚不可见**时到达；11.3.0 里老的「win32 dialog dpi hack」（上游 PR #16143）在这条路径上按错误口径保留/强设了缩放，1.5x 被留在了 1.0x 的屏上。主窗口不受影响：它的 DPI 变更发生在窗口可见后（用户拖动），走正常重缩放路径。
3. 上游修复：PR #18923「Remove win32 dialog dpi hack」（2025-05-26 合入 master，06-05 backport 进 11.3.x 支持分支）。移除 hack 之所以成立，是因为 PR #18315「Layout performance improvements」（2025-03-28 合入，早于 11.3.0 GA）已让 TopLevel 在任何 DPI 变化时自我失效重排——该机制 11.3.0 已自带，hack 反而成了干扰源。修复随 **11.3.1** 发布。

本仓库钉在 11.3.0，恰好卡在修复之前一个版本。

## 做了什么

- Avalonia `11.3.0 → 11.3.22`（11.3.x 线最新补丁，留在此补丁线内不跨大版本）：`StupidDict.App` 的 Avalonia / Avalonia.Desktop / Avalonia.Themes.Fluent，与 `StupidDict.App.Tests` 的 Avalonia.Headless.XUnit / Avalonia.Skia 锁同版本。
- 全量测试通过（Core 41 + App 81），所有钉住 11.3 行为的回归测试（设置下拉语言刷新、Enter 查原文、双击取词等）在 11.3.22 上照常通过。
- 构建期 `AVLN3001`（SettingsWindow 无公共无参构造）为基线既有警告，非本次引入。

## 评估过 / 不做

- **应用侧手工摆放**（`ShowDialog` 前显式设 `Position`、或改 `WindowStartupLocation=Manual` 绕开 CenterOwner）：治标，DPI 的最终裁决仍在框架层，叠加自绘定位反而更难排——不做。
- **升级 Avalonia 12.x**：跨大版本，本仓库多处依赖被回归测试钉住的 11.3 行为，风险与收益不成比例——留在 11.3.x 线，后续如需再评估。
- **本地验证的边界**：macOS 单显示器无法复现双 DPI 场景，headless 测试也无 DPI 概念；最终确认需要在 Windows 双缩放率机器上把主窗口放副屏开一次设置（预期：设置窗口按 100% 渲染、大小正常）。CI 同样覆盖不到，属已知盲区。
