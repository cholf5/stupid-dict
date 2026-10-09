---
id: B-013
title: 设置枚举未知字符串值导致整个 settings.json 回退默认
type: bug
priority: P2
size: S
status: done
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

settings.json 中单个未知枚举值只该项落默认，其余偏好（主题/语言/窗口尺寸）保留——对齐 AGENTS.md 已记载的「未知枚举值一律回退默认」逐值语义。

## Background

2026-10-09 扫描确认：

- `Settings/AppSettings.cs` + `Settings/SettingsService.cs:28-34`：`JsonStringEnumConverter` 反序列化遇未知枚举名（未来版本新增枚举后降级运行、或手改拼错）抛 `JsonException` → `Load` 整体 catch → `new AppSettings()`，**全部**偏好丢失，而损坏的只是这一个字段。
- AGENTS.md 记载的语义是「缺失/损坏/未知枚举值一律回退默认」——读作逐值回退，现实是整文件回退，代码与文档不符。
- 未定义的**数值**（如 `99`）反而能正常反序列化且 `ApplyTheme` 的 `_ => Default` 有兜底，无需处理。

## Acceptance Criteria

- [x] settings.json 含一个未知枚举名时：该字段落默认，其余字段保留
- [x] JSON 整体损坏仍整文件回退默认（既有语义不变）
- [x] `Load/Save` 的 path 注入测试更新并全绿

## Subtasks

- [x] 逐字段容错解析（自定义 Converter 对未知名落默认继续，或先 JsonDocument 逐字段读再构造）
- [x] 补/改 SettingsService 测试

## Dependencies

- none

## Test Cases

### TC-001: 单字段未知值局部回退

Steps:
1. 写 `{ "Theme": "Sepia", "Language": "SimplifiedChinese", "WindowWidth": 800 }` 到注入路径
2. `Load`

Expected:
- Theme=默认，Language/WindowWidth 保留

### TC-002: 损坏 JSON 整体回退

Steps:
1. 写非法 JSON

Expected:
- 返回 `new AppSettings()`（现状语义）

## Development Log

- 方案取舍（2026-10-09）：选卡面第一方向——自定义 Converter（`JsonConverterFactory` 对任意 enum 生效，未来 `AppSettings` 新增枚举属性自动覆盖，不必记得手工注册）；没选 JsonDocument 逐字段手读：要手工维护属性映射、窗口尺寸三字段的缺省语义得重写一遍，纯重复。
- 修法：新增 `Settings/ForgivingEnumConverter.cs`（Factory + `ForgivingEnumConverter<T>`），`SettingsService.Options` 换挂 Factory。读侧：String token 走 `Enum.TryParse(ignoreCase: true)`（与原 `JsonStringEnumConverter` 大小写语义一致）+ `Enum.IsDefined` 双检——未知名与越界数字串（如 `"9"`：TryParse 按数字成功但非定义名）落 `default`（即 System）继续反序列化；Number token 走 `Enum.ToObject`（undefined 数值如 `99` 保持原行为正常反序列化，下游 `ApplyTheme` 的 `_ => Default` 兜底未动；非整数/超 Int64 落 default）；其余 token 落 default。写侧 `Enum.GetName` 出名，输出与 `JsonStringEnumConverter` 完全一致（`"Theme": "Dark"` 手改友好与文件格式零变化，既有格式钉子测试原样绿）；undefined 数值写数字（AppSettings 实际不可达，防御性）。
- 边界留痕（刻意不做，保持最小改动）：JSON null 赋枚举字段（`"Theme": null`）仍抛 JsonException → 整文件回退，与现状一致，读作结构性损坏而非未知枚举值，未扩 `HandleNull`；类型错位的字段值（`"WindowWidth": "wide"`）同样仍整文件回退——AC 只要求未知枚举名逐字段。
- 测试（SettingsServiceTests 10→13）：新增 `UnknownThemeNameOnlyResetsTheme`（=卡面 TC-001 原样：Theme="Sepia"/Language="SimplifiedChinese"/WindowWidth=800，断言 Theme 落默认、其余保留）、`UnknownLanguageNameOnlyResetsLanguage`（反侧交叉）、`OutOfRangeNumericStringFallsBackPerField`（覆盖 TryParse 成功但 IsDefined false 的独立分支）。红检：修复前 3 条新用例全红（整文件回退把 Language/Window 一并清掉）、修复后连跑 3 轮全绿；既有 10 条原样绿，含 `CorruptFileFallsBackToDefaults`（=TC-002 损坏整体回退语义钉子）与 `SavedFileUsesEnumNamesAndIsHandEditable`（写出格式不变钉子）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Settings/ForgivingEnumConverter.cs`（新增）、`src/StupidDict.App/Settings/SettingsService.cs`、`tests/StupidDict.App.Tests/SettingsServiceTests.cs`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results: 2026-10-09 全套 Core 46/46 + App 195/195 全绿；修复前 3 条新用例红、修复后连跑 3 轮绿；双 TFM（net10.0 / net10.0-windows）构建 0 警告 0 错误。
