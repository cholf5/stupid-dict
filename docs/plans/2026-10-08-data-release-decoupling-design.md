# 数据资产与 App 版本解耦：独立 data-1 prerelease

日期：2026-10-08

## 背景与目标

`dictionary.zip`（约 600 MB）与 `audio-pack.zip`（566 MB）不随 App 版本变化，
且无法在 CI 构建（词典需本地 DataBuilder，发音包需 6 小时本地 TTS 合成）。
原设计里应用内下载走 `releases/latest/download/<asset>`，为让该 URL 持续解析，
CI 的 release job 在每个 App 版把上一个 Release 的两件数据资产**复制**过来，
`release.sh --watch` 也把 `dictionary.zip` 列为发布必需产物。成本：

- 每个 App 版在自己的 Release 里存一份 1.16GB 重复资产，随发版数线性膨胀；
- 接力是脆弱环节：上游缺资产时 CI 只打 warning，静默产出缺资产的 Release
  （首发时无 Release 可接力，只能人肉掐 CI 结束前补传，本版就撞上一次）；
- App 发版被迫与 1.16GB 上传/复制捆绑，尽管数据根本没变。

目标：数据资产与 App 版本彻底解耦——数据一次发布、基本不再动；App 发版
只携带应用包，流程里不再有任何数据资产环节。

## 做了什么

- 新增独立数据 Release：tag `data-1`，**prerelease**，只装 `dictionary.zip` /
  `audio-pack.zip` + 两个 `.sha256`，一次上传不再动。
- `ReleaseAssets`：新增 `DataTag = "data-1"` 常量，`GithubUrl` 从
  `releases/latest/download/<asset>` 改为 `releases/download/data-1/<asset>`。
  URL 形状只有这一处生产者，无测试钉住；镜像前缀（ghfast.top 等）拼在完整
  GitHub 路径前，对固定 tag 路径同样适用，下载回退链不变。
- CI（`dotnet-desktop.yml`）：删除「Carry data assets forward」整段；
  release job 只上传应用包。
- `release.sh --watch`：必需产物从「4 应用包 + dictionary.zip」收紧为
  4 应用包；删除 audio-pack 可选提示；新增对 `data-1`（与
  `ReleaseAssets.DataTag` 同步维护）的存在性提醒——缺失只警告不失败，
  因为应用包本身仍有效，数据 Release 可随后补上。
- README「打包与发布」与 AGENTS.md 发版段同步。

**prerelease 标记是本方案的承重墙**：GitHub 的 `releases/latest` 是"最近发布
的非 prerelease、非 draft"，而应用内「检查更新」读的正是这个页面（
`UpdateChecker.LatestReleaseUrl`，网页端 302 抠 tag）。若数据 Release 以普通
Release 身份发布，任何一次发布时间晚于最新 App 版的数据更新都会抢走
latest，更新检查拿到 `data-1` 这种 tag，三段数字解析失败 → 检查更新报错。
prerelease 从根上把它排除在 latest 之外，发布顺序随意。

## 评估过什么、为何不做

- **运行时重定向 / 先拉一个小 manifest 拿最新数据地址**：能彻底摆脱常量钉死，
  但给首次下载链多加一跳网络依赖和一种失败模式，而数据更新的现实频率约等于
  零。YAGNI。
- **更新检查加固为「跳过解析不出 x.y.z 的 tag」**：prerelease 已经把
  `data-*` 排除在 latest 之外，这道第二道防线没有攻击面。YAGNI。
- **靠发布顺序规避（先发 data 后发 App，数据永远后发就会被 latest 抢走）**：
  把正确性押在操作纪律上，一次数据更新就破。prerelease 标记零成本且无条件成立。

## 已知取舍

- 数据若真要更新：发 `data-2` + `ReleaseAssets.DataTag` bump + 随下个 App
  版生效；**已安装的旧版 App 永远下载旧 tag**。可接受——数据修复本来就该
  伴随 App 版本，且旧数据持续可用（不是坏，只是旧）。
- `release.sh` 里的 `DATA_TAG=data-1` 与 C# 常量是两份拷贝，靠注释互指同步；
  脚本只做存在性警告，失同步的后果是提醒而非静默失败。
- 本方案落地当天即发 `data-1`（566MB 发音包 + 601MB 词典）与 v1.0.0 首发，
  首发流程因此没有任何手动补传窗口。
