# NativeAOT 支持（2026-11-08）

## 背景与目标

发布资产从 CoreCLR 单文件自包含切到 NativeAOT：启动无 JIT 预热（词典应用是高频开关场景），产物更小（app-only zip 实测 ~90MB+ → 17MB）。Avalonia 12 官方支持 NativeAOT（`docs.avaloniaui.net/docs/deployment/native-aot`）；macOS 同 OS 跨架构编译（arm64 宿主出 osx-x64）受官方支持。

用户决策两条：①Windows TTS 回退改 PowerShell 子进程调 SAPI，顺带移除 System.Speech、合并 TFM；②下版发布直接切 AOT（CI workflow_dispatch 干跑验证后随下个版本生效），package.sh 保留 `--no-aot` 逃生门。

## AOT 硬伤与修法

| 硬伤 | 修法 |
|---|---|
| `SettingsService` 反射序列化（IL3050/IL2026 在 `IsAotCompatible` 下 build 即红） | `Settings/AppSettingsJsonContext` source-gen（`WriteIndented` 烘进 context）；类型信息经 `Options.GetTypeInfo` 取 `JsonTypeInfo<AppSettings>` 喂非反射重载——**泛型 `JsonSerializer.Deserialize/Serialize<T>(…, options)` 重载本身带 RUC 注解，就算 options 挂了 resolver 也必炸**，这是本次实证的第一个坑 |
| `ForgivingEnumConverterFactory` 的 `Activator.CreateInstance` + `MakeGenericType` | 工厂删除；`ForgivingEnumConverter<T>` 以闭型泛型显式注册进 `options.Converters`（静态引用即被 AOT 编译）。AppSettings 只有 `AppTheme`/`AppLanguage` 两个枚举，闭型注册不构成维护负担 |
| `TranslationService` 请求体匿名类型 | `TranslationRequestBody`/`TranslationMessage` record（`[JsonPropertyName]` 钉小写名，声明序保持 system→user，wire JSON 逐字节不变）+ `TranslationJsonContext` source-gen；encoder 在 options 拷贝上换 `UnsafeRelaxedJsonEscaping`（红线不变：body 是 application/json 不进 HTML 语境） |
| `Translations.AllPropertyNames` 静态初始化期 `GetProperties` | 显式 `string[]`（143 项，含 `CurrentLanguage`——反射版把它也扫进去了，行为保持一致）；`TranslationStringsTests.AllPropertyNamesMatchesActualProperties` 用反射镜像做奇偶校验，漏登记/残留都会红 |
| `System.Speech`（内建 COM interop，AOT 不支持） | 见下节 |

## Windows TTS：PowerShell 子进程 + TFM 合并

`SystemTtsPlayer.PlayWindows` 镜像 `PlayMac` 形态：`powershell.exe -NoProfile -NonInteractive -Command "Add-Type -AssemblyName System.Speech; …SelectVoice('NAME'); Speak('WORD')"`。要点：

- **同一批 SAPI 语音**（Windows PowerShell 5.1 自带 System.Speech，`pwsh` 不保证在），效果与进程内版本一致；`Speak` 同步，进程退出即播完，`ProcessPlayer.Stop()` 杀进程 = 与 `say` 完全同型的停播语义。
- 语音枚举复用 B-011 机制（懒跑一次、后台线程、读超时整树 Kill、未就绪用默认 voice）：探针输出 `Name|Culture` 行，`ParseWindowsVoiceList` 按**最后一个**管道符切分（culture 不含 `|`，语音名将来带了也不怕）；选择原样复用纯函数 `SelectWindowsVoice`（2026-10-10 的 name-not-culture 教训不动）。表落地后无英文语音 → 返回 false（与旧进程内行为一致）；探针未就绪 → 用默认 voice 发音（B-011 语义）。
- 词转义：PS 单引号字面量，`'` → `''`（词头字符集 `[a-z' -]+`，防御性处理）。
- powershell 定位：`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` 存在性检查优先（PATH 被弄坏不致命），PATH 兜底。
- **代价**：回退路径首响多一次 PowerShell 冷启（~1s）。只有音频包未装/缺词才走到，可接受。
- **TFM 合并**：System.Speech 是 `net10.0-windows` 的唯一解锁物，移除后单 TFM `net10.0`，包/CI 的 `-f` 分支、「忘加 `-f net10.0-windows` 静默丢 TTS」一类坑随之消失。`#if WINDOWS` 全仓清零（AutoStart/ProxyDetector 先例：平台分派用运行时 `OperatingSystem.IsX()`）。

