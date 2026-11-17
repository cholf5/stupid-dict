# 启动检查 + 弹窗 + 自动更新（Self Update，移植 Tokenmetry）— 设计记录（2026-11-17）

## 背景 / 目标

用户要求「热更抄下 Tokenmetry（启动时检测 + 弹窗 + 自动更新）」。改动前本应用只有「设置 → 关于 →
检查更新」：它只做一次 GitHub `releases/latest` 比对，发现新版本给一个「打开发布页」按钮，剩下的
下载、解压、替换全靠用户手动完成。本次由用户点名把 tokenmetry 的整套自更新搬过来，包括两条此前
被列为「刻意不做」的行为：**启动后自动检查**（周节流）与**应用自己替换文件并重启**。

目标：

- **启动 30 秒后静默检查**（≥7 天才真的发请求）：有新版才弹窗；已是最新/检查失败绝不打扰。
- **弹窗**（`UpdatePromptWindow`，非模态、主窗所有）：发现新版本 → 立即更新（带进度条、可取消）→
  就绪 → 立即重启并更新；重启后再弹一次「已更新到 vX」，替换失败则「上次更新未生效」。
- **自动落地**：下载 → SHA-256 校验 → 解压校验 → 替换文件 → 拉起新版本 → 旧进程退出。
  Windows 已安装副本（Inno Setup 装的）走安装器静默升级；绿色版 / macOS / Linux 由应用自己换文件。
- 设置「关于」页保持唯一的手动入口，检查结果与下载入口都在那里（下载入口把话筒交给弹窗，
  设置是模态的，不能同时显示进度）。

## 做了什么

- **`Update/UpdateAssets.cs`**：仓库、资产命名、平台挑选、镜像列表的唯一出处。资产名与 CI 产物
  严格一致：`StupidDict-{version}-{rid}.zip`、`StupidDict-{version}-win-x64-setup.exe`，各配一份
  `<资产名>.sha256`。Windows 只出 win-x64（ARM 走系统模拟），macOS 按进程架构分 arm64/x64。
  另含「是否安装副本」判定（进程可执行文件目录 == `%LOCALAPPDATA%\Programs\Stupid Dict`）。
- **`Update/UpdateChecksum.cs`**：SHA-256 计算与 `<hex>  <文件名>` 两种写法的解析。
  **自更新场景下校验和不是加强项而是前提**：下载走三个第三方镜像前缀，能换掉包就等于能换掉用户的
  程序；校验不过就删包并拒绝安装。
- **`Update/UpdateDownloadService.cs`**：镜像依次回退、`.part` → 改名、Content-Length 完整性、
  载荷魔数（zip `PK\x03\x04` / 安装器 `MZ`）、先取 `.sha256` 再下资产的 `DownloadVerifiedAsync`。
  超时语义照 tokenmetry：`HttpClient.Timeout` 抛的 `TaskCanceledException` 不算用户取消，否则慢网下
  三个镜像一个也试不到。
- **`Update/UpdateJournal.cs`**：`<数据目录>/update-state.json`，记录「正在装 vX、备份在哪」。
  写它的是即将退出的旧进程，读它的是重启后的新进程——版本号对上 = 成功（删备份、弹「已更新到 vX」）；
  对不上 = 替换没生效（自动回滚，见下）。JSON 走源生成，不碰反射。
- **`Update/UpdateStager.cs`**：便携形态的自替换。两种形态分开处理：
  - **macOS `.app`**：**整包换**——旧包 rename 进备分区、新包 rename 进位、把 zip 里没有的用户数据
    （`Contents/Resources/dictionary.db`、遗留 `audio/`）从旧包拷回，拷了东西才 `codesign --force -s -`
    重新封签（与 package.sh 同一手法；封签与内容不符正是 Gatekeeper 判「已损坏」的成因）。整包换而不是
    逐文件覆盖，是因为逐文件会把旧版本残留的二进制留在包内，封签必然对不上。
  - **Windows 绿色版 / Linux**：**逐文件覆盖**——每个被替换的文件先挪进备分区，新文件再进位；
    可执行文件放最后；任何一步失败按反向顺序回滚。逐文件而不是整目录换，是因为安装目录里还有
    词典 `dictionary.db`、遗留 `audio/` 等 zip 不带的东西，整目录换会把它们一起挪走。
  - 暂存区优先放在安装根的同级目录（同卷 ⇒ rename 是元数据操作）；普通安装目录不可写时回落临时目录
    走 copy+delete。**macOS 包不允许回落**：整包换 + 跨卷拷贝会删掉正在运行的旧包文件。
