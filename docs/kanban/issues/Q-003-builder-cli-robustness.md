---
id: Q-003
title: 构建器 CLI 与构建健壮性三小项（参数越界 / journal 半成品库 / --top 解析）
type: chore
priority: P2
size: M
status: done
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

DataBuilder / AudioPackBuilder 的 CLI 报错可读、构建中断不留半成品库。

## Background

2026-10-09 扫描确认：

1. **DataBuilder 位置参数越界**——`DataBuilder/Program.cs:9` 只挡零参数，`:51` `positional[0]` 无 `Count` 检查：`dotnet run -- ... --wordnet /dir`（忘传源文件）时 args.Length≥1 过首检，`positional` 为空 → 未捕获 `IndexOutOfRangeException` 裸堆栈。修法：校验 `positional.Count`，不足时打印用法退出。
2. **`journal_mode=OFF` 半成品库**——`Core/Dictionary/DictionaryDatabase.cs:34` `PRAGMA journal_mode=OFF; synchronous=OFF;`，且无 Rollback（:92-102 只有 Begin/Commit），依赖 Dispose 隐式回滚——journal OFF 时回滚不工作、崩溃后文件可能损坏（SQLite 文档明确）。`Create()` 先删旧文件（:29），350 万行单事务中途异常（磁盘满等）后，默认输出路径 `ApplicationData/StupidDict/dictionary.db`（DataBuilder Program.cs:142-144）**正是应用启动自动查找的词典位置** → 应用下次打开读到无 `built_at` meta 的半截库、无法自检。修法方向：临时文件构建 + 成功后原子改名（更稳，顺带解决「先删旧文件」窗口）。
3. **AudioPackBuilder `--top abc` 静默误报**——`AudioPackBuilder/Program.cs:27-28` `case "--top" when i + 1 < args.Length && int.TryParse(...)` 失败落 default，把参数值当词典路径，最后报「词典不存在： abc」。修法：解析失败显式报「--top 需要整数」退出。

## Acceptance Criteria

- [x] 三项分别落地；构建器手工冒烟跑通
- [x] 构建中途异常不再污染应用词典查找路径

## Subtasks

- [x] DataBuilder 参数校验
- [x] 临时文件 + 原子改名（或等价方案，留痕）
- [x] AudioPackBuilder --top 显式报错
- [x] 各补一条单测/冒烟记录

## Dependencies

- none

## Test Cases

### TC-001: 参数不足给用法

Steps:
1. `dotnet run --project src/StupidDict.DataBuilder -- --wordnet /some/dir`

Expected:
- 打印用法、退出码非 0，无堆栈

Result: ✅ 通过（真实进程冒烟）。stderr 首行 `缺少 ECDICT 源文件参数。`，随后完整用法；exit=1；无堆栈。零参数同 exit=1 给用法。进程内钉子 `MissingSourceArgumentIsRejectedInsteadOfIndexOutOfRange` 同语义。

### TC-002: 构建中断无半成品

Steps:
1. 注入构建中途异常（如磁盘满模拟）

Expected:
- 输出路径无 dictionary.db 半成品（临时文件被清理）

Result: ✅ 通过（真实进程冒烟，注入点为主事务提交后 WordNet 步骤：`index.noun` 第 3 列非数字 → `WordNetThesaurus.ReadIndex` 的 `int.Parse` 抛 FormatException）。stderr `构建失败: The input string 'abc' was not in a correct format.`，exit=1；输出路径上的旧库 **字节级原样**（SHA256 前后一致）；输出目录零 `*.tmp` 残留。进程内钉子 `BuildFailureLeavesPreviousDictionaryIntactAndCleansTempFiles` 同场景；红检（临时复原「Create 直接打输出路径 + 不改名」旧写法）该测试精确转红、还原后转绿。

## Development Log

三小项逐项修逐项验；两个构建器的解析逻辑抽成可注入的 internal 纯函数（仿 B-011 ParseVoiceList 惯例），DataBuilder 构建编排抽成 `DictionaryBuilder.Run`（顶语句形式无法在测试进程内驱动 TC-002 的中途异常注入）。

