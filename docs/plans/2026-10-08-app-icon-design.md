# 应用图标接入

日期：2026-10-08

## 背景与目标

项目一直没有应用图标（AGENTS.md 原记录"macOS Dock 图标…本应用没处理"）。本次
提供源图 `~/Downloads/stupid-dict-icon.png`（1024×1024，"A↔中"设计），要求参考
`../inpaint` 的现成方案接入。目标：Windows exe（资源管理器/任务栏）、macOS Dock
（打包 bundle 与裸 `dotnet run` 两种形态）、各平台窗口标题栏都有图标，且三端形状一致。

## 源图的一个前置问题

源图是**全出血方形、RGB 无 alpha**（四角就是藏青底色），直接用会在 Dock/任务栏
露出方角。inpaint 的结论（`MacDockIcon` 注释）是运行时设置的 Dock 图标不走
macOS 26 给 bundle 图标自动加的圆角蒙版，Windows .ico 同样不加工——**圆角必须
烤在素材里**，三端才能一致。

按 inpaint 成品量测出的几何重排（4× 超采样抗锯齿）：

- 1024 透明画布，内容缩放到 830×830 居中（四边 97px 透明边距，≈Apple 图标网格）；
- 圆角半径 194 ≈ 内容的 23.4%（Apple 圆角比例，与 inpaint 的 alpha 轮廓拟合一致）。

## 做了什么

- **素材三件套**（`src/StupidDict.App/Assets/`）：
  - `app-icon.png` — 1024 成品母版，后续重生成 ico/icns 的基准；
  - `app-icon.ico` — Pillow 默认 7 帧（16/24/32/48/64/128/256，与 inpaint 同帧集）；
  - `app-icon.icns` — `iconutil` 十档 iconset（16…1024）。
- **csproj**：`ApplicationIcon` 内嵌 Windows exe；`AvaloniaResource` 把三件套接进
  avares://（**显式列文件而非 `Assets/**`——本项目的 Assets/ 里还有 .cs 源文件，
  与 inpaint 目录布局不同**）；`AllowUnsafeBlocks`（MacDockIcon 的指针固定）。
- **窗口**：MainWindow / SettingsWindow 挂 `Icon="avares://StupidDict/Assets/app-icon.ico"`。
  avares 前缀是 AssemblyName `StupidDict` 而非项目名——项目此前没有任何 avares
  用法可参照，靠测试钉住（URI 错误会在 XAML 加载时直接抛）。
- **`MacDockIcon.cs`**：照 inpaint 移植。桌面生命周期分支启动后把内嵌 icns 经
  libobjc 手发 ObjC 消息设给 NSApplication，补裸 `dotnet run` 的 Dock 图标；
  纯外观，任何失败静默。headless 测试不走桌面生命周期，不会触发。
- **package.sh**：.app 增加 `Contents/Resources/app-icon.icns` + Info.plist
  `CFBundleIconFile`；CI 的 macos job 走同一脚本，无单独改动。
- **回归测试** `WindowsCarryAppIconFromEmbeddedAssets`：两个窗口 Icon 非 null +
  icns 流可打开。

## 评估过 / 为何不做

- **只发 ico、复用给 macOS**：ico 单帧上限 256px，Retina Dock 放大后糊；icns 是
  NSImage 原生格式且含全分辨率档。inpaint 同为双素材，沿用。
- **依赖 macOS 26 的 bundle 自动蒙版**：只作用于 bundle 图标；运行时设置
  （MacDockIcon）与 Windows .ico 都不经过，且源图无 alpha、蒙版无边界可依。
  烤圆角是唯一让三端一致的方案。
- **生成脚本入库**（如 `scripts/make-icon.sh`）：素材是一次性资产，母版已固化为
  `app-icon.png` 入库，重生成属极低频操作（YAGNI）。参数都记录在本档，需要时
  半小时可复现。
- **Linux .desktop / Icon 安装位**：Linux 只发 zip 无安装器，窗口图标已由
  `Window.Icon` 覆盖（YAGNI）。

## 验证

- 全量测试绿：Core 41 + App 55（含新增测试）。
- 本地 `package.sh --rids osx-arm64` 冒烟：bundle 内 icns 与源逐字节一致、
  Info.plist 过 `plutil -lint`、`CFBundleIconFile` 在位。

## 追加排查：裸 `dotnet run` Dock 图标一度不可见

首次实现在 `OnFrameworkInitializationCompleted` 里同步把 **icns** 设给
NSApplication，用户反馈 Dock 仍是通用图标。排查结论（本机 macOS 26.5）：

- **探针盲区**：`NSRunningApplication.icon` 只读 LaunchServices/IconServices 层，
  对运行时 `setApplicationIconImage` 完全不反映——用"红 bundle 图标 + 运行时设
  藏青"的对照实验证实（探针始终显示 bundle 图标）。所有走探针的"仍是 exec"
  判定全部无效；本机终端又没有屏幕录制授权，Dock 无法程序化取证。
- **API 本身没坏**：调研（Electron/SDL/Godot 实现 + Apple 文档 + 无回归报告）
  确认 macOS 26 上运行时设 Dock 图标对裸进程仍有效，条件是 Regular 策略 +
  主线程；Electron 在 Tahoe 上的可用性报告明确是 **flattened PNG**。
- **与全部已知可用案例的唯一差异是素材格式**：我们喂 icns，可查到的实现
  （Electron `nativeImage`、SDL surface 转 NSImage、Godot PNG）全是扁平位图。
  改喂内嵌 `app-icon.png`（1024 扁平、圆角已烤），icns 保留给 bundle 路径
  （那是它的正确用途），不再内嵌进程序集（省 880KB，打包走文件系统）。
- **时机**：调用移到 App 空闲优先级（`Dispatcher.Post(ApplicationIdle)`），
  机械验证落在 `isRunning=true`、`activationPolicy=regular` 之后——即 App 完成
  启动、run loop 运转后；代价是启动瞬间可能闪一下通用图标。Godot 在 26.0 上
  早期设置也能渲染，时机大概率不是主因，但延迟版同时覆盖时机与素材两种假设。
- 诊断用过的 `isRunning`/`activationPolicy` 探针已移除，代码回到纯设置路径。

运行时 Dock 图标最终只能人眼确认（见 AGENTS.md 平台坑条目）。若个别机器仍
不生效，兜底方案是用 package.sh 产出的 bundle 跑（bundle 图标层已在本机
端到端验证）。
