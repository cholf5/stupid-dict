---
id: P-003
title: 修复 WordNetThesaurus 后重建 dictionary.db 并发 data-2
type: release
priority: P2
size: M
status: todo
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

B-001 修复构建器后，重建词典数据并发 `data-2`，让用户侧近/反义词缺陷随数据更新消除。

## Background

- B-001 修的是 `src/StupidDict.DataBuilder`，但**已发布的 data-1 数据带着四处缺陷**（大量词缺近/反义词行、部分 synset 词性错乱、卫星形容词与专有名词断链）——缺陷已随 `dictionary.zip` 发出，必须走数据更新链。
- 数据与 App 版本解耦（AGENTS.md/README）：`dictionary.zip`+`.sha256` 放独立 prerelease `data-2`；应用端 `ReleaseAssets.DataTag` 常量（现 `data-1`，ReleaseAssets.cs:14）bump 为 `data-2`，随下个 App 版生效。跨会话旧 zip 校验不过 → purge → 重下的自愈路径已有（`CrossSessionStaleZipFailsChecksumAndIsPurgedAndReplaced` 钉住时序）。

## Acceptance Criteria

- [x] 用修复后 DataBuilder 全量重建 `dictionary.zip` + `.sha256`
- [x] 抽样验证四类缺陷消除：短行词（如 a.k.a. 类）、卫星形容词、专有名词（Rome）、跨词性串行不再出现
- [x] `gh release create data-2 … --prerelease`（流程照 README「打包与发布」）
- [x] `ReleaseAssets.DataTag` bump `data-2`，随下个 App 版发版（提交按会话指示留给 PM）
- [x] App 端从 data-1 升级路径冒烟（校验不过→purge→重下→装上）

## Subtasks

- [x] 重建数据并核对产物
- [x] 抽样验收四类缺陷
- [x] 发 data-2 prerelease
- [x] DataTag bump + 提交（bump 已落地；git commit 按 P-003 会话指示由 PM 处理）

## Dependencies

- B-001（构建器修复必须先落地）

## Test Cases

### TC-001: 升级自愈链

Steps:
1. 本地注入 data-1 时代的旧 zip（跨会话语义）后触发下载

Expected:
- 校验不过 → purge → 重下 data-2 → 装上

Result: ✅ 一次性 headless harness（`/tmp/p003/smoke-harness`，console + Avalonia.Headless，用完即删不进仓库）走真实生产链：预置 148 字节内容错误的旧 zip 到下载目录 + 预建 audio/uk 跳过发音包（`AudioPackInstalled()==true`），`MainWindow` 构造（downloader 缺省 = 真实 `AssetDownloadService`，DataTag 已指向 data-2）自动下载。事件时序（FileSystemWatcher，带毫秒戳）：旧 zip `DELETED`@22:02:26.654（purge）先于 `.part` `CREATED`@22:02:27.317（重下起点）——174MB 经生产下载链 ~4s 完成，`Verifying…→Extracting…`，`dictionary.db` 落位 604,295,168 B 且 meta 与 data-2 产物逐项一致（entries=3402564、thesaurus_words=145674、**thesaurus_lines=160654**——data-1 是 141847，该值即 data-2 字节签名；built_at=2026-10-09T21:50:58Z）；下载目录终态空（无 .part、成功后 zip 已删）。方法学备注：首跑 FAIL 是 harness 判别式缺陷非应用缺陷——watcher 启动时对已存在文件会合成一条 `CREATED`（FSEvents 特性），「任意 CREATED 先于 DELETED」误报；判别式改为「`.part` 的 CREATED 晚于旧 zip 的 DELETED」（只有下载器写 .part）后重跑 PASS。

## Development Log

### 2026-10-09 重建会话