## 产物形态：exe + 原生库随行

AOT 下 `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract` **静默无效**（不报错也不捆库，实测）——发布产物是 exe + `libAvaloniaNative/libSkiaSharp/libe_sqlite3/libHarfBuzzSharp` 随行（托管侧按 exe 同目录探测 dlopen，标准 .NET 发布形态；`otool -L` 只有系统库）。因此：

- package.sh staging 改 `stage_publish_binaries`：拷发布目录全部文件、排除 `.pdb`（macOS 的 dSYM 是目录自然排除）；.app 的 dylib 进 `Contents/MacOS/`，ad-hoc 封签递归覆盖。
- CI windows job：发布 → **ISCC 前删 `*.pdb`**（AOT 原生 pdb 数百 MB，Inno 按 `SourceDir\*` 递归打包会吞进去）→ zip 改收整个发布目录（dll 要进 zip）。
- 体积：osx-arm64 app-only zip **17MB**（CoreCLR 单文件 ~90MB+）；AOT exe 本体 20MB + dylib 19MB，JIT 运行时整段消失。

## 审计门禁事件（本机 SDK 滞后）

`PublishAot` 拉入 `Microsoft.DotNet.ILCompiler`，首个 spike 即被 NuGetAudit 拦死：**GHSA-p3jp-9v83-76w9 / CVE-2026-111497**（.NET Brotli 解码 OOB 写，高危；ILCompiler 原生库捆着 brotli）。修复 = runtime **10.0.14**（2026-10-25 安全补丁，对应 ILCompiler 10.0.14；10.0 线 [10.0.0, 10.0.14) 全中）。CI 的 `10.0.x` 解析到 10.0.403 自带修复，无需任何仓库改动；本机 10.0.101 落后 → **升级本机 SDK 解决，不加 suppress**（有修复可用时压制高危公告违背门禁本意；唯一的 suppress 例外 GHSA-2m69 仍唯一）。本机 SDK 从此需 ≥10.0.114。

## 评估过、不做的

- **WinRT `Windows.Media.SpeechSynthesizer`（CsWinRT）**：进程内无延迟、AOT 安全，但要 TFM 改 `net10.0-windows10.0.19041.0`（保住双 TFM）、WinRT 语音枚举 + SpeechSynthesisStream 写临时 wav 走 MCI，改动面大；对一条回退路径不成比例。选 PowerShell 子进程，同批语音、行为对齐，还能顺带杀掉整个双 TFM。
- **保留 System.Speech**：AOT 产物里 COM 激活抛异常被「失败返回 false」吞掉 → Windows TTS 静默无声——与 2026-10-10 SelectVoice 事件同类，明确拒绝。
- **`InvariantGlobalization=true`**（省 ICU）：语言跟随系统按 `CultureInfo.CurrentUICulture` 两位码解析，invariant 模式下该值退化，破坏既有语义；不做。
- **双轨发布（AOT + CoreCLR 并存）**：资产翻倍、下载页混淆；`--no-aot` 只是本地逃生门，不上 Release。
- **AOT 下静态链 skia/sqlite**：需重编第三方原生库为静态包，远超本卡范围；随行 dylib 是标准形态。

## 验证

- `dotnet build` 零警告（新 SDK 10.0.403）；`dotnet test` Core 83/83 + App 323/323（含 4 条新增：3 条 Windows 语音表解析 + 1 条属性数组奇偶校验）。
- AOT publish osx-arm64 / osx-x64（跨架构）均零告警；`file` 确认 x86_64 产物与随行 dylib 架构正确。
- `scripts/package.sh --rids osx-arm64` 全链路：sign_app 的 `--verify --strict` 通过、zip 布局含 dylib、app-only 17MB。
- CI workflow_dispatch 干跑（windows MSVC / linux clang / osx-x64）待推分支后执行；下个 release 走既有 release.sh 即 AOT 产物。
