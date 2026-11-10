# Windows 深色模式标题栏纯白：Avalonia 只在 Win11 代写 DWM 属性，且写完必须强制重组帧

- **状态**：已修复（`WindowsCaptionTheme`，两窗口 `Attach`），回归测试 `WindowsCaptionThemeTests`（属性值 / 19 回落 / 重组条件 / 零句柄跳过）
- **日期**：2026-11-10
- **背景**：Windows 用户夜间模式启动/重启后，主窗口与设置对话框的标题栏仍是系统白底（浅色主题下正常）。macOS 不受影响——原生深色材质自动跟随。

## 症状与根因

两层问题叠加，缺一不可：

1. **没人声明**。Win32 经典 caption 永远不跟随应用主题，必须由应用向 DWM 写 `DWMWA_USE_IMMERSIVE_DARK_MODE`。反编译 Avalonia 12.1.4 的 `Avalonia.Win32.WindowImpl.SetFrameThemeVariant`（确认于 `~/.nuget/packages/avalonia.win32/12.1.4`）：

   ```csharp
   if (Win32Platform.WindowsVersion.Build >= 22000)
   {
       int num = themeVariant == Dark ? 1 : 0;
       DwmSetWindowAttribute(_hwnd, 20, &num, 4);
       ...
   }
   ```

   即**只有 Windows 11（build ≥ 22000）才写属性 20**；Win10（19045 实测）上 Avalonia 一次都不写，标题栏自然是白的。

2. **属性写入 ≠ 视觉生效**。DWM 把 caption 的重组合成推迟到下一次 frame 事件，写完不强制重算，可能一直白到拖动窗口/最大化/重启才变。Avalonia 也没有补这一刀，所以即便在 Win11 上，应用层仍值得自己写一遍并 `SetWindowPos(SWP_FRAMECHANGED)`。

现场指纹：按着标题栏拖动时出现「白底 + 标题字在中间态」，随后最大化变黑——说明属性已经生效、只是 DWM 合成滞后，是时机问题而不是 API 用错；重启后又白，说明没有任何一次可靠的 frame 重组被触发。

## 修复

`src/StupidDict.App/WindowsCaptionTheme.cs`，`MainWindow` 与 `SettingsWindow` 构造时各 `Attach(this)`（每个窗口有自己的 HWND）：

- 三个时机：**构造期**（HWND 可能还没建，调用变 no-op）、**`Opened`**（真正的首帧前后补刀）、**`ActualThemeVariantChanged`**（运行期明暗切换）；
- 写属性：`DwmSetWindowAttribute(hwnd, 20, value)`，失败回落 `19`（Win10 1809–1903 的旧编号）；
- 写成功即 `SetWindowPos(hwnd, 0,0,0,0,0, SWP_NOSIZE|SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE|SWP_FRAMECHANGED)`——这一步才是「立刻看见」的原因；
- **Light 必须显式写 0**，不能「只在深色时写」：DWM 保留上一次的值，深→浅切换会粘住；
- 整条链 best-effort `try/catch`，标题栏颜色纯装饰，绝不能因 DWM 异常把窗口或进程带走。

句柄来源用 `window.TryGetPlatformHandle()?.Handle`，零句柄直接跳过：

- headless 平台的 `HeadlessWindowImpl.Handle` 是 `PlatformHandle(IntPtr.Zero, "STUB")`——Windows CI 跑 headless 测试时不会拿假句柄去调 DWM；
- 真实 HWND 在 `Show` 之前不存在（Avalonia 的 Win32 `WindowImpl` 在平台实现构造时建 HWND，而窗口的平台实现在 Show 时才建），所以构造期那一次一定是 no-op，`Opened` 才是首次有效写入。

测试缝：`internal Apply(IntPtr, ThemeVariant, ICaptionThemeBackend)`，生产后端 `DwmCaptionBackend`，测试用 `RecordingBackend` 断言「写哪个属性、写什么值、是否回落 19、是否触发重组」。

## 评估过但不做

- **自绘标题栏**（`ExtendClientAreaToDecorationsHint`）：要接管 min/max/close、拖动、Aero Snap 全套，代价与收益不成比例。
- **`DWMWA_CAPTION_COLOR` / `DWMWA_TEXT_COLOR`**：能精确配色，但压掉系统 active/inactive 状态与高对比度适配，收益只剩色值完全一致。
- **只在 Win10 上分支处理**：Win11 的 Avalonia 写入同样缺少强制重组，应用层统一写一遍 + 重组成本近乎为零，且不依赖 Avalonia 内部实现（反编译结论会随版本漂移）。

## 验收

- **真机目检是唯一有效验收**：headless 没有 HWND、DWM 不参与，CI 断言不了视觉。最小动作 = 夜间模式退出进程 → 重开，标题直接深色，无需任何交互；再在设置里切浅色，标题栏立刻变白（验证深→浅也生效）。
- **一次通过不算通过**：DWM 的重组时机随 Windows 更新漂移，同一台机器几周后可能回归；回归时优先怀疑时机（是不是少了 `SWP_FRAMECHANGED`），再怀疑属性编号。
- 单测覆盖的是协议本身（属性 20/19、值 1/0、失败不重组、零句柄不调用），不承诺视觉。

## 排查教训

1. **跨平台框架的「已经处理」要看平台门槛**。Avalonia 有 `SetFrameThemeVariant`，看起来万事大吉，实际上被 `Build >= 22000` 卡在 Win11；判断「框架管了没」必须反编译到具体分支，不能凭 API 名字。
2. **Win32 的「设置属性」和「看到效果」是两件事**。DWM 合成的非客户区尤其如此，写完必须显式触发 frame 重算；与 `WM_NCACTIVATE` 抖动这类兜底相比，`SWP_FRAMECHANGED` 无闪烁、可重复调用，优先它。
3. **深色主题的每一条渲染路径都要单独问一遍**。应用内的 XAML 与代码渲染都跟着 `ActualThemeVariant` 走了，但原生 chrome（标题栏、菜单栏、任务栏图标）各有各的协议，不属于 Avalonia 主题系统。
