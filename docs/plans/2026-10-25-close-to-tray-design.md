# 关闭到托盘（Close to Tray）— 设计记录（2026-10-25）

## 背景 / 目标

用户要求「加一个关闭后最小化到托盘选项，在第一次关闭窗口时弹出来询问」。前一份设计（`2026-10-24-launch-at-login-design.md`）把托盘列为 YAGNI——应用从无托盘；本次由用户点名，按 README「用户点名的例外」处理，与开机自启、翻译同级。

目标：

- **默认行为不变**：没做过选择的人，关闭仍是退出。
- 第一次关闭窗口时**问一次**，答案记住，之后不再打扰。
- 设置「通用」里留一个开关，随时改回来。
- 收进托盘后必须能**找回来**、必须能**真退出**。

## 做了什么

- `AppSettings` 两个持久化位：
  - `MinimizeToTrayOnClose`（bool，默认关）— 关闭时是否收进托盘；
  - `CloseToTrayAsked`（bool，默认 false）— 那次询问是否已答复。取消（Esc / 关对话框）**不算答复**，下次关闭再问。
- **首次关闭流程**（`MainWindow.OnWindowClosing`）：`e.Cancel = true` 后经 dispatcher 延迟弹 `Settings/CloseToTrayPrompt`（模态），三个结果：
  - 最小化到托盘 → 写两个设置位并 `HideToTray()`；
  - 退出应用 → 写 `CloseToTrayAsked` 后真正关闭；
  - 取消 → 什么都不写，窗口留在原地。
- **之后每次关闭**：`CloseToTrayAsked == true` 时只看 `MinimizeToTrayOnClose`——开则收托盘，关则退出。
- **`Tray/TrayIconHost.cs`**：`ITrayHost` 接口（测试注入 recorder）+ 真实 `TrayIcon` 实现。图标按需创建、隐藏即销毁；菜单两项「显示主窗口」「退出」；Win32/部分 Linux DE 的图标点击也恢复窗口（macOS 从不触发 `Clicked`，菜单是唯一入口）；tooltip 用应用名；语言切换时更新菜单文案。
- **`MainWindow.RestoreFromTray()`**：托盘菜单与单实例激活共用。隐藏中 → `Show()` + 必要时恢复正常态 + `Activate()`（`Activated` 既有处理器把焦点放回搜索框并全选）；只是最小化 → 沿用 `SingleInstanceGuard.BringToFront`；已关闭 → 忽略（进程正在退出）。
- **退出**：托盘菜单「退出」→ `_allowClose = true` 后 `Close()`，默认 `OnLastWindowClose` 生命周期收尾。
- 设置「通用」页签加 `MinimizeToTrayCheckBox`；**勾选或取消都等于回答了那次询问**（写 `CloseToTrayAsked = true`），不会出现「用户刚在设置里选了不要，关闭时又被问一次」。
- 收托盘时立即 `SaveWindowBounds()`：收进去之后可能再也不会经过 `Closed`（被任务管理器杀掉、直接重启），不存就丢几何。

## 关键取舍

- **默认关闭（opt-in），不默认收托盘**。默认收托盘会让第一次关闭「点了没反应」，是傻瓜词典不该有的体验；问一次、记住、默认按钮是「退出应用」（回车等于什么都不改变）。
- **只拦用户发起（`WindowCloseReason.WindowClosing`）的关闭**。`ApplicationShutdown` / `OSShutdown` / `OwnerWindowClosing` 一律放行：托盘偏好绝不能挂住注销、系统关机或 `lifetime.Shutdown()`。
- **不用 `ShutdownMode.OnExplicitShutdown`**。`Hide()` 不触发窗口关闭，默认 `OnLastWindowClose` 已经让进程存活；改成显式模式反而要额外接线「正常关闭也要退出」。
- **询问在 dispatcher 上延迟执行**，不在 `Closing` 栈上直接开模态窗：Windows/macOS 都在自己的关闭回调里 raise `Closing`，同步开对话框是重入。`e.Cancel` 先落下，等待期间窗口处于「已否决关闭」的稳定状态。
- **托盘图标只在隐藏期间存在**。产品是零配置傻瓜词典，能少一个常驻图标就少一个；恢复即销毁。
- **无托盘后端时按退出处理，绝不 Hide**。看不到窗口又没有图标 = 进程失联。`ITrayHost.Show` 返回 false 时走真关闭。
- **无托盘但已隐藏也不是死局**：单实例守卫生效——再启动一次应用，`RestoreFromTray` 会把窗口拉回来（GNOME 等不显示托盘图标的桌面靠这条兜底）。
- **语言切换不重建菜单，只改文案**；隐藏期间用户进不了设置，成本几乎为零但顺手正确。
- **设置里的开关即答复**：显式操作比一次事后弹框更可信。

