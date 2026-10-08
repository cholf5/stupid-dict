# macOS 菜单栏应用名跟随界面语言

日期：2026-10-08

## 背景与目标

macOS 裸跑（`dotnet run`）时，屏幕左上角系统菜单栏的应用名是 Avalonia 的默认名，
窗口标题正常。Windows 不受影响——没有任何 Windows 侧代码读这个属性。

根因（Avalonia 11.3.22 源码核实）：

- `Application` 构造函数把 `Name` 写成 `"Avalonia Application"`
  （`Application.cs`，属性注册默认值是 null，字符串来自 ctor 赋值）；
- AvaloniaNative 平台初始化 `AvaloniaNativePlatform.DoInitialize` →
  `SetupApplicationName()` 把 `Application.Current.Name` 传给原生
  `SetApplicationTitle`；原生实现（`native/Avalonia.Native/src/OSX/main.mm`）
  就是 `[[NSProcessInfo processInfo] setProcessName:]`——未打包进程的菜单栏
  名取自进程名，这条调用在任何时候都可以重复执行；
- 裸进程没有 bundle，读不到 Info.plist；打包的 .app 菜单栏名来自
  `CFBundleName`，不受进程名影响。

目标：菜单栏名不再写死，与窗口标题一样跟随界面语言（zh「傻瓜词典」/ en
"Stupid Dict"），切语言即时生效，不加设置项。

## 做了什么

- `MacAppTitle.TrySet(name)`（新文件，与 `MacDockIcon` 同一套 libobjc 互操作
  姿势）：macOS 上重放 `NSProcessInfo.setProcessName:`，失败静默；
  非 macOS 直接返回。
- `App` 构造函数：`Name = Translations.Instance.AppName`（机器 UI 文化种子值，
  赶在平台初始化的第一次推送之前），并订阅 `Translations.PropertyChanged`；
  `AppName` 变化时 `ApplyAppName()`——更新 `Application.Name` 并重放原生推送。
  启动路径靠 `WireSettings` 的初始 `SetLanguage`（逐属性 raise）自然覆盖
  保存的语言选择，不需要额外调用点。
- 数据源与窗口标题绑定同源（`Translations.AppName`），删掉了此前 XAML 里
  写死的 `Name="Stupid Dict"`。
- 回归测试 `MacMenuBarAppNameFollowsUiLanguage`：钉住「切语言 →
  `App.Current.Name` 跟随」的接线（原生推送本身 headless 观察不到）。

## 评估过什么、为何不做

- **XAML 写死 `Name`**（第一版方案）：解决默认名但仍是静态的，用户明确要
  多语言，废弃；`Name` 改由代码跟随 Translations。
- **升 Avalonia 12 用 `IAvaloniaAppDelegate.GetAppName()`**：为多语言也不
  值得跨大版本——该 API 同样只在平台初始化读取一次，切语言仍需自行重推；
  11.3 方案覆盖同一行为，升级后语义不变。
- **改打包 .app 的菜单栏名跟随应用内语言**：做不到。打包后菜单栏名由
  macOS 从 bundle 读取（`CFBundleName`），进程名不参与，运行时无法覆盖；
  这是系统行为。若想按**系统**语言本地化，正路是 bundle 里加
  `zh-Hans.lproj/InfoPlist.strings`（`CFBundleDisplayName`），属 package.sh
  的后续可选项，与「应用内切语言」是两回事。
- **改进程名的另一条原生分支**（`main.mm` 里 `disableSetProcessName == 1`
  时直接 `rootMenu setTitle:`）：依赖系统不重绘首项标题，不可靠；默认
  分支的 `setProcessName:` 是实证有效的那条路（用户看到的默认名即出自它）。

## 已知取舍

- 打包 .app 的菜单栏名恒为 `CFBundleName`（"Stupid Dict"），不跟随应用内
  语言切换——系统限制，见上；裸 `dotnet run` 完全跟随。
- 菜单栏最终效果 headless 无法断言，靠 `App.Current.Name` 接线测试 +
  跑起来人工目检兜底（`dotnet run` 后在设置里切语言看左上角）。
- `MacAppTitle` 在 headless 测试进程里也会执行（`WireSettings` 路径），
  效果只是改掉测试进程名，无害；非 macOS 平台为纯 no-op。
