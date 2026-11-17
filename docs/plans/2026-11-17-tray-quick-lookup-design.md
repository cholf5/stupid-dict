# 托盘常驻 + 左键快速查词（Tray Quick Lookup）— 设计记录（2026-11-17）

## 背景 / 目标

用户要求「参考 tokenmetry 添加托盘菜单，增加左键快速查词功能，保持右键菜单」。当前仓库只有
`2026-10-25-close-to-tray` 这一版托盘：**图标只在窗口收进托盘期间存在**，菜单两项「显示主窗口 /
退出」，左键（Win32 才有 `Clicked`）恢复窗口——设计文档当时把「常驻托盘」「托盘菜单放设置」列为
YAGNI。本次由用户点名推翻前两条，按 README「用户点名的例外」处理。

目标：

- **常驻托盘**：应用启动就有图标，窗口开着也在（否则「快速查词」只在收进托盘后才可达）。
- **左键 = 迷你查词窗**：托盘图标旁弹出无边框小窗，输入即出结果，Esc 关掉；回车把词丢给主窗口
  看完整词条。右键 = 原生菜单。
- **右键菜单四项**：显示主窗口 / 设置 / 检查更新 / 退出（用户点定的清单）。
- **关窗进托盘流程不变**：首次关闭仍问一次，答案记住；只是图标不再随隐藏/恢复出现消失。

## 做了什么

- `Tray/TrayIconHost.cs` 重写为**常驻**：`ITrayHost.Show(TrayActions)` 创建一次（幂等），
  `Dispose` 摘除；`TrayActions` 把五个动作（快速查词 / 显示主窗口 / 设置 / 检查更新 / 退出）
  作为委托交给宿主，宿主不含任何 UI 逻辑。
- **macOS 自管 NSStatusItem**（`Tray/MacStatusItem.cs`，移植 tokenmetry 的 MacTrayInterop 精简版）：
  Avalonia 的 `TrayIcon.Clicked` 在 macOS 从不 raise（`trayicon.mm` 无点击回调），左键弹层必须绕开
  Avalonia 托盘——自建 `NSStatusItem`，`objc_allocateClassPair` + `object_setClass` 派生按钮类重写
  `rightMouseDown:` 弹自有 `NSMenu`，左键走 target/action。安装失败（非 arm64 ABI、AppKit 变动）
  回落 Avalonia `TrayIcon`：图标与右键菜单照常，只是 macOS 上左键只会弹菜单。
- `Tray/PopoverPositioner.cs`：锚定纯函数（`AboveCursor` / `BelowAnchor` / `InCorner`，都带工作区
  钳制），headless 直测。平台锚点来自 `Tray/TrayAnchor.cs`：Windows 用 `GetCursorPos`（点击瞬间
  光标就在托盘图标上），macOS 用状态项按钮 frame（Cocoa→Avalonia 纵轴换算同 Avalonia.Native），
  Linux/探测失败用「工作区贴栏那个角」——比较 `Screen.Bounds` 与 `WorkingArea` 判面板在哪条边。
- `Tray/QuickLookupWindow.axaml(.cs)`：440 宽、无系统边框、不进任务栏、置顶；输入 150ms 防抖
  （代数丢弃过期结果，同主窗口纪律），渲染词头 + 音标 + 英/美发音按钮 + 前 6 条中文释义（英文
  释义兜底），未命中给「你是不是要找」词条按钮（点回填输入框）；Esc / 失焦关闭；回车回调主窗口。
  只读 `DictionaryService`，复用主窗口那一个 `ISpeechPlayer`（MCI 单 alias、单进程语义不能破）。
- 失焦守卫：Windows 上点托盘图标会**先**把焦点交给任务栏（弹层 `Deactivated` 收起），`Clicked`
  要等 mouse-up 才到——不设守卫则「开着点一下」表现为收起后立刻再弹。记录「自己因失焦收起」的
  时刻，左键入口在 400ms 内看到它就直接不再弹（消费制，等一下再点照常开）。与 tokenmetry B-017
  同一机理，同一取值。
