# System.Speech 的 SelectVoice 只认语音名：传文化名（en-US）必抛，Windows TTS 全程静默失效

- **状态**：已修复（选择逻辑抽成纯函数 `SystemTtsPlayer.SelectWindowsVoice`，返回语音**名**；`PlayWindows` 喂 (Name, Culture) 对），回归测试 `SelectWindowsVoice*`（SystemTtsPlayerTests，3 条），红检变异（返回文化名）恰 2 红
- **日期**：2026-10-10
- **背景**：Windows 用户反馈「TTS 单词发音不生效」。不是闪退、不是没语音包——是 TTS 回退路径**从未在任何 Windows 机器上工作过**，100% 复现。

## 症状与根因

播放链 `AudioPackPlayer → SystemTtsPlayer`。发音包未装（或词未覆盖）时落到 Windows 的 `SystemTtsPlayer.PlayWindows`：

```csharp
if (SelectVoice(_synthesizer, accent) is not { } voice) return false;
_synthesizer.SelectVoice(voice);   // ← 必抛 ArgumentException
```

旧 `SelectVoice` 按 `Culture.Name` 匹配，返回的是**区域文化名字符串**（`"en-US"`）；而 `SpeechSynthesizer.SelectVoice(string)` 匹配的是**语音名**（`"Microsoft Zira Desktop"`）。两者永远对不上（除非有语音恰好叫 "en-US"），SAPI 必抛：

```
ArgumentException: Cannot set voice. No matching voice is installed or the voice was disabled.
```

异常被 `PlayWindows` 外层「失败返回 false」的 best-effort catch 吞掉 → `PlayWord` 收到 false → 按钮闪 ✕ + 弹发音包下载条。三重静默叠加（编译期 `#if WINDOWS`、运行期 catch、UI 用 ✕ 表达失败），从外部看就是「点了没声」。

## 取证（探针工程，ProjectReference 引用真实 App 程序集，net10.0-windows）

zh-CN 系统、装有 Microsoft Zira (en-US) 的机器上：

| 探针 | 结果 |
| --- | --- |
| 裸 `SpeechSynthesizer` 按名选 Zira + 同步 `Speak` | ✅ 正常发声（1.7s 说完，与语音时长吻合） |
| `synth.SelectVoice("en-US")`（生产代码当时的做法） | ❌ `ArgumentException: Cannot set voice…` |
| 生产代码 `SystemTtsPlayer.Play(word, British/American)`（修复前） | ❌ 两个口音都返回 `false` |
| 同上（修复后） | ✅ 两个口音都返回 `true` 并入列发音 |

## 修复（`Speech/SystemTtsPlayer.cs`）

1. 选择逻辑抽成纯函数 `internal static string? SelectWindowsVoice(IReadOnlyList<(string Name, string Culture)>, SpeechAccent)`，放在 `#if WINDOWS` **外面**——不碰 SAPI，CI（ubuntu）也能编译和测。返回**语音名**；匹配语义不变（精确文化名优先 → 任何 `en*` 兜底 → null）。
2. `PlayWindows` 负责适配：`GetInstalledVoices()` 取 (Name, Culture) 对喂给纯函数，拿到的名字传给 `SelectVoice(name)`。
3. 测试三条（SystemTtsPlayerTests）：精确匹配返回语音名（zh/en-US/en-GB 混合表，两口音各中）、缺 en-GB 时回退 en-US **且返回的是名字不是文化名**（钉住本次回归）、无英语语音返回 null。

## 为什么原来没被发现

- Windows TTS 路径是 `#if WINDOWS` 包裹，测试项目只覆盖 macOS 侧（`SelectMacVoice`/`ParseVoiceList`/探针缝）；CI 跑在 ubuntu，永远到不了 SAPI。平台专属路径 + 无测试 + 异常被吞 = 出厂即坏也无人知晓。
- 次要叠加条件（非本次根因）：按 AGENTS.md 的 `dotnet run -f net10.0` 在 Windows 本地开发时，`net10.0` TFM 不下 `WINDOWS` 符号，整个 TTS 回退被编译排除，同样静默——Windows 本地跑应用要用 `-f net10.0-windows`。

## 教训

1. **同名不同义是互操作边界的高发坑**：SAPI 的 `SelectVoice(string)` 收语音名，`SelectVoiceByHints(…, CultureInfo)` 才收文化。调不熟的 API 时先用最小探针验证实参语义，别让「看起来对」的类型（都是 string）蒙混过关。
2. **「失败返回 false」的兜底会把确定性 bug 藏成偶发观感**：catch 吞异常是对的（播放路径不该崩），但代价是 100% 必现的故障外部零信号。平台专属且 CI 够不到的路径，要么抽纯逻辑进跨平台测试，要么靠发布前的真机冒烟清单覆盖——本次两者都缺。
3. **探针引用真实程序集（ProjectReference）比重写模仿品可靠**：直接调生产 `SystemTtsPlayer.Play` 一次就分清了「系统 TTS 坏」和「产品代码坏」，省去对整个链路的猜测。