**① DataBuilder 参数越界（Q-003-1）**——修法：解析与校验下沉 `BuilderOptions.Parse(args, out error)`（internal record，DataBuilder 已有 `InternalsVisibleTo Core.Tests`），`positional.Count == 0` 显式报「缺少 ECDICT 源文件参数。」；原「args.Length < 1 只挡零参数」首检并入 Parse（零参数现在也带原因行 + 用法，比旧版只打用法多一句原因，属报错可读化范围内的刻意行为变化）。校验顺序：positional 非空 → wordnet 目录存在 → cmudict 文件存在 → 源文件存在（与旧版相对顺序一致，仅新增首检）。Program.cs 顶语句收敛为 `return DictionaryBuilder.Run(args);`。

**② journal 半成品库（Q-003-2）**——修法方向采纳卡面建议：**临时文件构建 + 成功后原子改名**。落点在 DataBuilder 编排层（`DictionaryBuilder.Run`）而非 `DictionaryDatabase.Create`，理由留痕：`Create` 是共享原语，`TestDatabase.Create` 与 `WordNetThesaurusTests`（B-001 的 4 条夹具测试）依赖「Create(path) 后 Dispose 时文件就在 path」语义，把临时+改名塞进 Create/Dispose 需要引入显式 Complete 并改写全部既有调用点；而污染应用查找路径的只有 DataBuilder 这一个调用方，政策放编排层零风险且语义直白。实现：临时路径 = 输出同目录 `dictionary.db.<guid>.tmp`（同目录保证 `File.Move(overwrite: true)` 走同卷 rename 原子进位；应用按精确文件名 `dictionary.db` 查找，带 GUID/.tmp 后缀永不误认）；主事务 + WordNet 步骤全部成功、连接关闭后才 `File.Move` 进位；任何一步异常 → catch 里尽力删除临时文件、stderr 打 `构建失败: <message>`、return 1。效果：正式输出路径上要么完整新库、要么原样旧库（旧实现「先删旧文件」的整个构建期窗口被收窄为一次 rename；输出被占用如应用开着旧库时改名失败也不再毁掉可用产物）；`DictionaryDatabase.cs` **一字未改**——`journal_mode=OFF` + 无 Rollback 在「失败即整文件删除」的策略下无害（回滚不再被依赖，硬崩溃残留的也只是不在查找路径上的一次性 .tmp 文件），刻意保持最小改动。CL 报错 friendliness：构建中途异常从裸堆栈改为单行 `构建失败: <message>` + 非零退出（构建期工具，退出码 + 消息即诊断；堆栈对磁盘满/格式坏这类场景无增量信息）。

**③ AudioPackBuilder `--top abc`（Q-003-3）**——修法：解析循环抽 `AudioPackOptions.Parse`（internal record，AudioPackBuilder csproj 补 `InternalsVisibleTo Core.Tests`，Core.Tests 补 ProjectReference——先例是 Core.Tests 本就引用 DataBuilder 钉 B-001 夹具），`--top` 拆三个 case 依次覆盖：缺值（`--top` 落尾）报「--top 需要一个整数参数（例如 --top 80000）」、值可解析则 `top = t` 并消费 token、值不可解析报「--top 需要整数，收到: "abc"」——三条路都不再落 default 把值当词典路径。合法值路径行为不变（含 int.TryParse 接受负数/带符号等既有宽容度）。

**测试**（Core.Tests +9 → 81，App.Tests 195 不动）：新增 `BuilderCliTests`——①`MissingSourceArgumentIsRejectedInsteadOfIndexOutOfRange`（TC-001）、`EmptyArgumentsAreRejectedWithUsage`、`ValidArgumentsResolveSourceOutputAndFlags`；②`SuccessfulBuildWritesDictionaryWithMetaAtOutput`（ Happy 路径 + built_at/entries meta 断言）、`BuildFailureLeavesPreviousDictionaryIntactAndCleansTempFiles`（TC-002：真实经 `DictionaryBuilder.Run` 构建后注入 WordNet 步骤 FormatException，断言 exit 1 + 旧库逐字节原样 + 零 .tmp 残留）、`InvalidSourceIsRejectedWithoutWritingOutput`；③`TopRequiresAnIntegerValue`（Theory：abc / 落尾 两例，断言错误含 `--top` 且不误报词典不存在）、`ValidTopIsAcceptedAndDoesNotBecomeTheDictionaryPath`。夹具 `WriteEcdictSource` 用原始 SqliteConnection（Pooling=false）造极小 stardict.db。红检两组均实测：②红检如上；③红检（复原旧单 case）Theory 2 例精确红、合法值用例保持绿。两处红检后均已还原并复绿。