- 菜单动作：设置 = 先把窗口拉回来再开模态对话框（`SettingsWindow` 的 owner 必须可见）；检查更新 =
  拉回窗口 + 打开设置「关于」页并就地触发检查（结果文案仍只有那一处出处）；退出 = 既有
  `_allowClose` + `Close()`。
- 托盘图标素材：macOS 用新烤的模板图 `Assets/tray-icon.png`（黑 + alpha，系统按明暗菜单栏自动反色；
  彩色 app-icon 走模板会变成一块实心圆角方块），由 `scripts/make-tray-icon.py` 生成；Windows/Linux
  仍用 `app-icon.png`。csproj 的 `AvaloniaResource` 显式列表同步加上它。

## 关键取舍

- **左键 = 查词，不做「切换主窗口」**：用户点定；主窗口的入口留在菜单第一项，两个动作各归其位。
- **无词典数据时左键直接唤起主窗口**，不弹一个只能报错的迷你窗——首次启动的下载面板在主窗口里。
- **图标常驻但不改关闭语义**：常驻只解决可达性；「关窗 = 收托盘 / 退出」仍由既有设置位与一次询问
  决定，绝不因为「有图标了」就偷偷把关闭改成隐藏。
- **迷你窗不渲染近义/反义/联想、不做双击取词、不做翻译**：它是「瞄一眼」，完整词条回主窗口。
- **不做托盘 tooltip 动态摘要 / 托盘菜单「最近搜索」**：摘要要实时算、最近搜索属于主窗口语境。
- **不做圆角/毛玻璃弹层**：透明窗口在各平台的合成分支不同（Windows 10 无合成时四角发灰），
  方角卡片先保证正确；观感升级留待用户点名。
- **不改 `ShutdownMode`**：隐形窗口不参与关窗计数，默认 `OnLastWindowClose` 已经让进程在
  「主窗隐藏、迷你窗关闭」时存活。

## 评估过、不做的

- 用 `Shell_NotifyIconGetRect` 精确定位 Windows 托盘图标：Avalonia 的托盘 GUID 不可得，光标位置
  同样精确（点击瞬间光标就在图标上），不值得为了理论精度去逆向它的隐藏窗口。
- SF Symbol 作 mac 菜单栏图标（`character.book.closed`）：系统版本可用性分叉，且脱离品牌图形；
  自烤模板图与 app-icon 同族。
- 迷你窗独立进程 / 全局热键唤出：进程间共享词典连接与发音链的复杂度远超收益。

## 测试

`tests/StupidDict.App.Tests/TrayTests.cs`（fake `ITrayHost`，零真实托盘）与
`QuickLookupTests.cs`（`[AvaloniaFact]`，`LookupOverride` 注入假查询）：

- 启动即建图标、动作齐全；退出/关窗时 Dispose。
- 左键切换迷你窗（开 → 关），失焦守卫吞一次后能再开。
- 无词典数据时左键走主窗口。
- 菜单四项：显示主窗口（隐藏态恢复）、设置（拉回窗口 + 开对话框）、检查更新（拉回 + 关于页 +
  检查已启动，假 `UpdateChecker` 零网络）、退出（真关闭）。
- 迷你窗：输入 → 防抖 → 词头/释义上屏；未命中给建议；Esc 关；回车回调词条；位置纯函数
  （光标上方、状态项下方、角落、越界钳制）。
- 既有 `CloseToTrayTests` 适配：图标不再随恢复消失、`Show` 在启动而非首次收托盘时调用。

## 真机验证状态（2026-11-17）

macOS（Apple Silicon，原生 AOT 产物）：`MacStatusItem` 安装成功、`TryGetStatusItemFrame` 可用，应用
运行与退出无异常、无回落告警；迷你查词卡在 headless 下渲染经截图目检（日/夜两套主题）。**未验证**：
开发机菜单栏处于自动隐藏状态（截屏里没有菜单栏，状态项 frame 退化为 0 高），所以「弹层贴在图标正
下方」与右键原生菜单的手感只能靠用户在正常桌面上目检。Windows 的 `Clicked` / 右键菜单 / 光标锚定
同理未在真机跑过（headless 只能验接线，锚定数学由纯函数测试钉住）。
