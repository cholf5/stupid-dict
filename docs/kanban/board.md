# Kanban Board

两列：Todo / Done。卡片详情在 `docs/kanban/issues/<ID>-<slug>.md`（唯一事实源）。
移动卡片 = 同时改这里的行与 issue 文件的 `status`，两处同步。

来源：2026-10-09 全量扫描（App+Core+构建器+脚本+文档）确认的全部发现；修复批次划分见 `roadmap.md`。

## Todo

| ID | Title | Priority | Size | Blocked | Dependencies |
|---|---|---|---|---|---|
| B-001 | 修 WordNetThesaurus 四处数据质量缺陷（近/反义词行缺失与词性错乱） | P1 | M | | |
| B-002 | 切语言一次触发约 98 次结果页全量重建（UI 冻结） | P1 | S | | |
| B-003 | Navigate 不推进 _searchGeneration，在途查询反噬导航页 | P1 | S | | |
| B-004 | 下载 body 无空闲超时，服务器停发即永久挂起 | P1 | S | | |
| B-005 | MCI play 返回码被忽略 + Stop 的 close 失败泄漏 alias | P1 | S | | |
| B-008 | 留盘 zip 复用链上 CRC 失败无 purge 路径，用户卡死解压失败循环 | P1 | S | | |
| B-006 | Range 续传无 If-Range/Content-Range 校验，换包可拼出损坏 zip | P2 | S | | |
| B-007 | .sha256 解析只容空格分隔，常见变体静默降级为不校验 | P2 | S | | |
| B-009 | 词典下载面板清理两处：删除 zip 失败误报解压失败 + 取消/失败进度条残留 | P2 | S | | |
| B-010 | RecentSearchStore 读写共享连接加锁不对称（GetRecent 无锁） | P2 | S | | |
| B-011 | macOS 首次 TTS 回退在 UI 线程同步跑 say -v ?（读无超时 + 孤儿进程） | P2 | M | | |
| B-012 | 应用退出无停播出口，孤儿音频进程继续播 | P2 | M | | |
| B-013 | 设置枚举未知字符串值导致整个 settings.json 回退默认 | P2 | S | | |
| Q-001 | MainWindow 杂项健壮性五小项（未观察任务/丢弃任务/裸 await/UI 闪烁/滚动入视） | P2 | M | | |
| Q-002 | Core 查询质量三小项（排序一致性 / Words 首访竞态 / CJK 判定区间） | P2 | M | | |
| Q-003 | 构建器 CLI 与构建健壮性三小项（参数越界 / journal 半成品库 / --top 解析） | P2 | M | | |
| P-001 | package.sh 发布步骤注释纠正为数据解耦流程 | P2 | S | | |
| P-003 | 修复 WordNetThesaurus 后重建 dictionary.db 并发 data-2 | P2 | M | | B-001 |
| DOC-001 | 文档对齐：AGENTS.md 幽灵方法名 BuildLinkText + README 代理顺序表述 | P2 | S | | |
| B-014 | 单实例监听端 ReadLine 无超时，异常本地客户端可废 activate 通道 | P3 | S | | |
| B-015 | MacDockIcon 的 NSImage alloc 后未 release（一次性泄漏） | P3 | S | | |
| P-002 | 打包/发布脚本健壮性小项（8 处，2 项可选） | P3 | M | | |

## Done

| ID | Title | Priority | Done At |
|---|---|---|---|
