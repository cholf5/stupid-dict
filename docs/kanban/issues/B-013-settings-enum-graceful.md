---
id: B-013
title: 设置枚举未知字符串值导致整个 settings.json 回退默认
type: bug
priority: P2
size: S
status: todo
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

- [ ] settings.json 含一个未知枚举名时：该字段落默认，其余字段保留
- [ ] JSON 整体损坏仍整文件回退默认（既有语义不变）
- [ ] `Load/Save` 的 path 注入测试更新并全绿

## Subtasks

- [ ] 逐字段容错解析（自定义 Converter 对未知名落默认继续，或先 JsonDocument 逐字段读再构造）
- [ ] 补/改 SettingsService 测试

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

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Settings/SettingsService.cs`、`src/StupidDict.App/Settings/AppSettings.cs`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results: 未运行（待修复会话）
