# 外部取消者续集：与静默无关的发版日七杀，与手动发版 playbook

- **状态**：v1.0.0 已发布（tag run 37878344915 第 4 次尝试六 job 全绿，release job 用 CI 产物覆盖了本地产物）；取消者身份仍未证实
- **日期**：2026-10-09
- **背景**：`2026-10-09-ci-first-green.md` 记录了 v1.0.0 发版评估时的四次外部取消，把画像收窄为「sibling 会话的 CI watcher 按静默判卡死」，并认定逐测试输出是解毒剂。当天晚些时候真正执行 `scripts/release.sh 1.0.0 --watch`，取消者重现且行为超出了那个画像。本文记录新证据、被推翻的结论，以及最终把 Release 发出去的 playbook。

## 事实时间线（全部 2026-10-09 UTC）

九次 test step 执行：七次被杀、两次幸存，跨五个 run。

| run / attempt | commit | test step | 结局 |
|---|---|---|---|
| 37873354563 (main) | 8b6f315 | 02:11:32→02:22:30 (11m) | cancelled，杀前静默 288s |
| 37874933205 att1 (v1.0.0) | 8b6f315 | 02:31:20→02:38:37 (7m) | cancelled，杀前静默 179s |
| 37874933205 att2 | 8b6f315 | 02:42:29→02:49:41 (~7m) | **exit 143**，杀前静默 151s |
| 37874933205 att3 | 8b6f315 | 03:01:02→03:07:02 (6m) | **幸存**；macos 打包死于 package.sh 的 bash 3.2 坑 |
| 37878311405 (main) | ca771e1 | 03:14:07→03:22:55 (9m) | 被杀（纯测试 run，可弃） |
| 37878344915 att1 (v1.0.0) | ca771e1 | 03:14:36→03:19:25 (4.8m) | exit 143，**杀前静默仅 20s、138 条测试行稳定输出** |
| 37878344915 att2 | ca771e1 | 03:23:17→03:29:48 (6.5m) | cancelled |
| 37878344915 att3 | ca771e1 | 03:32:18→03:41:08 (9m) | cancelled |
| 37878344915 att4 | ca771e1 | 03:45:04→03:50:35 (5.5m) | **幸存，六 job 全绿**；release job 03:51:50 覆盖上传 |

## 对旧画像的三点修正

1. **「静默触发」被证伪**：att1（37878344915）被杀时最后一条 Passed 距死亡仅 20 秒，138 条逐测试行稳定推进——按静默判定挂死的 watcher 杀不到它。旧文档「逐测试输出是解毒剂」**不成立**；逐测试输出仍值得保留（人对进度的判断受益），但它不构成对取消者的防御。
2. **「时长阈值」不成立**：死亡时 test step 时长 4.8m–11m 不等，幸存者 5.5m–6m，分布重叠。
3. **「只杀 test job」是幸存者偏差**：打包 job 只在 att3 执行过约 23 秒就死于真 bug（见 `2026-10-09-package-sh-bash32-fullwidth-varname.md`），从未长到够被杀。别从「只有 test 被杀」推断取消者认识 job 类型。

取消者身份依旧无法外部证实，ci-first-green.md 的排除清单仍有效（并发组按 ref 隔离且无新 run、无 timeout-minutes、无 webhook/第二个 workflow/仓库脚本）。两次幸存窗口分别落在 sibling 会话跑本地测试（02:53–03:10）与本地收尾提交（03:16–03:48）的时段内，但 03:22–03:41 的连续击杀同样落在 sibling 活跃期——**「取消者只在多会话活跃期出现」这个方向成立，但击杀时刻的单一变量归因全部失败**，连 sibling 自己的 run 也被杀过。证据不足时别把「sibling 会话」写成定罪。

## 两种死法的辨认

- 步骤 conclusion = `cancelled` + `##[error]The operation was canceled.`：平台层取消（cancel API / 并发组）。
- 步骤 conclusion = `failure` + `##[error]Process completed with exit code 143.`：SIGTERM（128+15）直接送达进程树，无任何 `Failed <test>` 行，post 步骤 skipped——同样是外部终止，只是呈现为「失败」。**看到 exit 143 + 零测试失败行，先按外部取消处理，别去修测试。**