1. **zhTermRegex 扩 〇（Q-002 遗留建议执行）**——`DataBuilder/Program.cs` 的 zhTermRegex 从 `[\u3400-\u9FFF]+` 扩为 `[\u3007\u3400-\u9FFF]+`（带注释：刻意不含扩展平面）。新增测试 `ChineseTranslationContainingCircleZeroEntersZhIndex`（BuilderCliTests；fixture 扩 Frq 重载——zh_index 只索引常用词，原夹具硬编码 frq=0 触不到该链路）。红检闭环：退回旧 regex 恰 1 红（二〇二五零匹配）、恢复绿。**重建实证**：ECDICT 1.0.28 全表译文含 〇 的行为 **0**（python instr + 精确 hex E38087 双验，比 Q-002 卡「近乎为零」更绝对），故 data-2 的 zh_index 计数不变（197,349）——本项是能力就绪（源数据未来更新即生效），非当前数据变化，留痕防止误判「扩了没生效」。
2. **数据源钉死与下载**（全部 /tmp/p003/sources/，不触真实用户数据）：
   - ECDICT 1.0.28 `ecdict-sqlite-28.zip`（216,765,132 B，sha256 `ea01f76a…`）→ stardict.db **3,402,564 行 = data-1 meta entries**，同源钉死；
   - WordNet 3.0：官方 wordnetcode.princeton.edu 已 404（WNdb-3.0.tar.gz 与 wn3.0.dict.tar.gz 均为 HTML 错误页），改用 nltk_data 镜像 `nltk/nltk_data@gh-pages packages/corpora/wordnet.zip`（10,775,600 B，sha256 `cbda5ea6…`，文件日期 2012-06-11 = 3.0 拷贝）。同源证据：data.noun 15,300,280 / index.noun 4,786,655 B 与官方 3.0 Unix 发行件字节数一致；index.noun 117,827−29 头行 = 117,798、index.adj 21,508−29 = 21,479 与 B-001 卡记录吻合；全行带两个尾随空格（B-001 记录的 3.0 原始特征，index.noun 117,827/117,827、data.noun 82,144/82,144）；
   - CMUdict 0.7b：`Alexir/CMUdict` 的 `cmudict-0.7b`（3,865,710 B，sha256 `fd1a4a40…`，134,429 行含 56 注释行，头注 `$HeadURL: …/branches/cmudict/cmudict-0.7b` 为官方 SVN 分支原件；cmusphinx 主干已重命名为 cmudict.dict 不可用）。
