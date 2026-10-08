# CI 首次真正跑绿：审计门禁、CJK 字体缺失与看不见的取消者

- **状态**：已修复（aa4777c 全绿，test job 326s；scroll 理论三宽度全过）
- **日期**：2026-10-09
- **背景**：v1.0.0 首次发版评估发现 main HEAD 的 CI 是红的。追下去发现一个叠加事实：5adddb5 加入的一批 UI 回归测试**从未在 CI 上执行过**——5adddb5 之后的第一次 CI（f2c06d2）在 restore 就阵亡，之后每次都没跑到测试。于是老问题（审计门禁）和新测试的隐藏假设（CJK 字体）在同一个修复窗口里连环爆。

## 第一层：NU1903 让 restore 全挂，本地却是绿的

`Directory.Build.props` 的 `TreatWarningsAsErrors` 把 NuGet 审计告警升级为错误：传递依赖 `SQLitePCLRaw.lib.e_sqlite3` 2.1.11（`Microsoft.Data.Sqlite 10.0.0` 拉进）命中高危公告 GHSA-2m69-gcr7-jv3q（SQLite 内存破坏，需 ≥ 3.50.2），restore 阶段全部工程直接失败。**本地 `dotnet test` 一直是绿的，因为本地 NuGet 审计数据库滞后于 nuget.org 的公告数据**——审计门禁下「本地绿」不构成任何证据。

修复走了一段弯路：

- **尝试一（钉版本）**：显式 `PackageReference` 钉 `lib.e_sqlite3 3.50.3`。公告页面还写着 patched = none，但 nuget.org 实际已把该包版本号与内置 SQLite 对齐重排（3.50.3 / 3.53.3），3.50.3 过了审计。**结果 CI 上 App.Tests 静默停滞、四次 run 全灭**（见第三、四层）；3.53.3 在本地也把整套测试从 3 分钟拖到 16 分钟（该数字后来被证明掺了水，见第五层）。跨 major 重排的 3.x 原生库与 2.1.x 托管层混搭不可信，「audit-clean ≠ 能跑」。
- **最终方案**：回退 pin（恢复与上次全绿 run 完全一致的依赖图），在 `Directory.Build.props` 用**单条公告的 `NuGetAuditSuppress`** 压制。上游 SQLitePCLRaw 至今没有修复版（公告 patched 一栏为空，修复只能在它发布捆绑 SQLite ≥ 3.50.2 的新包时落地），suppress 是唯一可落地的选择；风险评估（离线本地词典、SQLite 只开自家库、唯一不可信输入是手动导入的外部 db）、移除条件（审计清零即删）都写在 props 注释里。**不写无条件全局关审计，suppress 必须收窄到单条公告。**

## 第二层：钉 3.x 原生库为什么跑不绿（当时的误诊与后来的真相）

四次 CI run 的死法完全一致：restore 过、Core.Tests 几秒绿、App.Tests 静默直到 7.5–12.5 分钟时被取消。当时把停滞归因于 3.50.3 的 linux-x64 原生二进制，回退到 2.1.11 后**依然停滞**——停滞与依赖版本无关（真相见第三、四层）。教训：**改依赖要么连 CI 一起验证再落定，要么一开始就选不改变运行时行为的方案**（suppress 不动依赖图，天然没有这层风险）。

## 第三层：裸 runner 没有 CJK 字体——两个独立的后果

CJK 词典应用在只有西文字体的 ubuntu runner 上跑 headless UI 测试：

1. **几何断言失败，且是真失败不是舍入**：`ResultsScrollReachesBottom(width: 1000)` 报 `StackPanel bottom 373.0 beyond viewport 337`——36px，正好一个 CJK 行高。时序是：测试先等联想词 WrapPanel 出现、设定最大 offset；无本地 CJK 字体时**首次 CJK 整形发生在这之后**，回退字体解析让词库行重新测量变高，Extent 涨了、已设的 offset 过期。真人永远不会在字体稳定的毫秒级窗口里滚动，这是测试时序撞上了首帧布局假设，不是应用 bug。修复：测试里**重设最大 offset 直到报告的 extent 不再变化**（≤3 轮）——「滚到底」要滚到「稳定」，不是滚一次。
2. **文本密集测试慢一个数量级**：每个字形 run 触发一次 fontconfig 全盘回退扫描，词库相关测试集体变慢，App.Tests 整体拖过 10 分钟。修复：workflow 测试步骤预装 `fonts-noto-cjk`（18 秒）。**CJK 产品的 CI 要把 CJK 字体当测试基建**，字体一装，两层问题一起消失。

