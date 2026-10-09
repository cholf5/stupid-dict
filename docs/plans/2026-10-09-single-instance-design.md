# 单实例守卫 — 设计记录（2026-10-09）

## 背景 / 目标

用户反馈：Windows 上可以双开（再点一次图标/再起一个进程就多出一个窗口）。两个进程各自独立，历史库、`settings.json`、下载状态被交错读写。目标是同帐户同时只允许一个实例；二次启动不静默吞掉——把已运行窗口带到前台（最小化则还原），这是所有 Windows 应用的标准预期，也符合「傻瓜词典：用户再点一次图标就是想让窗口出来」。不新增设置项。macOS 的 bundle 经 LaunchServices 本来就双开不了，Linux 无人管但同样会双开——三平台统一处理。

## 做了什么

`src/StupidDict.App/SingleInstance.cs`（`SingleInstanceGuard`），`Program.Main` 在 `BuildAvaloniaApp()` 之前认领，guard 挂在 Main 的 `using` 上随进程退出释放：

- **所有权 = 独占锁文件**：`new FileStream(LockFilePath, OpenOrCreate, ReadWrite, FileShare.None)`。持有期间任何其他进程（含同进程）再打开即 `IOException` → 判定已有实例。`LockFilePath` 在临时目录（Windows/macOS 本就按用户隔离；Linux 共享 /tmp，文件名带用户名哈希按用户区分）。进程无论怎么死（正常退出/崩溃/被杀）OS 都会关 fd 释放锁，零清理逻辑、无 abandoned 状态。
- **激活 = 命名管道**：首实例起后台线程循环 `NamedPipeServerStream` 监听；后到实例 `Connect` 后写一行 `activate` 即退出。首实例的 `ActivationRequested` 事件在监听线程上触发，App 订阅后 `Dispatcher.UIThread.Post` 调 `BringToFront`（最小化还原 + `Activate`；不调 Show——应用无托盘隐藏态）。后到实例的信号带约 1.5s 重试窗（10 次 × 100ms + 50ms 间隔），覆盖「两个实例几乎同时启动」的竞态——先认领到锁的赢，输家重试到监听者就位。
- **跨平台零 `#if`**：命名管道在 Windows/macOS/Linux 同一套 API（Unix 映射为 `$TMPDIR/CoreFxPipe_<name>` 的 unix domain socket）。macOS bundle 由 LaunchServices 兜底，裸跑二进制（`dotnet run`）也获得同款行为；Linux 从无到有。
- **fail-open 哲学**：锁文件建不出来（只读临时目录、异类沙箱）→ 无守卫照常启动；监听端建不出来（SIGKILL 残留 socket、路径异常，有界重试 3×200ms 后放弃）→ 单实例仍由锁文件保证，只丢激活礼遇；通知发不出去 → 后到实例照样退出（「不开第二个进程」是不变量，激活只是加在其上的礼遇）。守卫自己的任何故障都不能阻止启动。
- App 端订阅时检查 `IsVisible`：主窗口已关（应用正在退出）就不去激活一个死窗口。

## 关键依据：为什么不是命名 Mutex（macOS 上它是坏的）

初版用 `Mutex(false, name)` + `WaitOne(0)` 认领，本地探针实测 **macOS（Darwin 25）上 .NET 命名 Mutex 不互斥**：两个 `Mutex(false, 同名)` 的 `WaitOne(0)` 都返回 True——第二实例照样当「首实例」，且不抛任何异常（不 fail-open 都来不及发现）。新测试逮住后换成锁文件方案。`FileShare.None` 独占打开在三平台语义一致（Unix 底层是 flock/fcntl），跨进程与同进程都拦得住。管道侧两个行为也已探针验证：`WaitForConnectionAsync(token)` 在 macOS 正常；dispose 服务端能解阻塞 pending 的 wait（SocketException），Dispose 据此两段式 join。

**管道名长度红线**：Unix 上管道名变成 `$TMPDIR/CoreFxPipe_<name>`，unix socket 路径上限 104 字节；macOS 的 `$TMPDIR` 本身约 49–51 字符，名字预算只剩约 44 字符。`Id` = `stupiddict-` + 用户名 SHA256 前 12 hex（共 23 字符，socket 路径约 84）——**别加长 `Id`**，测试管道名同理用 16 字符短名（`SingleInstanceTests` 有注释）。锁文件是普通文件，无此限制。

## 评估过、不做的

- **只守 Windows**（按字面需求）：Linux 双开同样交错写 history.db / settings.json，行为不一致没有收益；跨平台实现反而零 `#if` 成本，测试还能在 CI（ubuntu）上跑到。
- **命名 Mutex**：macOS 坏（见上）；Windows 好但为省一个文件句柄引入平台分叉不值。
- **只用管道当锁**：Windows 的 `NamedPipeServerStream` 允许同名多实例，两个同时启动的进程都能建成功，当不了原子认领。
- **Win32 `FindWindow` + `SetForegroundWindow` 激活**：要猜窗口标题（本地化会变），仅 Windows 可用；管道通知一套代码三平台生效。
- **第二实例弹「已在运行」提示框**：窗口弹到前台本身就是自解释反馈，弹窗是打扰；通知失败（锁在而管道不在的窄窗）静默退出，下次再点即正常。
- **flock 自封装 / D-Bus 单实例**（Linux 惯例通道）：平台专用代码换不来额外保证，`FileShare.None` 与命名管道已覆盖。
- **转发命令行参数**（如文件关联打开词条）：无文件关联无协议，YAGNI。
- **「允许多实例」设置项**：违反「新增设置项原则上禁止」。
- **Windows 前台锁的 hack**（topmost 闪烁抢焦点）：后台进程抢前台被系统降级为任务栏闪烁是标准可接受结果。

## 极端场景记录

- **SIGKILL 后**：锁文件残留但 fd 已释放，下次正常启动；Unix 侧残留的 socket 文件可能让新监听端 bind 失败 → 有界重试后放弃，实例照常跑（无激活礼遇）。
- **/tmp 清理器**：运行中删掉锁文件（Unix 可删已打开文件）→ 新实例能重新认领 → 短暂双开可能。极端且后果轻（SQLite 本身并发安全），接受。
- **跨版本**：锁/管道名不含版本号。升级安装时旧版还开着，新版起不来并激活旧窗口——关旧开新即正常，与单实例语义一致。
- **几乎同时双开**：竞态由「锁认领的原子性 + 通知重试窗」收敛，认领输家最多等约 1.5s 后退出。

## 测试

`SingleInstanceTests`（App.Tests，全平台 CI 可跑）：第二次认领被拒、Dispose 释放后可再认领、管道信号触发 `ActivationRequested`、`BringToFront` 还原最小化窗口（headless）。崩溃释放锁是 OS 文件句柄语义，不做进程级模拟。headless 测试不经 `Program.Main`（测试自建窗口），internal 重载注入随机锁路径/管道名，不触碰静态 `Current`。
