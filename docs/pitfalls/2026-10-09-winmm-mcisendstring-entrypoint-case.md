# winmm 的 mciSendString：P/Invoke 方法名大小写写错，点发音按钮直接杀进程

- **状态**：已修复（`EntryPoint = "mciSendStringW"` + `ExactSpelling = true`；`Play`/`Stop` 兜底不再外抛），回归测试 `MciPlayerReportsFailureInsteadOfThrowingWhenTheFileIsMissing`（SpeechInteropTests）
- **日期**：2026-10-09
- **背景**：Windows 用户点结果页的 UK/US 发音按钮，进程立刻闪退（发音包已装，57k 词、uk/us 两份都在 `%APPDATA%\StupidDict\audio`）。不是「点了没反应」而是进程消失。

## 症状与根因

事件查看器的现场记录（最快的取证入口，比复现快）：

```
Application Error 1000: StupidDict.exe, 异常代码 0xe0434352 (CLR 未处理异常), 模块 KERNELBASE.dll
.NET Runtime 1026: The process was terminated due to an unhandled exception.
  System.EntryPointNotFoundException: Unable to find an entry point named 'MciSendString' in DLL 'winmm.dll'.
     at StupidDict.App.Speech.MciAudioFilePlayer.MciSendString(...)
     at StupidDict.App.Speech.MciAudioFilePlayer.Send(String command) in ...\Speech\AudioFilePlayer.cs:line 76
```

`winmm.dll` 只导出带后缀的 `mciSendStringW` / `mciSendStringA`（**没有**无后缀的 `mciSendString`），而 Windows 的 `GetProcAddress` **大小写敏感**。声明写成了 C# 风格的方法名 `MciSendString`，`ExactSpelling` 默认为 false 时运行时的探测顺序是「名字 + 字符集后缀」→「原名」，即 `MciSendStringW` → `MciSendString`，两个都不存在（首字母大写的 `M` 对不上小写的 `m`），于是抛 `EntryPointNotFoundException`。

本机探针（`NativeLibrary.TryGetExport` + 真实调用）：

| 声明 | 结果 |
| --- | --- |
| `MciSendString` + `CharSet.Unicode`（修复前） | ❌ `EntryPointNotFoundException` |
| `mciSendStringW` + `ExactSpelling` | ✅ 解析成功 |
| 导出 `mciSendStringW` / `mciSendStringA` | ✅ 存在 |
| 导出 `mciSendString` / `MciSendStringW` | ❌ 不存在 |

异常为什么直接杀进程：`MciAudioFilePlayer` 是唯一**没有** try/catch 的播放后端（`ProcessPlayer`、`SystemTtsPlayer` 都吞异常返回 false）。异常逃出 `CompositeSpeechPlayer`，冒到 `async void PlayWord` 的按钮回调——`async void` 的异常没有调用者可接，只能上抛到 UI 框架外，CLR 终止进程。发音包已安装时每个词都命中 MCI，所以 UK/US「都闪退」。

## 修复（`Speech/AudioFilePlayer.cs`）

1. **钉死导出名**：`[DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]`，托管方法名保持 C# 风格 `MciSendString`。`ExactSpelling` 顺带关掉运行时的后缀推导，不会再探出别的拼法。
2. **补上兜底**：`Play` 整体 try/catch 返回 false，`Stop` 先清 `_open` 再 close 且自己吞异常。契约回到文档承诺的「任何引擎失败返回 false，交给合成器回退 TTS」，互操作故障不再有机会外抛。
3. **回归测试**：Windows 上真调 `MciAudioFilePlayer.Play(不存在的临时文件)` 断言返回 false（不抛）。用不存在的路径是为了**不出声**：open 必然失败，修复前这条测试会以 `EntryPointNotFoundException` 失败。非 Windows 直接早退，CI（ubuntu）保持绿。

## 排查副产品：MCI 的 mpegvideo 只在 STA 套间可用

第一轮探针在控制台里调 `open … type mpegvideo` 一律返回 **266 = MCIERR_CANNOT_LOAD_DRIVER**，而 `type waveaudio` 返回 0、`mciqtz32.dll` 也能 `LoadLibrary`，一度像是「MPEG 驱动装不上」的第二个产品缺陷。换成 `[STAThread]` 后同一份代码 `rc = 0`，MTA 工作线程仍是 266——`mpegvideo` 底层是 DirectShow（mciqtz32），**要求 STA 套间**。应用 `Program.Main` 是 `[STAThread]`、点击回调跑在 UI 线程，链路本来无恙。

**教训**：复现平台级 API 时先对齐调用方的套间/线程模型，否则测的是探针而不是产品。

## 教训

1. **P/Invoke 的方法名必须与导出名逐字符一致**（Windows 大小写敏感，Unix `dlsym` 同理）。要给导出名起 C# 风格的方法名，就显式写 `EntryPoint`；不要指望「反正会加 A/W 后缀」。
2. **`async void` 事件处理器是未处理异常的放大器**：任何可能抛的调用要么自己兜在「返回值即契约」的边界内，要么在事件处理器这一层兜。此处播放后端本来就以 false 表达失败，漏掉的 try/catch 让一个 API 细节升级成进程终止。
3. **崩溃先查事件查看器**：`Application Error 1000`（异常代码 0xe0434352 = CLR 未处理异常）+ `.NET Runtime 1026`（含完整托管堆栈）比反复手点复现快得多，也直接证明了「闪退」是未处理异常而非原生崩溃。
