---
id: B-001
title: 修 WordNetThesaurus 四处数据质量缺陷（近/反义词行缺失与词性错乱）
type: bug
priority: P1
size: M
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

修复 `src/StupidDict.DataBuilder/WordNetThesaurus.cs` 四处确认缺陷，使重建后的 `dictionary.db` 近/反义词行完整且词性正确。

## Background

2026-10-09 全量扫描确认（行号为当时时点），四处相互独立、同文件一起修：

1. **`ReadIndex` 的 `tokens.Length < 9` 预检误杀短行**（:176）。index.\* 行格式 `lemma pos synset_cnt p_cnt [ptr…] sense_cnt tagsense_cnt offsets…`，最少 7 个 token（p_cnt=0、synset_cnt=1 时），后面 :182 的精确校验 `tokens.Length < start + synsetCount` 本来就够。用真实 WordNet 3.0 实测丢弃率：index.noun 36%（42547/117798）、index.adj 47%（10021/21479）、index.adv 77%（70/91），如 `a.k.a. r 1 0 1 0 00270446` 只有 7 token。
2. **四个 data.\* 文件的 synset 共用 `Dictionary<int, Synset>` 按 offset 合并**（:199/:229）。WordNet 的 offset 是**文件内**字节偏移，四文件首条 synset 偏移全部是 `00001740`，后解析的 adj/adv 覆盖 noun/verb；指针目标（`@` 上位、`&` similar、`!` 反义）落在被覆盖偏移时取到别的词性的错误 synset。修法：key 用 (POS, offset) 复合键。
3. **`InsertionOrder = ['n','v','a','r']`（:25）缺 `'s'`**。卫星形容词 synset 的 ss_type 是 `'s'`，池按 `synset.Pos` 写入（:106），插入循环只遍历四个字符，`'s'` 池永远不输出；而 `DisplayPos`（:155）已写 `'a' or 's' => "adj."`，说明本意要处理。WordNet 形容词约一半是卫星词，这些词头一行近义词都出不来。
4. **`Normalize`（:161）不做小写化**。word_lower 全链路小写（建库 DictionaryDatabase.cs:113 `ToLowerInvariant`、查询侧同规则），`FindWordId`（DictionaryDatabase.cs:155）直接 `WHERE word_lower = $lower` 不归一；WordNet 专有名词词头（Adam/Rome）大写 → 断链；:72 同义词过滤 `WordId(word) is null` 同因丢成员（若专有名词是唯一成员则整行被丢）。查询侧 `FindRelatedWords` 先 `ToLowerInvariant()`（DictionaryStore.cs:187）证明其余环节全小写，此处是唯一漏网点。

注意：这四处只影响构建器，但产物是已发布的 data-1 词典数据，缺陷已随数据发出——修完后的数据重建与发 data-2 是独立卡 **P-003**。

## Acceptance Criteria

- [ ] 短行（7–8 token）不再被丢，index 解析完全由 :182 的精确校验把关
- [ ] synset 缓存键含 POS（复合键），跨 data.\* 文件不再互相覆盖
- [ ] `'s'`（卫星形容词）池输出为 adj. 行
- [ ] WordNet 链路归一化与查询侧一致（小写），专有名词词头有近/反义词行
- [ ] 用真实 WordNet 3.0 数据全量重建跑通，修复前后 words/lines 计数对比记录在 Verification

## Subtasks

- [ ] 移除 `tokens.Length < 9` 预检（保留精确校验）
- [ ] synset 字典改 (POS, offset) 复合键
- [ ] `InsertionOrder` 加入 `'s'`
- [ ] `Normalize`/查找链路统一小写归一化
- [ ] 补齐单测夹具（迷你 index/data 文件）并跑通
- [ ] 真实数据重建并记录前后计数对比

## Dependencies

- none（P-003 依赖本卡）

## Test Cases

### TC-001: 短行不再丢失

Steps:
1. 临时目录放迷你 `index.noun`，含一行 7 token（p_cnt=0、synset_cnt=1）与对应 `data.noun` 条目
2. 跑 `WordNetThesaurus.Build`

Expected:
- 词条的 syn 行存在（现行代码会丢）

### TC-002: 跨文件同 offset 不覆盖

Steps:
1. 夹具 `data.noun` 与 `data.adj` 各含同 offset（如 1740）但词性不同的 synset
2. Build 后断言两词性的池各自正确

Expected:
- 互不覆盖，词性正确

### TC-003: 卫星形容词输出

Steps:
1. 夹具含 ss_type='s' 的 synset 词条

Expected:
- 产出 kind=syn、pos=adj. 的行

### TC-004: 专有名词词头有近义词行

Steps:
1. word 表只含小写 `rome`，index.lemma 为 `Rome`

Expected:
- Build 后 Rome 词条（wordId 命中 rome）有近义词行

## Development Log

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.DataBuilder/WordNetThesaurus.cs`、`src/StupidDict.Core/Dictionary/DictionaryDatabase.cs`
- How to run/verify: `dotnet test StupidDict.slnx`；真实数据重建 `dotnet run --project src/StupidDict.DataBuilder -- <ecdict.csv …> --wordnet <WordNet3.0 目录>`
- Results: 未运行（待修复会话）