## 第四层：默认 verbosity 下「慢」与「挂」不可分辨

`dotnet test` 的默认 console 输出只在程序集结束时打汇总，逐测试一行都不打。于是 CI 上缓慢推进的 App.Tests 和真死锁**看起来一模一样**——对人如此，对看门狗也如此。四次 run 都是「Core.Tests 绿了之后 App.Tests 长时间静默 → 被取消」。修复：CI 测试步骤加 `--logger "console;verbosity=normal"`，逐测试即时输出；加了之后 run 顺利跑完（326s），怀疑卡死的判据从根上消失。

## 第五层：四次「The operation was canceled」——排除法与真凶画像

四次取消的间隔 7.5–12.5 分钟不等，报错都是 `##[error]The operation was canceled.`。排除清单：

- workflow 只有 `cancel-in-progress` 的并发组，但取消发生时**不存在更新的 run**（`gh run list` 反复核对）；
- workflow 没有任何 `timeout-minutes`；
- 仓库无 webhook、无第二个 workflow、无 GitHub App 痕迹；
- `scripts/release.sh` 的 --watch 是纯只读轮询（`gh run list/view`），无 cancel 逻辑；
- 事件流无 force-push（GitHub events API 缺失且乱序，只能当反证）。

最符合的画像：**同目录另一个会话的自动化在盯 CI**，等待预算耗尽后把「静默无输出」的 run 当卡死取消（它在自己的四次窗口内恰好活跃）。多会话共用一个仓库时，你的 CI run 可能被别人家的看门狗杀掉——第四层修复的逐测试输出恰好也是对这个的解毒剂：日志在推进，「卡死」的误判就立不住。

## 顺手踩到的工具坑（速记）

- `gh run list --commit <短SHA>` **静默返回空**，必须完整 40 位 SHA——为此白轮询了 10 分钟 null。
- `gh run view --log-failed` 对 cancelled 步骤返回空；被取消的 run 要用 `--log` 读全程（被取消的步骤没有「失败」记录，但日志在）。
- `dotnet list package --vulnerable --include-transitive` 在多 TFM 工程（net10.0;net10.0-windows）上报 `Unable to read a package reference`，restore 后依旧（已知解析怪癖）；按单 TFM 工程逐个审计即可，App 的 SQLite 依赖图就是 Core 的。
- **XML 注释里不能出现 `--`**：在 Directory.Build.props 注释里写 `dotnet list package --vulnerable` 直接把 props 炸成解析错误（`An XML comment cannot contain '--'`），而且错误指向文件而不指向注释内容，第一次 restore 还侥幸过了——注释里提 CLI 参数要改写措辞。

## 多会话同目录协作

- 另一个会话在共享工作区先做了本地提交（README 美化），我的提交落在它上面；`git status --porcelain` 只看工作树，**看不到自己的父提交已经被换掉**，push 时把两个提交一起带上了 main。推前扫一眼 `git log`，别只看 status。
- 本地测试从 3 分钟劣化到 16 分钟，最初归因给 3.53.3——实际机器正被该会话的文件churn触发的 Spotlight 重索引 + Time Machine 扫描拖住（`top` 空闲、`sample` 显示线程在等、`pgrep` 抓到 mdworker/backupd）。**共用机器上的单点性能归因不可信**：先排 IO/索引/备份，再怀疑代码；而判定回退依赖的依据最终落在 CI 的对照数据上（2.1.11 同样停滞），不是本地计时。

## 教训浓缩

1. 本地绿 ≠ CI 绿，对审计门禁尤其如此：审计漏洞数据是活的，本地缓存滞后会漏报，restore 在 CI 才炸。
2. CI 测试步骤必须让进度可见：默认 verbosity 下慢与挂不可分辨，会被人和自动化一起误杀。
3. CJK 产品的 CI 把 CJK 字体当测试基建；涉及文本的几何断言要容忍「首次整形后的重测量」，滚动断言滚到稳定而非滚一次。
4. 安全告警的处理顺序：先核实上游有没有修复版（公告页会滞后于 nuget.org 实际发布），有则 pin 并连 CI 验证，没有则收窄范围 suppress + 注明风险评估与移除条件——绝不全局关审计。
5. 排除法要穷尽自动化面（并发组、timeout、webhook、仓库脚本、事件流）再考虑外部因素；多会话共库时，外部因素排第一嫌疑。
