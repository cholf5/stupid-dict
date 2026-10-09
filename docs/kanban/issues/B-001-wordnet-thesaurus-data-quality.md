---
id: B-001
title: 修 WordNetThesaurus 四处数据质量缺陷（近/反义词行缺失与词性错乱）
type: bug
priority: P1
size: M
status: done
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

- [x] 短行（7–8 token）不再被丢，index 解析完全由 :182 的精确校验把关（结构下限收为 6 个固定字段；TC-001；真实数据实测见 Verification 的修正①）
- [x] synset 缓存键含 POS（复合键），跨 data.\* 文件不再互相覆盖（TC-002；键 = 文件 POS 命名空间，真实数据 377,592 指针 0 丢失）
- [x] `'s'`（卫星形容词）池输出为 adj. 行（TC-003；真实数据 muggy 抽查吻合）
- [x] WordNet 链路归一化与查询侧一致（小写），专有名词词头有近/反义词行（TC-004；after 库 Rome 有 syn 行）
- [x] 用真实 WordNet 3.0 数据全量重建跑通，修复前后 words/lines 计数对比记录在 Verification（基线与生产 meta 完全一致后 +15,225 行）

## Subtasks

- [x] 移除 `tokens.Length < 9` 预检（保留精确校验）——落地为 6 固定字段的结构下限，见 Dev Log
- [x] synset 字典改 (POS, offset) 复合键
- [x] `InsertionOrder` 加入 `'s'`
- [x] `Normalize`/查找链路统一小写归一化
- [x] 补齐单测夹具（迷你 index/data 文件）并跑通
- [x] 真实数据重建并记录前后计数对比

## Dependencies

- none（P-003 依赖本卡）

## Test Cases

### TC-001: 短行不再丢失

Steps:
1. 临时目录放迷你 `index.noun`，含一行 7 token（p_cnt=0、synset_cnt=1）与对应 `data.noun` 条目
2. 跑 `WordNetThesaurus.Build`

Expected:
- 词条的 syn 行存在（现行代码会丢）

Result: ✅ `ShortIndexLineIsKeptAndGarbageLineSkipped`——修复前失败（行被丢）、修复后通过；同夹具的 3-token 截断残片被结构下限跳过不炸。

### TC-002: 跨文件同 offset 不覆盖

Steps:
1. 夹具 `data.noun` 与 `data.adj` 各含同 offset（如 1740）但词性不同的 synset
2. Build 后断言两词性的池各自正确

Expected:
- 互不覆盖，词性正确

Result: ✅ `SameOffsetInDifferentPosFilesDoesNotClobber`——修复前 animal 拿到 warm 的 synset（bogus adj. 行、无 n. 行），修复后两词性行各自正确。

### TC-003: 卫星形容词输出

Steps:
1. 夹具含 ss_type='s' 的 synset 词条

Expected:
- 产出 kind=syn、pos=adj. 的行

Result: ✅ `SatelliteAdjectiveSynonymsAreWritten`——夹具按真实数据语法（index 侧 pos 记 'a'、data 侧 ss_type='s'、`&` 指针 pos 记 'a'），修复前 's' 池不输出、修复后产出 `syn/adj./humid`。

### TC-004: 专有名词词头有近义词行

Steps:
1. word 表只含小写 `rome`，index.lemma 为 `Rome`

Expected:
- Build 后 Rome 词条（wordId 命中 rome）有近义词行

Result: ✅ `ProperNounLemmasMatchLowercaseHeadwords`——修复前 WordId("Rome") 断链无行，修复后命中。注意：真实 WordNet 3.0 的 index lemma 本就全小写（见 Verification 修正②），本 TC 防护的是归一化规则与查询侧契约，以及 data 侧大写 lemma 池成员。

## Development Log

### 2026-10-09