3. **重建抽样抓出 B-001 修复②的残留缺陷（本卡最重要的发现）**——首次重建 meta 全部命中 B-001 预期（157,072 行），但四类抽样中的跨词性验证失败：`bad` 的 adv. 行是 `thriftily`（应为 badly）、`frontal` 混入 `sociolinguistically`、a.k.a.（index.adv 7-token 短行）零行（注：data-1 生产库反而**有** a.k.a. 的行——裸 offset 键时代其 offset 270446 四文件间恰好不碰撞、侥幸命中；file[0] 修复把它丢了，见第 4 条 ③），且 data-1 同样带 bad/frontal 的串扰。根因：B-001 把 synset 缓存键的「文件 POS 命名空间」实现为 `file[0]`——`"adv"[0]` 是 **'a'** 与 `"adj"[0]` 撞车（代码注释声称 adv→'r'，与实现不符；真实 3.0 数据 adj∩adv 同 offset **21 处**）。后果双向：data.adv 覆盖 data.adj 同 offset 的 synset（bad 的一个 adj synset 被顶成 thriftily 的 adv synset），且 `('r', …)` 指针与 index.adv 目标全部 miss（4,478 个 adv 词条几乎零行——修复前 45 行全是串扰产物）。原 TC-002 夹具只测 noun/adj（'n'≠'a' 天然不撞），恰好漏掉 adj/adv 对。修复：`PosFiles` 改 `(string File, char Pos)[]` 显式 POS 字母（noun→n/verb→v/adj→a/adv→r），ReadIndex/ReadSynsets 解构取值；新增回归测试 `SameOffsetInAdjAndAdvFilesDoesNotClobber`（夹具含 index.adv 7-token 短行 + data.adv 同 offset，双断言：warm 的 adj. 行、bad 的 adv. 行=badly）。红检：仅把 `("adv",'r')` 改回 `("adv",'a')` 恰 1 红、恢复绿。顺带修了红检脚本插入破坏括号配平的低级伤（当场修复并复绿）。
4. **修正后重建（最终产物）**：`dotnet run -c Release --project src/StupidDict.DataBuilder -- /tmp/p003/sources/ecdict/stardict.db /tmp/p003/dictionary.db --wordnet /tmp/p003/sources/wordnet --cmudict /tmp/p003/sources/cmudict-0.7b`（macBook 本机 41s；输出显式指 /tmp，Q-003 的临时文件+原子进位语义覆盖失败路径）。meta 与派生指标见 Verification 对比表。四类抽样全部通过：①muggy = `steamy, sticky, wet`；②Rome = `roma, eternal city, italian capital, capital of italy, national capital, leadership, leaders`（与 B-001 卡逐成员一致）；③a.k.a.（7-token 短行）= `alias, also known as`——**data-1 生产库本来就有这行**（独立审查下载 data-1 官方 dictionary.zip 逐字节核实）：裸 offset 键时代 a.k.a. 的 offset 270446 四文件间恰好不碰撞、侥幸命中；B-001 file[0] 修复后反而丢失（首次重建零行）；本卡修复后恢复，且从「侥幸」变「必然」（短行解析 + ('r',…) 命名空间命中都是确定性规则）；④跨词性：bad adv. 行 = `badly`（data-1 是 `thriftily, badly`、首次重建是 `thriftily`），且被 data.adv 顶掉的 adj synset 成员（tough、uncomfortable）回归，frontal 串扰词消失、卫星行 `anterior, head-on, front` 成立。
5. **打包**：`scripts/package.sh --rids "osx-arm64" --dictionary /tmp/p003/dictionary.db --audio-pack dist/audio-pack.zip`——产出 dist/dictionary.zip（182,890,568 B）+ .sha256，zip 内 dictionary.db meta 与重建产物逐项一致、unzip -t 全过。**注意坑**：`--audio-pack` 传 `dist/audio-pack.zip` 时源与目标相同，BSD cp 报 "identical (not copied)" 非零退出，`set -e` 中断脚本（发生在全部产物落盘之后，dist 完整；复跑应省略该参数或先拷走）。audio-pack.zip 保持 data-1 字节、.sha256 与字节匹配（`shasum -c` 过）。
6. **发布 data-2**：`gh release create data-2 dist/dictionary.zip dist/dictionary.zip.sha256 dist/audio-pack.zip dist/audio-pack.zip.sha256 --prerelease`（正文照 data-1 格式 + 「相对 data-1 的变更」节）。发布后核验：四资产齐、prerelease=true（不参与 releases/latest）、上传的 .sha256 内容与本地字节 sha256 一致（dictionary.zip `da0a2ce6…`、audio-pack.zip `6e17c72c…`）。
7. **DataTag bump（执行顺序调整：提前到冒烟前）**——`ReleaseAssets.DataTag` "data-1" → "data-2"（唯一改动行）。顺序调整理由：生产下载链的 URL 由该常量拼出，冒烟 harness 不注入 downloader，只有 bump 后才能从 data-2 真实下载；data-1→data-2 的升级语义由「预置 data-1 时代旧 zip + 远端 data-2 校验」保持。tag 字面量清理：`ProxyDetectorTests.BuildAttempts…` 的输入 URL 改为 `ReleaseAssets.GithubUrl(ReleaseAssets.DictionaryAsset)` 生产构造，不再依赖具体 tag 值。全套测试绿（Core 83/83 + App 195/195）。**审查补正**：本条初版称「grep 全仓：tests 里唯一的 tag 字面量…」与事实不符——当时的 sweep 只扫了 src/tests，漏掉 `scripts/release.sh:148` 的功能性字面量 `DATA_TAG=data-1`（驱动发版尾部的数据 Release 存在性警告，上一行注释明写 keep in sync with ReleaseAssets.DataTag；bump 后它仍查 data-1，守卫查错对象比没有守卫更有害）。独立审查抓出后补改：release.sh `DATA_TAG=data-2` + :6 头注释对齐 + `.github/workflows/dotnet-desktop.yml` 两处纯文档注释对齐（bash -n 通过）。教训：tag 字面量的 sweep 范围必须含 scripts/ 与 .github/，不只 src/tests。
8. **TC-001 冒烟**——一次性 harness（/tmp/p003/smoke-harness，console + Avalonia.Headless 与 TestAppBuilder 同参，ProjectReference App 程序集；不进仓库）走真实链：预置旧 zip → `MainWindow` 自动下载 → FileSystemWatcher 钉 purge 时序 → db 落位 meta 断言 → 下载目录终态清净。首跑因判别式把 watcher 对已存在旧 zip 的合成 `CREATED`（FSEvents 特性）误当下载起点而 FAIL，判别式改用 `.part` 的 CREATED 后重跑 PASS（终跑时间线见 TC-001 Result）。过程中 593MB 发音包由预建 `audio/uk` 目录跳过（`AudioPackInstalled()==true`），与本卡无关。

### 2026-10-09 审查修正（REQUEST_CHANGES，两必修全落实；data-2 资产经字节级复核无误、无需重建重传）

