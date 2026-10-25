# Avalonia 11.3.22 → 12.1.4 升级设计与实施（2026-10-20）

## 背景

用户报告：Windows 上搜索框**中文输入法英文模式下每个按键（含 Backspace）卡一下**，切纯英文键盘后完全消失；记事本里同输入法不卡。定位过程（全部有实测数据）：

- 应用托管侧每键成本：真实窗口 + 真实 604MB 词典自动打字探针（注入 TextInput 事件），打字/Backspace/慢打/下拉更新帧全部 ≤2.1ms；下拉面板"从无到有"翻面 11-16ms；补全查询后台 0.1-24ms；Enter 结果页渲染 11-70ms（唯一真实卡点，但在查词时、非打字时）。
- 唯一探针覆盖不到的层：**真实 NSEvent/Win32 键事件 → IME → Avalonia 的互操作通道**（探针注入事件恰好绕过）。用户切走 IME 后卡顿消失，把根因钉死在这一层。
- Avalonia 上游无同款 issue（搜 ime slow/lag/pinyin/TSF 等）；11.3.22 已是 11.3 系列末版；12.x 无点名的 Windows IME 性能修复，但有输入/文本性能改动（caret 测量移出 TextLayout getter 等），属"值得赌"的升级。

结论：修复手段只剩整版升级。用户拍板跳过进一步 A/B 验证直接升。

## 做了什么

1. `Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` / `Avalonia.Headless.XUnit` / `Avalonia.Skia` 11.3.22 → **12.1.4**。
2. API 适配（警告即错误，全部源头修）：
   - `TextBox.Watermark` → `PlaceholderText`（MainWindow.axaml ×1、SettingsWindow.axaml ×2）。
   - `Bitmap.Save(string, int?)` 过时 → `Save(string, PngBitmapEncoderOptions)`（测试 SaveScreenshot）。
3. 测试工程 **xunit 2.9.2 → xunit.v3 3.2.2**（Avalonia 12 的 Headless.XUnit 绑 v3，双引用直接 CS0433）；runner → 3.1.5。v3 适配：xUnit1051 全部按其要求改为传 `TestContext.Current.CancellationToken`（`File.WriteAllBytesAsync`、`Task.Delay`、`TranslateAsync` 的 `default`、`CheckAsync`、`ConvertZipToDatabase`、`ImportAudioPack` 共 35 处）。
4. 验证：全套 **Core 83/83 + App 309/309 绿**（一次通过，11.x 行为钉子全部在 12.x 成立）；macOS 真实窗口探针在 12.1.4 下重跑，打字/Backspace/下拉链路正常、成本持平；NuGetAudit 门禁通过。
5. Windows 测试包 `dist/StupidDict-win-x64-av12.1.4-test.zip`（net10.0-windows、自包含单文件）供用户在卡顿机器上实测；随下个正式版发布。

## 评估过 / 未做

- **A/B 探针（裸 TextBox vs 真 App）**：已打包但用户选择跳过——无论 A/B 哪个结果，升级都是唯一修复路径，省一步。
- **应用侧规避**：不存在干净方案。搜索框必须留 IME（中文查词走 zh_index），"关 IME"不成立；`TextInputOptions` 无 IME 开关。
- **上游 issue**：若升级后 Windows 仍卡再报（届时可附本包探针方法与数据）。
- AGENTS.md 各坑位记录的「Avalonia 11.3」表述保留为历史事实，修法是否在 12.x 仍必要由测试说话（现全部仍通过）。

## 遗留与风险

- **Windows 实测待确认**：升级动机是那台 Windows 机器的 IME 卡顿，最终效果必须由用户在原机器验证；macOS 这边无感（本就测不到该层）。
- 12.x 为 major 升级，UI 细节差异可能漏网——已知的 11.x 坑全部有测试钉住，未钉住的渲染差异靠人工目检 + 后续反馈。
