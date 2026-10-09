---
id: P-002
title: 打包/发布脚本健壮性小项（8 处，2 项可选）
type: chore
priority: P3
size: M
status: done
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

打包/发布链的八个小缺口一次清掉；标「可选」的两项评估后落地或留痕不做。

## Background

2026-10-09 扫描确认：

1. **release.sh:137 `gh release view ... || true`**——`gh` 查询失败（网络抖动/Release 刚建未同步）时 ASSETS 为空，:141-148 把全部资产误判为「Release 产物缺失」，诱导去 Re-run 一个本来成功的 job。修法：区分「查询失败」与「真缺失」（查询失败重试或明确提示无法核对）。
2. **release.sh:71-73 mktemp 在 /tmp**——跨卷 `mv` 非原子且权限 0600。修法：`mktemp "$PROJECT.tmp.XXXXXX"` 同目录，mv 原子 rename。
3. **release.sh:42 版本校验**——接受前导零（01.02.003）、不校验新旧版本单调（降级 tag 可发布）。可选修。
4. **package.sh:57/47 `--rids` 无白名单**——rid 直接拼 `rm -rf dist/stage/$rid` 与输出路径，`--rids "../.."` 越界。修法：rid 必须匹配已知 RID 集合（或 `^[a-z0-9]+-[a-z0-9.]+$` 且不含路径分隔符）。
5. **package.sh:88/100 目标 zip 已存在时 `zip -r` 是增量更新不是重建**——本地重复打包 + 中途换过 dictionary.db 时新旧混合。修法：zip 前置 `rm -f`。
6. **package.sh:77 Info.plist `CFBundleShortVersionString` 恒 1.0**——未接 csproj `<Version>`，Finder「显示简介」永远显示 1.0。修法：从 csproj 提取版本注入 heredoc。
7. **.github/workflows/dotnet-desktop.yml:137-141 choco innosetup 未钉版本**——上游 Inno 7 / 包名变化时 ISCC 硬编码路径断裂。可选修（钉 6.x）。
8. **make-icon.py:143 `--content 0` 除零**——argparse 无范围校验。修法：`0 < content <= 1024` 校验。

## Acceptance Criteria

- [x] 1/2/4/5/6/8 落地
- [x] 3/7 评估后落地或留痕不做（3：前导零拒绝落地、单调性校验留痕不做；7：钉 6.7.1 落地）
- [x] `release.sh`/`package.sh` 手工冒烟通过（不真发版，可用 dry-run/参数校验路径验证）

## Subtasks

- [x] release.sh view 失败区分
- [x] release.sh 同目录 mktemp
- [x] （可选）版本前导零/单调校验——前导零拒绝落地；单调性校验评估后留痕不做（理由见 Development Log ③）
- [x] package.sh --rids 白名单
- [x] package.sh zip 前 rm -f
- [x] package.sh Info.plist 版本注入
- [x] （可选）CI choco 钉版本——落地，钉 6.7.1（choco feed 实查确认存在、无 7.x）
- [x] make-icon.py --content 校验

## Dependencies

- none

## Test Cases

- 测试不适用为主（CI shell 脚本）：以手工冒烟 + 关键路径 dry-run 记录代替；Info.plist 修复后验证 .app 在 Finder 显示正确版本。

## Development Log

2026-10-09 修复会话。行号相对卡面已漂移，按内容定位。八处逐处修逐处验；两处可选均给出结论。

① **release.sh gh release view 失败区分**——原 `ASSETS=$(gh release view … || true)` 查询失败时 ASSETS 为空串，下游 MISSING 循环把全部资产误判为缺失、诱导 Re-run 本来成功的 job。修法：查询重试 3 次（间隔 5s，覆盖「Release 刚建未同步/网络抖动」）+ 成功/失败分流——3 次全败走新分支，明确报「无法核对 Release 产物：gh release view 连续 3 次失败（网络抖动或未 gh auth login）」并给手动核对指引（产物齐全无需补救），不再说「产物缺失」；查询成功才进入 MISSING 判定，此时缺失是真缺失。实现细节：`if ASSETS=$(gh …)` 利用条件上下文豁免 set -e； exhaustion 检测用 `i==3`（3 次尝试即 i 递增到 3），成功 break 时 i<3，无需额外标志位；「Release 存在但 0 资产」走查询成功路径、照常报缺失——那本来就是失败态。循环体 `[ "$i" -lt 3 ] && sleep 5` 在 set -eu 下安全（失败的 `[` 非 AND-OR 列表末命令，不触发 errexit；stub 测试实证 3 次迭代完整走完）。