## 评估过、不做的

- **常驻托盘图标（打开应用就出现）**：多一个长期占位，且与「关闭到托盘」无关；只在需要时出现。
- **询问框加「记住我的选择」复选框**：询问本身就是一次性决策，多一个复选框只是多一个状态。
- **最小化按钮也收托盘**：用户要的是「关闭后」，最小化是系统手势，不劫持。
- **托盘菜单放「设置 / 最近搜索」**：YAGNI，托盘只承担「找回来 / 退出」。
- **自绘/模板托盘图标**（macOS template image、夜间变体）：用应用图标 PNG；模板图需要单色素材，未做。
- **收托盘时弹系统通知**：托盘图标本身就是提示。
- **托盘创建失败的自动重试**：失败即当次退出，下次启动再判断；重试没有可观察的收益。
- **为 `RestoreFromTray` 的「已最小化」分支单写测试**：`SingleInstanceGuard.BringToFront` 的语义早已被单实例测试覆盖，本功能只新增「隐藏 → 显示」这条。

## 测试

`tests/StupidDict.App.Tests/CloseToTrayTests.cs`（13 个，`[AvaloniaFact]`，托盘经 `ITrayHost` 注入 fake，零真实托盘）：

- 首次关闭：选托盘 → 隐藏 + 两个设置位；选退出 → 真关闭 + 只记 `CloseToTrayAsked`；取消 → 窗口留着、询问不消费、第二次再问。
- 已答复后不再询问（override 里直接抛异常钉住「没被调用」）。
- 托盘恢复（窗口回来、图标消失、焦点回搜索框）；托盘退出（真关闭、图标 Dispose）。
- 无托盘后端：直接收托盘与首次选托盘两条路径都回退为真关闭。
- 真对话框：两个按钮各自上报选择、直接关闭/Esc 读作 Cancel。
- **真对话框经 `ShowDialog` 的接线**（两个用例）：直接把答案写在 `Choice` 上再调无参 `Close()`，`ShowDialog<TResult>` 收到的是 `null`（读作 Cancel），按钮在真机上等于没反应——`ThePromptReportsItsAnswerThroughShowDialog` 钉住回传值，`FirstCloseThroughTheRealPromptHidesTheWindow` 走完整链路（首次关闭 → 真对话框 → 点「最小化到托盘」→ 隐藏 + 两个设置位）。只断言 `Choice` 的用例测不到这一层，别再退回去。
- 设置开关：勾/取消写共享设置并落盘、重开回显、**开关即回答询问**；`WireSettings` 收编两个新字段（选托盘后 `settings.json` 里两个位都在）。
- 真实 `TrayIconHost` 在 headless 下 Show/Hide/Dispose 不抛异常（返回值平台相关，不做断言）。

既有用例的适配：`WindowBoundsTests` 三个关闭用例显式加 `CloseToTrayAsked = true`（它们测几何，不是在测询问）；`MainWindow` 在**不传 settings** 时的兜底实例同样预置该位——那是测试/设计器路径，生产永远传入从磁盘加载的 `AppSettings`，否则 headless 测试的 `Close()` 会弹一个没人应答的模态框而卡死。