1. **release.sh DATA_TAG 失同步（必修 1）**——初版会话的 tag 字面量 sweep 只扫 src/tests，漏掉 `scripts/release.sh:148` 的功能性 `DATA_TAG=data-1`（上一行注释明写 keep in sync with ReleaseAssets.DataTag；驱动发版尾部数据 Release 存在性警告，bump 后仍查 data-1——存在，故警告静默通过，守卫查错对象比没有守卫更有害）。补改：`DATA_TAG=data-2` + :6 头注释 `(data-1, …)` → `(data-2, …)` + `.github/workflows/dotnet-desktop.yml` 两处纯文档注释对齐；`bash -n` 通过；第 7 条的 sweep 表述已更正并留教训（sweep 范围必须含 scripts/ 与 .github/）。
2. **「a.k.a. data-1 零行」记录错误（必修 2）**——审查员下载 data-1 官方 dictionary.zip 逐字节核实：生产库中 a.k.a. **有行**（`syn|adv.|alias, also known as`）。data-1 建于裸 offset 键时代，a.k.a. 的 offset 270446 四文件间恰好不碰撞、侥幸命中；即 data-1 有行（侥幸）→ B-001 file[0] 修复后丢失（首次重建零行）→ 本卡修复后恢复且语义确定。第 4 条 ③ 与 Verification 对应处已更正（B-001 补记里的「a.k.a. 零行」是首次重建语境、本身正确，未动）。
3. **kind/pos 基线标注（建议落实）**——Verification 的 kind/pos 行起点 45/0 是首次重建（B-001 原码）而非 data-1；data-1 生产实测 syn|adv.=2,779、ant|adv.=917、syn|adj.=5,049 等（合计 141,847，本机 data-1 副本复算吻合）。data-2 syn|adv. 2,689 略低于 data-1 的 2,779 是预期非回归：data-1 的 adv. 行含 **115 行**词头不在 index.adv 的纯串扰产物（本机实测，审查员估 50–90 偏保守），data-2 全为真实 adv 行。
4. **README 时态（建议落实）**——「数据资产与 App 版本解耦」段落对齐现状（现为 data-2、下次 data-3），发布示例命令同步为 data-3。本轮全部为文字/脚本注释级改动，零 C# 变更，未重跑 dotnet test（审查指示）。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| P-003-B1 | high | B-001 修复②的复合键用 `file[0]`，"adv"[0]='a' 与 "adj" 撞车：data.adv 覆盖 data.adj 同 offset synset（21 处）、`('r',…)` 目标全 miss，4,478 个 adv 词条近/反义词行缺失或串扰（bad→thriftily、frontal→sociolinguistically） | Closed | PosFiles 改显式 POS 字母映射（adv→'r'）+ 回归测试 `SameOffsetInAdjAndAdvFilesDoesNotClobber` + 红检闭环 |

## Verification