② **release.sh 同目录 mktemp**——`TMP=$(mktemp)` 落 /tmp，`mv` 跨卷退化为 copy+unlink 非原子，且 mktemp 0600 经 rename 原样留在 csproj 上。修法：`TMP=$(mktemp "$PROJECT.tmp.XXXXXX")`（同目录同卷，mv 即原子 rename；模板 X 后缀 macOS/GNU mktemp 均要求落在最后一段文件名，满足）+ `chmod 644`（还原 csproj 既有权限， mktemp 的 0600 不再进库）+ `trap 'rm -f "$TMP"' EXIT`（sed/校验失败时清残留——残留文件是未跟踪文件，会污染重跑时的「未跟踪文件」预检；rename 成功后 trap 触发是无害 no-op）。`$VERSION` 进入 sed 替换段前已被 ①同款正则钉死为纯数字加点，无 `&`/`\` 注入面。

③ **（可选）版本校验**——前导零拒绝**落地**：正则收紧为 `^([0-9]|[1-9][0-9]*)\.([0-9]|[1-9][0-9]*)\.([0-9]|[1-9][0-9]*)$`（单段 0 合法，拒绝 01.02.003 这类从来不会是手滑以外输入的版本；tag 驱动 csproj、资产名、更新检查三件事，脏版本号零容忍）。UpdateChecker.TryParseVersion 对 "01" 按 int.Parse 数值解析，功能上无害——收紧是卫生问题而非正确性问题，故只动入口校验、不碰 C#。单调性校验**评估后留痕不做**：tag/csproj 一致性已由 CI 强制，降级必须显式提交降了版的 csproj（可见、可审计的主动行为）；UpdateChecker 对更低的远端判 UpToDate（不误报更新）， stray 降级「难做出、无害果」；硬拒绝反而为极低频事故加一条需要维护的数值比较逻辑（CUR 解析失败的边界等）。理由已写进 release.sh 正则旁注释。

④ **package.sh --rids 白名单**——rid 直接拼 `rm -rf dist/stage/$rid` 与输出路径，`--rids "../.."` 越界。修法：参数解析后逐 rid 校验 `^[a-z0-9]+(-[a-z0-9.]+)*$`（小写字母数字起头、`-`/`.` 分段、不含路径分隔符——`..`/`.` 首字符即拒，`/` 无匹配面，遍历不可能；`a..b` 类单段合法但只是无害目录名，dotnet publish 的 RID 校验兜底报错）。正则存变量再 `=~`：bash 3.2（本机 /bin/bash 3.2.57，package.sh 经 env bash 可能落在它上面）对 [[ =~ ]] 字面量正则有解析怪癖，变量形式是 3.2 推荐写法。大写 rid 一并拒绝：case 语句按 `win-*` 小写匹配，大写会静默走错 TFM/EXE 分支，拒绝比修正诚实。错误文案 `${rid}` 用花括号——紧邻全角括号，bash 3.2 全角坑的硬规矩。

⑤ **package.sh zip 前 rm -f**——`zip -r` 对已存在目标是增量更新不是重建，旧条目（脚本演进换过布局/换过 dictionary.db）会残留。修法：三处 zip 目标前置 `rm -f`——`StupidDict-$rid.zip`、`StupidDict-$rid-with-dictionary.zip`（卡面点名的两处）+ `dictionary.zip`（同缺陷类、同一失败模式，随本项一并处理并留痕；audio-pack.zip 走 cp 覆盖无此问题）。

⑥ **package.sh Info.plist 版本注入**——`CFBundleShortVersionString` 恒 1.0，Finder「显示简介」永远 1.0。修法：脚本头部从 csproj 提取 `<Version>`（与 release.sh 同一 sed 表达式，tag 的单一来源）注入 heredoc，读不到回退 1.0（本机临时打包兜底）；heredoc 由引用（`<<'PLIST'`）改为展开（`<<PLIST`）——已核对 plist 全文无 `$`/反引号，展开安全。CI 侧 package.sh 在 tag 上跑，版本自动与 tag 一致。

⑦ **（可选）CI choco 钉版本**——**落地**。实查 choco feed（community.chocolatey.org API FindPackagesById）：innosetup 现有版本 6.0.5–6.7.1，无任何 7.x，最新 6.7.1（2026-02-17，IsLatestVersion）。钉 `choco install innosetup --version 6.7.1`，workflow 内注释写明动机（ISCC 路径硬编码 "Inno Setup 6"，同 id 的 Inno 7 不能被自动拾取）与升级姿势（要新 6.x 时有意 bump 该钉）。选落地而非留痕：版本已验证存在（不会引入「钉了不存在的版本」这类新故障），钉住让发版链输入确定，代价只是 6.x 补丁不自动跟进（构建期工具，暴露面小）。

⑧ **make-icon.py --content 校验**——`--content 0` 触发除零（`round(CANVAS * radius / content)`）且更早在 PIL resize((0,0)) 就会炸、负值/超画布值破坏 bbox 断言。修法：`parse_args` 后、任何文件写出前校验 `0 < content <= 1024`（卡面修法原样）；顺带 `--radius < 0` 同类崩溃面（Pillow rounded_rectangle 对负 radius 抛 ValueError）一并拒绝，各给中文单行报错 exit 1。上界用 CANVAS 常量不写死 1024。radius 上限（> content/2 的几何退化）未加——Pillow 不炸、断言兜底，工具语义上属「垃圾进」由使用者自担，不扩大校验面。

冒烟细节备注：第二次实跑因验证命令的 `head -4` 提前关管道 SIGPIPE（exit 141）中断——测试 harness 问题非脚本问题，已补第三次完整跑确认 exit 0 + 无 stage/publish 残留；dist/ 既有 dictionary.zip / audio-pack.zip（data-1 同源资产）经 sha256 核对未受测试扰动。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `scripts/release.sh`、`scripts/package.sh`、`scripts/make-icon.py`、`.github/workflows/dotnet-desktop.yml`
- How to run/verify: 手工冒烟（参数校验路径、`--rids` 非法值拒绝、重复打包 zip 内容干净）
- Results: 已验证（2026-10-09，逐项对应上表序号）：
  - 语法：`/bin/bash -n`（bash 3.2.57，发版脚本真实运行环境）双脚本通过；release.sh 另过 `/bin/sh -n`（其 shebang）；`python3 -m py_compile make-icon.py` 通过；workflow YAML `yaml.safe_load` 通过。
  - ① stub gh 驱动 release.sh 资产核对块四条路径全对：fail-all→恰 3 次尝试后报「无法核对」exit 1；fail-twice→第 3 次成功收敛 exit 0；ok-empty→真 0 资产仍报缺失列表 exit 1；ok-full→一次过 exit 0。echo 文案在 bash 3.2 + `set -eu` 下逐行实跑（全角 `${VAR}` 规矩）无一触发 unbound variable。
  - ② 同目录 mktemp 模板在 macOS 实测可用（创建/640→644/mv 进位/EXIT trap 失败路径零残留，四点各验）。
  - ③ 版本正则表驱动：接受 1.0.0 / 0.0.1 / 10.20.30；拒绝 01.02.003 / 1.02.3 / 1.0 / 1.0.0.0 / v1.0.0 / 空串（1.0.0-beta 被拒与旧正则一致，非行为变化）。
  - ④ `--rids "../.."` / `"."` / `"osx-Arm64"` / `"win/x64"` 全部 exit 1 拒绝、清晰报错、dist/ 零副作用；合法 `--rids "osx-arm64 win-x64"` 通过校验进入 publish。
  - ⑤ 往 `dist/StupidDict-osx-arm64.zip` 注入 STALE-MARKER.txt 条目后重跑 package.sh：条目消失、zip 7 个文件、无多余内容——重建非更新实证。
  - ⑥ 三次实跑（osx-arm64 app-only，`--dictionary /nonexistent`）：`unzip -p` + `plutil -lint` OK，`CFBundleShortVersionString` = 1.0.0（与 csproj `<Version>` 一致）；Finder 目检待下次真机开包时顺带确认（产物几何已由 plutil 与 zip 内容验证）。
  - ⑦ workflow 改动仅 choco 行 + 注释；未触发 CI（不发 tag，不碰远端）。
  - ⑧ `--content 0/-5/2000`、`--radius -1` 全部单行中文报错 exit 1、Assets/ 零写入（git status 确认）；`--content 1` 按预期穿过范围检查落到「源图不存在」（校验顺序正确）。
  - 收尾全套 `dotnet test StupidDict.slnx`：Core 81/81 + App 195/195 全绿（与基线一致，脚本改动零波及）。
