---
id: Q-002
title: Core 查询质量三小项（排序一致性 / Words 首访竞态 / CJK 判定区间）
type: chore
priority: P2
size: M
status: done
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

- [x] 内存索引与 SQL 排序对同一输入给出一致顺序（freq 优先、bnc 兜底）
- [x] `Words` 首访只算一次
- [x] CJK 判定缺口按评估结论落地（至少 U+3007），与建库侧关系留痕
- [x] Core.Tests 全绿

## Subtasks

- [x] EffectiveRank 对齐 SQL CASE 规则
- [x] Words 懒加载线程安全化
- [x] IsChineseQuery 补区间（含评估记录）
- [x] 三个单测

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

### 2026-10-09 修复会话

1. **排序一致**——词频规则收敛到唯一实现：`CommonWord` 增加 `Bnc` 字段与 `Commonality` 计算属性（逐字镜像 `DictionaryStore.CommonalitySql` 的 CASE：freq>0 → freq，否则 bnc>0 → bnc，否则 999999），`GetCommonWords` 改为 SELECT freq,bnc；`CommonWordIndex.Ranked` 与 `FuzzyMatcher` 各自的 `EffectiveRank`（freq-only、0→int.MaxValue）删除，统一改读 `Commonality`。两条路径对同一前缀不再分叉——bnc-only 词（ECDICT 中 bnc>0 而 frq=0）此前在 1–2 字母补全与模糊纠正里被排到所有 freq>0 词之后。 ELSE 取 999999 而非 int.MaxValue 纯为镜像 SQL：真实加载路径 `WHERE freq > 0 OR bnc > 0` 使 ELSE 分支实际不可达。
2. **Words 首访竞态**——`CommonWordIndex` 的 `_ranked ??=` 改 `Lazy<>`（默认 ExecutionAndPublication）：WarmupAsync 与首按键并发时全表扫描只跑一次，输家等赢家而非各自扫一遍。行为差异留痕：Lazy 会缓存 load 抛出的异常（旧 `??=` 每次访问重试）——load 唯一失败面是 SQLite 打不开，届时 DictionaryService 本就要整体重建（下载完成后热重建），无实质影响。
3. **CJK 判定**——`IsChineseQuery` 补 U+3007（〇）与扩展 B 起的全部 CJK 统一表意文字扩展区块（区间逐条对官方 Blocks.txt latest / 2026-07-08 核对）：Ext B 20000–2A6DF、C 2A700–2B73F、D 2B740–2B81F、E 2B820–2CEAF、F 2CEB0–2EBEF、I 2EBF0–2EE5F、兼容表意补充 2F800–2FA1F、G 30000–3134F、H 31350–323AF、J 323B0–3347F（代码里 F+I、G+H 各并作一条相邻区间，J 单列）；判定改 `EnumerateRunes` 逐码点（代理对按整字判定，孤立代理项解码为 U+FFFD 不会误判）。**刻意不加**：CJK 标点/符号/部首/全角块（3000–303F 其余、2E80–2EFF、FF00–FFEF 等）与 **Seal 块（3D000–3FC3F，script=Seal 非 Han，18.0 新增）**——不是 CJK 统一表意文字，出现在查询里不应改变路由，点名留痕防后人当缺口重提。**与建库侧关系（定夺留痕）**：建库侧 `zhTermRegex`（DataBuilder Program.cs:60）维持 `[\u3400-\u9FFF]+` 不动；IsChineseQuery 本就是建库侧词类的超集（F900–FAFF 兼容字是既有先例），扩展区查询改判中文后落到中文 NotFound 渲染——分类更诚实，结果正确性不变（zh_index 本就不含这类词）。不动建库侧的理由：数据资产与 App 版本解耦（data-1 已发），改 regex 只在下一次重建 dictionary.db 时生效，而 ECDICT 译文含扩展区字符近乎为零。**遗留建议**：下次数据重建时把 zhTermRegex 扩为 `[\u3007\u3400-\u9FFF]+`，含 〇 的译文名词（如「二〇二五」）才能真正进 zh_index 可查。AGENTS.md 无需变更（该文件未记录具体区间）。
4. **红检实证**——三处修法各做一次红检：① `Commonality` 临时退回 freq-only → 3 条排序测试全红；② `IsChineseQuery` 临时退回旧实现 → 理论用例中 8 红（恰为 〇/扩展区用例；「二〇二五」「猫」旧实现本就为真——串里含 URO 常规字）；③ `CommonWordIndex` 临时退回 `??=` → 并发测试红（loads>1；loader 内置 1ms 延迟提供判别窗口，Lazy 下通过与否不依赖时序），恢复后绿。红检临时改动均当场还原。

### 2026-10-09 审查修正（REQUEST_CHANGES，两条必须修 + 建议全收）

1. **Ext I 上界勘误**——首修凭 15.1 时代的单字记忆把 Ext I 写成 2EBF0–2EBFF；官方 Blocks.txt（latest，2026-07-08）实为 **2EBF0–2EE5F**（已分配 Han 至 2EE5D 共 622 字），2EC00–2EE5D 段 606 字被漏。上界改 `0x2EE5F`，代码注释与上条区块列表一并改对。红检：退回旧上界 → `\U0002EC00` 用例红。
2. **Ext J 补块**——Blocks.txt 18.0 新增 CJK Unified Ideographs Extension J = **323B0–3347F**（已分配 Han 323B0–33479 共 4,298 字），与已覆盖的 Ext H 上界 323AF 相邻，首修整块漏掉。补 `>= 0x323B0 and <= 0x3347F` 一行。红检：去掉该行 → `\U000323B0` 用例红（两用例恰好 2 红/18 绿，判别力精确）。
3. **建议项一并落地**：表驱动补 `\U0002EC00`（Ext I 尾段，钉住上界错误）、`\U000323B0`（Ext J 块首）两条判别用例（理论 20 条；`\U000323AF` 既有端点用例保留）；卡面点名 Seal 块刻意排除（见上条）；代码注释补「3400–9FFF 顺带含易经卦画符号 4DC0–4DFF（as before）」；TC-003 loader 内置 1ms 延迟，重验 `??=` 回退下直接红（无需临时改测试）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.Core/Dictionary/CommonWordIndex.cs`、`FuzzyMatcher.cs`、`CommonWord.cs`、`DictionaryStore.cs`、`src/StupidDict.Core/Application/DictionaryService.cs`、`tests/StupidDict.Core.Tests/`（`CommonWordIndexTests.cs`、`FuzzyMatcherTests.cs`、`DictionaryServiceTests.cs`、`TestDatabase.cs`）
- How to run/verify: `dotnet test StupidDict.slnx`（Core.Tests）
- Results: 2026-10-09 本机 macOS 全套绿（含审查修正终跑）——Core.Tests 72/72（基线 46 + 新增 26），App.Tests 195/195（无改动，确认 IsChineseQuery 扩区间不破坏 UI 侧行为）；`TreatWarningsAsErrors` 下零警告。
