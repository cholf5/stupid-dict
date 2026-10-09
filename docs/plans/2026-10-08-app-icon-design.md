# 应用图标接入

日期：2026-10-08（同日换源：藏青细节版 → 宝蓝简洁版）

## 源图更换：小尺寸可识别性

首版源图（藏青底、A 带表情 + 黄色感叹 + 单个 ↔）在任务栏尺寸细节过多，且深藏青
底在深色任务栏里与背景几乎同明度、整块融掉。换用 `stupid-dict-icon-blue.png`
（宝蓝底、更大更粗的 "A ⇄ 中"、上下双箭头）：16/24/32/48px 在深浅两种任务栏底色
上的放大对比里，新版在 16px 仍清晰可读，旧版 16px 只剩深色方块加模糊白痕。
换源只重跑素材生成管线（几何参数不变），代码零改动。

## 背景与目标

项目一直没有应用图标（AGENTS.md 原记录"macOS Dock 图标…本应用没处理"）。本次
提供源图 `~/Downloads/stupid-dict-icon.png`（1024×1024，"A↔中"设计），要求参考
`../inpaint` 的现成方案接入。目标：Windows exe（资源管理器/任务栏）、macOS Dock
（打包 bundle 与裸 `dotnet run` 两种形态）、各平台窗口标题栏都有图标，且三端形状一致。

## 源图的一个前置问题

源图是**全出血方形、RGB 无 alpha**（四角就是底色，首版藏青、现版宝蓝），直接用会在
Dock/任务栏露出方角。inpaint 的结论（`MacDockIcon` 注释）是运行时设置的 Dock 图标不走
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
- **生成脚本**（`scripts/make-icon.py`）：初版按"素材是一次性资产、重生成属极低频
  操作"否掉，只在本档记录参数。实际进入多候选试错循环后结论反转：每次换源都要
  重跑同一条管线、且需要小尺寸深/浅底预览辅助判断，脚本化后试错零成本，还把
  "源图 → 三件套"固化成唯一入口（脚本重跑当前源图，png/ico 逐字节复现；icns 因
  iconset 帧改用 Pillow 缩放与手排 sips 版有帧级差异，语义等价）。
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

## 2026-10-09 重设计：放大镜 + Aa → 呆萌书脸（B v2）

珊瑚红 Aa 版定稿后仍不满意，归因三层：放大镜+Aa 是词典类图标通用模板、无署名权；
"Aa" 是排版/字体类符号，且英汉词典的图标里一个汉字都没有；高饱和红与应用内的纸面
调色板是两个世界。两轮 SVG 候选（母版与深浅底 × 16…512px 对比页全留在
`design/icon-candidates/`，含已淘汰方案）：A 词典书（纸面书封 + 大「词」+ 朱红
书签带）、A′ 朱印极简（大「词」+「傻」字印）、B 呆萌书脸、C 词条式（A / 分隔线 /
中）。C 过于普通首轮淘汰；A / A′ 的「词」居中问题 v2 已修，但 A′ 印文换真篆体
未走完（用户装的篆体英文族名 EBAS＝全字庫說文解字，覆盖度未及验证），用户拍板
「算了，确定用 B」。

**B v2**：暖炭书封 #2A2926（深色 CardBackground）+ 纸色呆脸 #F2F0E7（深色
TextStrong）+ 红书签带 #C4553C，纸色外板留白。深浅任务栏下的识别都由
「暗块 + 亮点 + 红角」承担；纯几何无文字 = 无字体依赖、无转曲步骤，SVG 即唯一
真源。管线：`b-derp-book-v2.svg` → `qlmanage -t -s 1024` 渲 1024 母版 →
`make-icon.py` 烤圆角出三件套；换图标 = 改 SVG → 重渲母版 → 重跑管线。

顺带修了 `make-icon.py` 两个缺陷（本次第一烤翻车暴露）：

- 源图带 alpha 通道时，展平用的是**亮度当蒙版**（`paste(..., im.convert("L"))`），
  深色像素被当成透明冲向白底——qlmanage 渲出的 RGBA 母版整个洗灰（炭黑书封变
  浅灰、红带变粉），而此前各版源图恰好都是 RGB 无 alpha 所以从未触发。改为用
  真实 alpha 通道合成，旧约定「源图 RGB 无 alpha」不再正确性前提。
- 预览表的对照列在 `Assets/app-icon.png` 已被本次候选覆盖**之后**才读盘，永远
  等于候选自身；改为覆盖前快照旧母版传入。旧珊瑚版母版备份在
  `design/icon-candidates/previous-icon-magnifier-aa-1024.png`。