## Playbook：取消者持续活动时怎么把 Release 发出去

按实际发生顺序：

1. **被取消 ≠ 要动 tag**。纯取消用 `gh run rerun --failed` 原地重跑，tag 不动；只有修复需要新提交时才走「删 tag → 修 main → 重打 tag」（release.sh 注释里的恢复路径；tag 与 csproj 一致性由 CI 把关，修复必须先落 main）。
2. **重试挂后台当彩票**。取消是阵发的，重试成本约 12 分钟/次，第 7 次尝试拿到全绿。轮询放后台，主线并行推进。
3. **发布构建必须从 tag 的干净 worktree 出，绝不用共享工作树**：`dotnet publish` 构建的是工作树不是 HEAD，而共享工作树里有另一个会话未提交的 `Assets/app-icon.ico`（AvaloniaResource，会被编进二进制）。`git worktree add /tmp/xxx v1.0.0` 后在 worktree 里跑 `scripts/package.sh`，隔离是唯一可信的。
4. **手动建 Release 是文档化路径，不是黑箱兜底**：package.sh 头部「Upload with: gh release create v1.x dist/*」与尾部「发布步骤（手动）」写得明明白白。照抄 workflow release job 的行为（`gh release create --verify-tag --generate-notes`、资产名 `StupidDict-<ver>-<rid>.zip`、只带应用包）保证后续 CI 接管时幂等。
5. **双路径安全的前提是 release job 幂等**：先手动发（Release 立刻存在），CI 彩票后续变绿时其 release job 的 `view` 守卫跳过 create、`upload --clobber` 用干净环境产物覆盖本地产物——最终状态收敛到 CI 构建。顺序反了才有风险（等 CI 就永远发不出去）。
6. **测试证据可迁移要明说**：ca771e1 与 8b6f315 的差异只有 `scripts/package.sh`（不在任何 dotnet 测试面上），8b6f315 的 CI test 绿（att3）加本地全绿（Core 41 + App 97）对 ca771e1 的 C# 代码构成证据。手动发版前把这条论证写下来，别让它看起来像「跳过测试」。

## 顺手踩到的工具坑（速记）

- zsh 把 `"$VAR:refs/..."` 里的 `:r` 当变量修饰符吃掉，refspec 变成 `ca771e1…efs/heads/main`——带后缀路径的变量拼接一律 `${VAR}`。与脚本里的全角字符坑同一个教训：**展开边界必须显式**。
- shell 状态不跨命令保留，`${MY_SHA}` 展开为空后 refspec 退化成 `:refs/heads/main`，等于**删除请求**，GitHub 报 `refusing to delete the current branch: refs/heads/main`——看到这个报错先想「空源 refspec」，不是权限问题。
- `gh api .../actions/runs/<id>/attempts`（列表端点）不存在（404）；按次取是 `/runs/<id>/attempts/<n>` 与 `/runs/<id>/attempts/<n>/jobs`。重跑多了以后 `gh run view` 只反映最新一次，历史 attempt 只能走这条 API。

## 教训浓缩

1. 多会话共库时，CI run 无故被杀的第一嫌疑是外部取消者；辨认凭据：无 Failed 行、无并发组新 run、击杀时刻随机分布且与静默/时长无关、exit 143 或 cancelled。
2. 对旧结论要敢翻案：20 秒静默被杀的新样本直接推翻「静默触发」画像。画像服务于应对策略——策略有效比画像正确重要。
3. 发布产物只能来自 tag 对应的干净 worktree；共享工作树的「当前状态」不属于任何一次发布。
4. release job 的幂等设计（view 守卫 + clobber）是「手动先发 + CI 后接管」的安全前提；写 workflow 时把幂等当需求写。
5. 重试是廉价的：后台挂彩票、主线走兜底，两条路径靠幂等性收敛到同一个结果。