- **`Update/UpdateInstaller.cs`**：Windows 已安装副本拉起 Inno 安装器
  `/SILENT /CLOSEAPPLICATIONS /NORESTART /SUPPRESSMSGBOXES`，装完由 Inno 的 `[Run]` 把应用拉回来
  （`scripts/StupidDict.iss` 因此去掉 `skipifsilent`——代价是无人值守静默安装也会启动一次应用）。
- **`Update/UpdateRelaunchHandshake.cs`** + `Program.Main`：新进程带 `--wait-for-exit <pid>` 启动，
  **先等旧进程真的退出再去抢单实例守卫的锁**；不这么做，新实例抢不到锁、给旧实例发个激活请求就自杀，
  用户看到的是「更新完应用不见了」。等待有 20 秒上限，等不到最坏只是多一个实例被守卫挡回去。
- **`Update/UpdateService.cs`**：进程级状态机（Idle/Checking/UpToDate/Available/Downloading/
  ReadyToRestart/Restarting/Failed），检查节流时间戳落 `AppSettings.LastUpdateCheckUtc`，
  启动时解读 journal（成功 → 清备份；失败 → `TryRollback` 自动恢复旧文件并保留备份路径）。
- **`Update/UpdatePromptWindow.axaml(.cs)`**：唯一更新界面。非模态（下载期间还能查词），关窗/收托盘
  即取消下载；失败留在对话框里可重试，安装目录不可写时改为「已下载 + 在文件管理器里打开」。
- **接线**：`App` 创建唯一 `UpdateService`、订阅 `RestartRequested → desktop.Shutdown()`、启动
  `RunStartupUpdateCheckAsync`（30s 延迟 + 7 天节流）；`MainWindow` 持有弹窗（`ShowUpdatePrompt`、
  设置页的「下载并更新」经 `StartUpdateFromSettings` 转交）；`SettingsWindow` 关于页多出
  「立即更新 / 立即重启并更新」两个入口。
- **打包链**：CI 发布 job 为每个资产生成 `.sha256`（`sha256sum` 格式），`scripts/package.sh` 本地
  打包同样为应用 zip 产校验和，`scripts/release.sh` 核对产物时连同 `.sha256` 一起点名。

## 关键取舍

- **启动检查默认开、无开关**：用户点名「启动时检测」，且仓库规矩是「新增设置项原则上禁止、优先自动
  决定」。7 天节流 + 只在有新版本时弹窗，把打扰降到与 tokenmetry 静默周检同一水位。
- **不用横幅用弹窗**：词典应用没有常驻顶栏，用户要的也是「弹窗」；非模态保证下载不阻塞查词。
- **成功提示也弹一次**：「已更新到 vX」只在本进程显示一次，关掉不再出现（`_noticeDismissed`）。
- **自动回滚而不是只保留备份**：tokenmetry 只提示备份路径；本应用是多文件覆盖，半替换的半成品比
  单文件更糟，所以版本对不上时先把备份挪回去，再报告「已恢复到旧版本」。
- **重启走正常生命周期**（`desktop.Shutdown()`，`RestartRequested` 经 `Dispatcher.Post`）：直接
  `Environment.Exit` 会跳过 MCI/播放器停止、窗口尺寸落盘与安装器的文件锁释放。
- **`UpdateService` 不做单例静态**：由 `App` 创建、构造函数注入，测试可用假 handler/假 stager 全链路
  跑通；不注入（`MainWindow` 传 null、`SettingsWindow` updateService=null）时退回旧的「只检查」行为。

## 评估过、不做的

- **接力脚本/计划任务换文件**（Windows `.bat` + 轮询退出、launchd 一次性任务）：每个平台的编码、
  权限与「等进程退出」都能静默炸掉升级；命令行交接标记 + 有界等待已经够用。
- **流式/断点续传更新包**：更新包几十 MB，一次下完最简单；失败就重来，断点续传留给 600 MB 的词典资产。
- **自动重试/指数退避**：按一次点击算一次，失败给按钮；后台自动重试会把「网络不通」变成持续流量。
- **更新通道（stable/beta）/ 指定版本降级**：单通道，`releases/latest` 之外的口子是「打开发布页」。
- **差分更新**（bsdiff 之类）：收益是几十 MB 流量，成本是一套签名与回滚验证，YAGNI。
- **托盘菜单直接开弹窗**：托盘「检查更新」仍落在关于页（结果文案唯一出处），发现新版本时弹窗入口
  就在那里；两处各写一套结果行才是真的重复。
- **更新历史/日志界面**：排查靠 `update-state.json` 与 stderr，不做 UI。