- Related files: `src/StupidDict.DataBuilder/Program.cs`（zhTermRegex）、`src/StupidDict.DataBuilder/WordNetThesaurus.cs`（PosFiles 命名空间修复）、`src/StupidDict.App/Assets/ReleaseAssets.cs`（DataTag）、`scripts/release.sh`（DATA_TAG 同步 bump，审查修正）、`README.md`（数据发布段时态对齐）、`.github/workflows/dotnet-desktop.yml`（两处注释对齐）、`tests/StupidDict.Core.Tests/BuilderCliTests.cs`、`tests/StupidDict.Core.Tests/WordNetThesaurusTests.cs`、`tests/StupidDict.App.Tests/ProxyDetectorTests.cs`、`docs/kanban/issues/B-001-wordnet-thesaurus-data-quality.md`（补记）
- How to run/verify: `dotnet test StupidDict.slnx`；重建命令见 Development Log 第 4 条；发布核验 `gh release view data-2`
- Results:
  - 测试全程：起点 Core 81/81 + App 195/195 → zhTermRegex 后 82/82 → 命名空间修复后 **Core 83/83** → DataTag bump + 测试改动后 **Core 83/83 + App 195/195 全绿**（两次全套实跑）。
  - 重建 meta 对比（同源 ECDICT 1.0.28 stardict.db 3,402,564 行 + WordNet 3.0 + cmudict-0.7b）：

    | 指标 | data-1（生产） | 首次重建（B-001 原码，file[0] 缺陷在） | **data-2 最终产物** | B-001 卡预期 |
    |---|---|---|---|---|
    | entries | 3,402,564 | 3,402,564 | **3,402,564** | 3,402,564 ✓ |
    | us_phonetics | 101,156 | 101,156 | **101,156** | 101,156 ✓ |
    | thesaurus_words | 145,674 | 145,674 | **145,674** | 145,674 ✓ |
    | thesaurus_lines | 141,847 | 157,072 | **160,654**（+18,807，+13.3%） | 157,072（被本卡修正超越，见 Development Log 第 3 条） |
    | 有行去重词头 | 127,590 | 140,197 | **142,756** | 140,197（同上） |
    | 命中零行 | 18,084 | 5,477 | **2,918** | 5,477（同上） |
    | zh_index | 197,349 | 197,349 | **197,349**（ECDICT 译文含 〇 为 0，扩 〇 现阶段 no-op） | — |

  - kind/pos 变化（**注意基线是首次重建（B-001 原码）而非 data-1**）：syn|adv. 45（全是串扰产物）→ **2,689**；ant|adv. 0 → **905**；syn|adj. 20,205 → 20,231。data-1 生产库实测（独立审查提供，本机 data-1 副本复算吻合）：syn|adv.=**2,779**、ant|adv.=**917**、syn|adj.=**5,049**、syn|n.=112,467、syn|v.=11,565、ant|adj.=4,273、ant|n.=2,957、ant|v.=1,840（合计 141,847）。**data-2 syn|adv. 2,689 略低于 data-1 的 2,779 是预期、非回归**：data-1 的 adv. 行含 115 行词头不在 index.adv 的纯串扰产物（本机实测，审查员估 50–90 偏保守——如 aircraft landing→indecorously、burn→morphologically、comfort→yonder、conviviality→better/best，全是 noun/verb/adj 词头与 data.adv offset 碰撞顶出的无义配对），data-2 的 2,689 全为真实 adv 行。两库 adv 行总量（data-1 3,696 − 115 串扰 ≈ 3,581；data-2 2,689+905 = 3,594）接近但不必精确相等——data-1 的裸 offset 键还有 noun/verb/adj 反向顶掉 adv 目标的另一向丢失，两种失败模式的残差与碰撞模型定性自洽。
  - 四类抽样全 PASS（命令：只读 SqliteConnection、Pooling=false）：muggy `syn|adj.|steamy, sticky, wet`；Rome `syn|n.|roma, eternal city, italian capital, capital of italy, national capital, leadership, leaders`；a.k.a. `syn|adv.|alias, also known as`（**data-1 生产库本就有此行**——裸 offset 键时代 offset 270446 四文件间恰好不碰撞、侥幸命中；B-001 file[0] 修复后丢失，本卡修复后恢复且从侥幸变必然；独立审查已对 data-1 官方资产逐字节核实）；bad `syn|adv.|badly`（data-1 `thriftily, badly` → 修复后串扰清除）。
  - 产物：dist/dictionary.zip 182,890,568 B + .sha256（`da0a2ce6dcafed9b804cd4c9aa72ae834153abd6603fb3edc53f08527c420bc5`）；audio-pack.zip 593,599,501 B（data-1 原字节，sha256 `6e17c72c52bd316d063c18bc604defe96276844a6a575dffaff9df3ff3fd8007` 与 .sha256 复用一致）。中间产物 /tmp/p003/（重启即清）。
  - data-2 发布：https://github.com/cholf5/stupid-dict/releases/tag/data-2 —— 四资产（dictionary.zip 182,890,568 / dictionary.zip.sha256 81 / audio-pack.zip 593,599,501 / audio-pack.zip.sha256 81 字节），`isPrerelease=true`（repo 无正式 Release，releases/latest 保持 404，data-2 不参与）。上传的 .sha256 内容与本地 `shasum -a 256` 逐字节一致。
  - TC-001 冒烟：PASS（事件时序 purge 先于重下；db meta 与 data-2 产物一致；无 .part 残留；真实下载走生产链）。
  - 风险与备注：①macOS 本地打包的 app zip（StupidDict-osx-arm64*.zip）为打包副产物，未进 data-2（App 包走 CI 正常发版链）；②package.sh 的 --audio-pack 传 dist 内同路径文件会被 set -e 中断（cp identical），已留痕；③首次重建（带缺陷）的 dictionary.db 已被最终产物覆盖，157,072 行的中间值仅存于本卡记录；④ECDICT 译文 〇 为零意味着 zhTermRegex 扩展对 data-2 无增量，未来源数据更新时自动生效。