**冒烟记录**（真实 `dotnet run --no-build` 进程，输出全部显式指向 /tmp 临时目录，未触碰本机 AppData 词典位置）：TC-001/零参数/TC-002（成功 + 注入失败）如上 Result；`--top abc` → `--top 需要整数，收到: "abc"` exit=1（无「词典不存在」）；`--top`（落尾）→ 缺值文案；`--top 80000 /nonexistent/dictionary.db` → 推进到词典检查报「词典不存在: /nonexistent/dictionary.db」证明解析通过。全套 `dotnet test StupidDict.slnx`：Core 81/81 + App 195/195 全绿，0 警告。

**对 P-003 的兼容性**：schema 零变更（`DictionaryDatabase` 未动）；成功构建的产物内容与旧实现逐字节同构（同一插入序列、同一 meta 集，仅构建期间文件名不同）；CLI 层唯一行为差异 = 失败路径报错文案/退出码更明确、零参数多一行原因。P-003 重建 dictionary.db 可直接使用本修法。

**邻接问题（留痕不顺手修）**：
- DataBuilder/AudioPackBuilder 的「选项值缺失落到 default 变 positional」同族：`--wordnet`（无值落尾）会被当源文件报「源文件不存在: --wordnet」；`--workers abc` 因 guard 里 `args[++i]` 先自增再解析失败，值 token 落 default 变词典路径（与 --top 修复前同型）；AudioPackBuilder `--out`/`--zip`/`--piper` 等落尾同理。均给清晰错误 + 非零退出，不崩溃，待后续卡统一。
- DataBuilder positional 第 3 个及以后参数被静默忽略（可考虑显式报错）；`--top` 负数可解析（SQLite `LIMIT -5` 语义为不限），语义上是「不限制」不是错误。
- CmuPhonetics.Load 对格式坏行逐行容忍不抛（仅文件级 IO 才可能抛，且在源文件存在性检查之后），未纳入本次 catch 范围评估的变更。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.DataBuilder/Program.cs`、`src/StupidDict.AudioPackBuilder/Program.cs`、`src/StupidDict.Core/Dictionary/DictionaryDatabase.cs`
- How to run/verify: `dotnet test StupidDict.slnx` + 构建器手工冒烟
- Results: ✅ 2026-10-09 修复会话验证。
  - `dotnet test StupidDict.slnx`：Core.Tests **81/81**（基线 72 + 新增 BuilderCliTests 9）、App.Tests **195/195**，0 警告（TreatWarningsAsErrors）。
  - 真实进程冒烟全过（命令与输出见 Development Log「冒烟记录」段）：TC-001 用法报错 exit 1 无堆栈；TC-002 中途异常下旧库 SHA256 前后一致、零 .tmp 残留、`构建失败: …` exit 1；`--top abc`/`--top` 落尾显式报错、合法 `--top 80000` 正常推进到词典检查。
  - 红检两组：②临时复原旧「输出路径直接删旧建新」写法 → `BuildFailureLeavesPreviousDictionaryIntactAndCleansTempFiles` 红、还原绿；③复原旧 `--top` 单 case → Theory 2 例红、还原绿。
  - `DictionaryDatabase.cs` 复核后未改动（理由见 Development Log ②段）：journal_mode=OFF 在「失败整文件删除」策略下无害，共享原语语义改动会波及 TestDatabase / WordNetThesaurusTests 全部调用点，超出本卡范围。
  - 未验证项（留痕）：磁盘满等真实 IO 故障仅以异常注入等价模拟；Windows 上 `File.Move(overwrite: true)` 的原子性依赖 MoveFileEx(REPLACE_EXISTING)（同卷），未在 Windows 冒烟——P-003 重建在 macOS 上执行，不受影响。
