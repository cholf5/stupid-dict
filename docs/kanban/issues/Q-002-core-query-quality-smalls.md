---
id: Q-002
title: Core 查询质量三小项（排序一致性 / Words 首访竞态 / CJK 判定区间）
type: chore
priority: P2
size: M
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

统一两路排序的词频规则、消除 Warmup 竞态、补齐 CJK 判定的明显缺口；`dotnet test` Core.Tests 全绿。

## Background

2026-10-09 扫描确认：

1. **内存排序忽略 bnc，与 SQL 路径不一致**——`CommonWordIndex.cs:33-41` 的 `EffectiveRank` 只用 freq（0 → int.MaxValue），bnc 完全不参与；SQL 侧（DictionaryStore.cs:14-15）是 `CASE WHEN freq > 0 THEN freq WHEN bnc > 0 THEN bnc ELSE 999999 END`。后果：ECDICT 中 bnc>0 而 frq=0 的词在 1-2 字母补全与模糊纠正中被排到所有 freq>0 词之后，且两条路径对同一前缀给不同排序。修法：内存侧用同一 CASE 规则。
2. **`Words` 首访竞态**——`CommonWordIndex.cs:16` `Words => _ranked ??= Ranked(_load())`；`WarmupAsync` 与首个按键并发时两线程各自跑 ~57k 行全表扫描 + 排序（良性竞态，内容一致，浪费一次）。修法：`Lazy<T>` 或锁。
3. **CJK 判定缺口**——`DictionaryService.cs:175-176` 只认 `\u3400-\u9FFF` 与 `\uF900-\uFAFF`，漏 U+3007（〇，「二〇二五」常用）与扩展 B+ 区（U+20000+，代理对逐 code unit 检查必然漏）。建库侧 `zhTermRegex`（DataBuilder Program.cs:60）也只索引 3400-9FFF——两侧一致，缺口只影响查询分类路由（含这些字符的查询被当英文走 NotFound 渲染），不产生错误结果。修法评估：至少补 U+3007；扩展区是否纳入需与建库侧一致性一起定夺，决定留痕（含 AGENTS.md 若有变更）。

## Acceptance Criteria

- [ ] 内存索引与 SQL 排序对同一输入给出一致顺序（freq 优先、bnc 兜底）
- [ ] `Words` 首访只算一次
- [ ] CJK 判定缺口按评估结论落地（至少 U+3007），与建库侧关系留痕
- [ ] Core.Tests 全绿

## Subtasks

- [ ] EffectiveRank 对齐 SQL CASE 规则
- [ ] Words 懒加载线程安全化
- [ ] IsChineseQuery 补区间（含评估记录）
- [ ] 三个单测

## Dependencies

- none

## Test Cases

### TC-001: 排序一致

Steps:
1. 构造 freq=0/bnc=1000 与 freq=5000 两条词

Expected:
- 内存索引排序：bnc 词在前（与 SQL 路径一致）

### TC-002: CJK 判定表

Steps:
1. 表驱动断言 U+3007、扩展 B 区字符、常规 CJK、纯拉丁

Expected:
- 按定案结论判定

### TC-003: 首访只算一次

Steps:
1. 并发触发 WarmupAsync 与 Words

Expected:
- 加载计数 == 1（注入计数缝）

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.Core/Dictionary/CommonWordIndex.cs`、`FuzzyMatcher.cs`、`src/StupidDict.Core/Application/DictionaryService.cs`
- How to run/verify: `dotnet test StupidDict.slnx`（Core.Tests）
- Results: 未运行（待修复会话）