- 四处修复全部落在 `WordNetThesaurus.cs`：①index 预检改为「6 个固定字段」结构下限（挡截断残片，不再按总长丢短行，偏移齐全仍由精确校验把关）；②synset 缓存键改 (POS, offset) 复合键，**键用文件 POS 命名空间 `file[0]` 而非行内 ss_type**——data.adj 里卫星词 ss_type 是 's'，但 index 与指针记录里形容词一律记 'a'（实测 3.0 全量：377,592 个指针目标按命名空间键 0 丢失；按 ss_type 键会有 10,693 个 `&` 目标 miss）；③`InsertionOrder` 加 's'；④`Normalize` 补 `ToLowerInvariant()`。
- 测试：新增 `tests/StupidDict.Core.Tests/WordNetThesaurusTests.cs`（4 TC，迷你夹具不带真实文件的尾随空格）；DataBuilder csproj 加 `InternalsVisibleTo StupidDict.Core.Tests`、Core.Tests 加 DataBuilder 项目引用。4 个新测试对修复前代码全部失败（咬合确认），修复后 Core 45/45 + App 126/126 全绿（套件数据查询走独立只读连接，`Pooling=false` 遵守 Windows 铁律）。
- 真实数据重建（ECDICT 1.0.28 sqlite release 的 stardict.db，3,402,564 行与生产 meta 的 entries 完全一致 + Princeton WordNet 3.0）：基线 145,674/141,847 与生产 meta 完全一致（同源同码，基线可信）；修复后 145,674/**157,072**（+15,225 行）。两处卡片前提实测修正（见 Verification）。
- 下一步：P-003（重建 dictionary.db 发 data-2）依赖本卡，仍待开。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.DataBuilder/WordNetThesaurus.cs`、`tests/StupidDict.Core.Tests/WordNetThesaurusTests.cs`、`src/StupidDict.DataBuilder/StupidDict.DataBuilder.csproj`、`tests/StupidDict.Core.Tests/StupidDict.Core.Tests.csproj`、`src/StupidDict.Core/Dictionary/DictionaryDatabase.cs`（FindWordId 小写精确比对，未改动）
- How to run/verify: `dotnet test StupidDict.slnx`；真实数据重建 `dotnet run -c Release --project src/StupidDict.DataBuilder -- <stardict.db> <输出.db> --wordnet <WordNet3.0 dict 目录>`
- Results:
  - 测试：Core 45/45（+4 新增）+ App 126/126 全绿；4 个新测试修复前 4 败。
  - 真实数据重建对比（同源：ECDICT 1.0.28 `stardict.db` 3,402,564 行 + WordNet 3.0 dict）：

    | 指标 | 修复前 | 修复后 |
    |---|---|---|
    | thesaurus_words（index lemma 命中 word 表） | 145,674 | 145,674 |
    | thesaurus_lines（syn_group 行） | 141,847 | **157,072**（+15,225，+10.7%） |
    | 有行的去重词头 | 127,590 | **140,197**（+12,607） |
    | 命中但零行的词头 | 18,084 | 5,477 |
    | 原文非全小写词头上的行 | 31,626 | 34,800 |

  - 修复前基线与生产 dictionary.db 的 meta（145674/141847）完全一致，证明对比同源可信。
  - 抽样语义验证：muggy（卫星形容词）行 `steamy, sticky, wet` 与其 data.adj 原始行（co-lemmas steamy/sticky + `&` 指向头形容词 wet）逐成员吻合；Rome 行修复前是裸 offset 污染产物（`national capital, leadership, leaders`，缺实例 lemma），修复后 `roma, eternal city, italian capital, capital of italy, national capital, leadership, leaders`。
  - **修正①（对 Background 第 1 条）**：真实 WordNet 3.0 原始文件每行末尾带两个尾随空格，Split 后最短的 7 字段行恰为 9 token，`<9` 预检实测**零丢弃**（四文件 parsed=total；卡片所记 36%/47%/77% 丢弃率在原始发行件上不成立，应是把行尾空白剥掉后再数的——恰好证明清洗过的输入会整批丢行）。修复保留：规则正确性 + TC-001 钉住。
  - **修正②（对 Background 第 4 条）**：WordNet 3.0 原始 index lemma **全为小写**（四文件大写 lemma 计数为 0），「Adam/Rome 大写断链」在 index 侧不成立（thesaurus_words 前后不变的原因）；该修复的真实影响在 data 侧——data.noun 有 42,998 个大写 lemma token（Rome、Eternal_City 等实例与多词专名），修复前作为池成员 FindWordId 必 miss 被丢（修复前 Rome 行缺 roma/eternal city 等），修复后并入行。归一化与查询侧小写契约的对齐保留为防御性正确。
  - 重建产物：`/tmp/b001/before.db`、`/tmp/b001/after.db`（各 ~575MB，重启即清）。