验证：成品中心像素 = #2A2926、四角透明；`WindowsCarryAppIconFromEmbeddedAssets` 绿。

### v3：实装后反馈——内嵌书封圆角与外板不匹配

实装后用户反馈书封的黑色圆角与外板（系统）圆角不匹配、看着别扭。根源是嵌套圆角
未按同心规则（**内圆角 = 外圆角 − 内缩量**）：外板 239.4（194 ÷ 0.8105）− 白边
100 = 应得 ≈139，实际 rx 48——内角比该有的紧了近三倍，两条曲线不平行。v3 修正：
书封 inset 统一为 100（824×824 正方居中，v2 是 784×824、左右 120 上下 100 不均匀，
也贡献了别扭感）、rx 139；脸、丝带、配色零改动。

评估过用户提议的「去掉一圈白边」：书封直接成为底板确实根治不匹配，但深色图标在
深色任务栏/菜单栏会失去轮廓——纸色外板正是深底可见性的来源（v1 藏青融底同款
教训），否。回退路径：`python3 scripts/make-icon.py
design/icon-candidates/b-derp-book-v2-master.png`（v2 母版与 v3 母版并存于
`design/icon-candidates/`）。验证：v3 成品中心像素不变、四角透明，回归测试绿；
本次喂给管线的就是 qlmanage 渲出的 RGBA 母版，上一节的 alpha 修复由此得到
端到端确认。

## 2026-10-09 Windows 任务栏：ico 满幅出图（png/icns 不动）

用户反馈 Windows 任务栏/开始菜单里图标比欧路词典这类满幅邻居小一圈（同屏对比
截图），macOS 无此问题。归因：三件套此前共用 Apple 网格几何（1024 画布 + 830
内容块 + 四边 97px 透明边距），而 Windows 对 exe/任务栏图标**按 tile 原生尺寸
原样渲染、无系统蒙版、不缩放**——边距烤进素材就整幅缩水。实测旧 ico 256 帧
alpha bbox (22,22,234,234)：内容只占画布 82.8%，同 tile 下线性尺寸是满幅邻居
的 81%（面积 66%）。macOS 图标语义本来就是留边（Dock/bundle 走 HIG 网格观感），
与用户「Mac 没问题」一致，故只动 ico。

**做法**：`make-icon.py` 一次跑两套几何——`styled_icon(source, content, radius)`
照旧出 png/icns（Apple 网格），再以 `styled_icon(source, CANVAS, 239)` 出满幅
ico（内容撑满 1024，圆角同比例 194/830 → 239/1024，不另造几何）；新增自检
断言（满幅 alpha bbox 必须 (0,0,1024,1024)、角部透明），预览候选列换成满幅
ico（任务栏真实所见）。

**回归测试** `WindowsTaskbarIconArtworkIsFullBleed`：手解 ICO 目录取 256 帧
PNG，经 Avalonia.Skia 传递引用的 SKBitmap 采样 alpha——(0,0)=0（圆角烤在
素材）、(128,128)=255、(3,128)=255（左边缘中点，旧边距 ico 此处为 0，判别
点）、(252,128)=255。自取帧不依赖 Skia 的 ICO codec 选帧行为，Pillow 换帧
编码格式（PNG↔BMP）也不受影响。

**评估过**：

- 三端全满幅：否——macOS Dock/bundle 图标系统语义就是带边距，满幅在 Dock 里
  反而比邻居大一圈；用户明说 Mac 没问题。
- ico 折中留小边（如 content 940）：否——「比别家小」的观感来自边距本身，
  小边只是小半圈，仍然别扭。
- 圆角按 Windows 惯用比例（~15%）重烤：否——改比例即改形状；同一形状放大
  撑满才是「把 Icon 撑满」的本意，23.4% 圆角在任务栏尺寸下与邻居观感一致。
- 与上节否掉的「去掉白边」不冲突：那是去书封**内部**的纸色外板（深底可见性
  来源），本次去的是**画布级透明边距**，纸色外板完整保留并扩到画布边缘。

**验证**：重跑 `make-icon.py design/icon-candidates/b-derp-book-v3-master.png`
后 png/icns 逐字节不变（sha256 同前），仅 ico 更新；7 帧（16…256）全部满幅；
`WindowsCarryAppIconFromEmbeddedAssets` + 新测试绿，全量测试绿。

完整症状、量测数据与排查教训见
`docs/pitfalls/2026-10-09-windows-taskbar-ico-full-bleed.md`。
