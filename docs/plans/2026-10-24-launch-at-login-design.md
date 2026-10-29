# 开机自启 — 设计记录（2026-10-24）

> 本文档为补记：实现进入 PR 时设计文档按 2026-10-20 起的「`docs/plans/` 本地留存」约定（.gitignore）未上传；评审时维护者决定撤销该约定、恢复设计文档入库——本文与同批恢复入库的 2026-10-20 四篇本地文档（avalonia-12 升级、云端翻译三篇）一并进仓。内容由维护者依实现与评审记录补写，与 PR 中已实现的代码一致（含评审后追加的原子写与桌面入口图标两处收尾）。

## 背景 / 目标

用户点名要求的可选设置（仓库「新增设置项原则上禁止」的点名称例外，与云端翻译同级，README/AGENTS 已同步改写）：设置 → 通用页签一个开关，默认关闭。打开后随系统登录自动启动，关掉即移除。硬性约束：

- **三平台全 per-user、免管理员**——应用是绿色 zip 分发，没有安装器提权的机会。
- **零常驻、零联网**——开机拉起即正常显示主窗口（应用没有托盘，「后台隐藏启动」等于失踪）；自启注册本身也不产生任何网络请求。
- **best-effort**——注册失败绝不崩应用、绝不阻塞启动；这是个锦上添花的功能，不能变成故障面。

## 核心语义：设置是唯一事实源

系统状态（注册表值 / LaunchAgent plist / autostart 桌面文件）永远向 `AppSettings.LaunchAtLogin` 收敛，收敛点两处：

1. **每次应用启动**（`App.OnFrameworkInitializationCompleted`，`WireSettings` 之后调 `AutoStart.Apply`）——覆盖三类漂移：便携版被挪动后条目里的旧路径、用户手动删了条目但设置还开着、settings.json 被重置后系统里的残留条目。
2. **设置开关切换**（`SettingsWindow` 经 `applyLaunchAtLogin` 注入缝调用）——即时生效，与主题/语言同语义（非翻译页的 draft+保存）。

收敛规则：启用则写入（内容与期望一致则跳过，避免每次启动无谓写盘）、禁用则移除、什么都没有则不动。副作用**刻意不挂进 `WireSettings` 的 switch**：测试直接调 `WireSettings`，挂那里会写真实注册表/用户文件；switch 只加空 case 保证落盘（`default: return` 会静默丢弃未登记属性，`LaunchAtLoginPropertyChangePersists` 钉住）。

## 平台选型

- **Windows：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`**（值名 `StupidDict`，数据 = 带引号 exe 路径）。备选：Task Scheduler（能做更精细的触发但重出一个抽象面）、开始菜单 Startup 文件夹（同 per-user 但 shell:startup 路径解析间接，无收益）。Run 键的额外好处：任务管理器「启动」页对它的禁用写在 `StartupApproved\Run`，应用按内容比对重写 Run 值**不会**覆盖用户在任务管理器里做的禁用，两种关闭途径兼容。
- **macOS：`~/Library/LaunchAgents/com.cholf5.stupiddict.plist`**（Label = bundle id 与 package.sh 的 `CFBundleIdentifier` 一致，`Program` = `Environment.ProcessPath`，`RunAtLoad`）。备选：SMAppService（macOS 13+ 才有，且与系统设置「登录项」页耦合，语义不再由文件内容唯一决定，收敛模型复杂化）；`launchctl` 即时 load（需求是「登录自启」不是「立即拉起」，落盘文件下次登录自然生效，还免掉 launchctl 的进程依赖）。plist 里 `Program` 用单个字符串路径（launchd 不经 shell，无引号问题），内容经 `WebUtility.HtmlEncode` 转义 XML 特殊字符（`& < > ' "` 与 U+00A0–U+00FF；CJK 等更高码位按原样 UTF-8 落盘，文件声明 UTF-8，XML 合法）。
- **Linux：`~/.config/autostart/StupidDict.desktop`**（XDG autostart，`SpecialFolder.ApplicationData` 在 Linux 上映射 `XDG_CONFIG_HOME`）。`Exec` 带引号处理空格路径；`Name[zh_CN]=傻瓜词典` 本地化名；`Icon` 指向 exe 同目录的 `app-icon.png`（package.sh 在 Linux zip 里携带），文件不存在时桌面环境回退通用图标、无副作用。

## 实现要点

- **平台分派用运行时 `OperatingSystem.IsX` 而非 `#if`**——测试项目只编 net10.0，`#if WINDOWS` 分支任何 CI 机器都测不到。注册表 helper 首行 `IsWindows` 守卫满足 CA1416（`TreatWarningsAsErrors` 下平台分析器告警即失败）；`Microsoft.Win32.Registry` 在 net10.0 可编译，照 `ProxyDetector` 先例（try/catch 静默 + `finally Dispose`）。
- **注册表 / 文件 helper 全带路径参数**——测试走 scratch 键与临时目录，绝不碰真实 Run 键或用户 LaunchAgents（照 `SingleInstanceGuard.TryAcquire(lockPath, pipeName)` 注入先例）。
- **条目文件原子写**：temp 文件 + `File.Move(overwrite)` 进位，与 `SettingsService` 的落盘纪律一致——写一半崩溃不会留下半截 plist/desktop 文件给 launchd 或会话管理器；崩溃残留的 `.tmp` 由下一次写入覆盖、`RemoveEntryFile` 一并清扫。注册表 `SetValue` 天然原子。
- **整个 `Apply` best-effort 包 try/catch**：策略限制、只读家目录、注册表故障都吞掉；失败不回滚设置（下次启动收敛重试——「设置保留、系统状态下次追上」是这个模型下的自愈）。

## 评估过、不做的

- **托盘 / 最小化启动**：应用没有托盘，隐藏启动等于失踪；拉起即显示主窗口，重复拉起由单实例守卫兜底（后到实例发 activate 后退出）。
- **启用后立即拉起一次**：需求是「登录自启」，不是「现在启动」；不在开关语义里掺启动副作用。
- **注册失败回滚设置**：不回滚。回滚会把「暂时性故障」（杀软锁文件、注册表被策略锁）固化成「设置丢了」，而收敛模型下失败只意味着下次启动重试。
- **卸载清理**：绿色 zip 无卸载器，应用被删后残留条目指向不存在的路径——launchd / 会话管理器对启动失败静默跳过，无害；不做定时巡检之类的清理机制。

## 测试

`AutoStartTests`（纯 xunit，不碰 Avalonia）：Run 值引号、plist/desktop 内容格式、条目文件写/比/删往返（含原子写不留 `.tmp`、Remove 清残留 `.tmp`）、`ApplyFileEntry` 收敛语义（禁用无文件不落盘、启用写目录、内容一致无 churn、陈旧内容重写、禁用移除）、scratch 注册表键往返（仅 Windows 实际执行——CI 由 **windows 测试 job** 覆盖该路径，ubuntu/macOS 上早退）。Headless UI：`TogglingLaunchAtLoginAppliesAndPersists`（recorder 缝驱动开关、落盘、重开窗口反映已存设置、构造期赋初值不触发注册）；`LaunchAtLoginPropertyChangePersists` 钉住 `WireSettings` 落盘收编。注册表 helper 的真实 API 行为在任何 Linux CI 上都测不到——windows job 跑全套测试正是为此（顺带覆盖 AGENTS「Windows 三条额外约束」在 CI 的历史盲区）。
