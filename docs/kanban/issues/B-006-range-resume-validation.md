---
id: B-006
title: Range 续传无 If-Range/Content-Range 校验，换包可拼出损坏 zip
type: bug
priority: P2
size: S
status: done
created: 2026-10-09
updated: 2026-10-09
blocked: none
---

## Goal

跨版本的断点续传不再产出拼接损坏的 zip：服务器返回的 206 必须验证起始偏移，不符则丢弃 `.part` 从零下载。

## Background

2026-10-09 扫描确认（`Assets/AssetDownloadService.cs:146-159`）：

- `resumeFrom > 0` 时带 `Range` 头，收到 206 即按「服务器内容与 `.part` 同源」续接（`append = statusCode == PartialContent && resumeFrom > 0`），未带 `If-Range`/ETag，也未比对 `Content-Range` 首字节是否等于 `resumeFrom`。
- 仓库已有「远程换包」真实前例（data-1 → data-2 语义）。残留 `.part` + 远端已换包 → 206 把两版字节拼在一起，成品 zip 损坏。
- 下游缓解：SHA-256 校验能捕获并 purge 重下（MainWindow 侧 purge-once 自愈）；但 checksum 拉不到时（离线、或 B-007 的格式不识别）损坏 zip 直接落盘，并接入 B-008 的复用通道。

## Acceptance Criteria

- [x] 206 的 `Content-Range` 首字节偏移 ≠ `resumeFrom` 时，删 `.part` 从零重下（或视作该源失败进回退链）
- [x] 正常 206 续传、200 忽略 Range、416 清 `.part` 三个既有路径行为不变

## Subtasks

- [x] 解析 `Content-Range` 并校验起始偏移
- [x] 不符路径的处理与测试
- [x] （评估，可缓）If-Range/ETag 缓存——远端 checksum 通常已兜，留痕决定做/不做 → **评估后不做**，留痕见 Development Log

## Dependencies

- none（与 B-007、B-008 同链路但可独立修）

## Test Cases

### TC-001: 错位 Content-Range 触发重下

Steps:
1. ScriptedDownloader 返回起始偏移 ≠ resumeFrom 的 206
2. 跑续传路径

Expected:
- `.part` 被丢弃，从零重下（或进回退链），最终产物完整

Result: 通过（`MisalignedContentRangeDiscardsPartAndRestartsFromZero`）。预设 8 字节 `.part`，首请求 `Range: bytes=8-`、响应 `206` + `Content-Range: bytes 0-99/100`（从服务器自身字节零起的焊接口径）；断言修复前实测红（产物以陈旧 `.part` 字节 0x11,0x22,… 开头——盲拼接 bug 被钉住），修复后：删 `.part` 同源从零重发（第 2 请求无 Range）、成品为完整 fresh 字节、`.part` 无残留。

### TC-002: 正常续传回归

Steps:
1. 既有续传测试全量回归

Expected:
- 全绿

Result: 通过。新增 `AlignedContentRangeStillAppendsPart`（8 字节 `.part` + `Content-Range: bytes 8-107/108` 仍追加拼接、单请求）与既有路径回归 `OkResponseIgnoresRangeAndRestartsFromZero`（200 时忽略 Range、FileMode.Create 从零）+ `RangeNotSatisfiableDeletesPartAndExhaustsChain`（416 删 `.part`、attempt 失败进链、全源耗尽抛 AllSourcesFailed）；全套 163/163 绿。

## Development Log

修法（`AssetDownloadService.DownloadFromAsync`）：响应头处理重排为小循环（每源最多两次请求）。

- `resumeFrom > 0` 改记为 `tryResume`；206 且 `ContentRangeStartsAt(response, resumeFrom)` 不满足时（含 `Content-Range` 缺失、unit 非 `bytes`、`*/*` 不可满足形态——一律视为不可验证、不可信任）→ 删 `.part`、`resumeFrom = 0`、无 Range 重发一次；第二次迭代 `tryResume` 已为 false，错位分支天然不可达，无死循环风险；再异常则按普通 attempt 失败进回退链。
- 200（任何非 206 成功）忽略 Range → 从零重下（既有语义）；416 删 `.part` + 抛错（既有语义原样保留）。
- 纯函数 `ContentRangeStartsAt`：`ContentRange is { Unit: "bytes", From: long from } && from == offset`。

**If-Range/ETag 评估：不做。** 理由分层防御：①状态化服务器「换包后按请求偏移续新内容」的焊接（`Content-Range` 校验天然测不到——206 的 From 合法等于 resumeFrom），发布 checksum 时被既有 purge-and-redownload-once 自愈链兜住，这是 data-1→data-2 换包的设计防线；②checksum 不可得时（离线/格式不识别），B-008 本次给复用链补上 CRC 失败 purge，焊接产物即使落盘也会在解压处自愈，不再锁死用户；③若真做 If-Range 需要跨会话持久化 validator（`.part` 旁挂 meta 文件）+ 逐源维护，是新的持久化面，对上述兜底的边际收益小。留痕：若日后「无 checksum 运行」成为常态再重评。

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/StupidDict.App/Assets/AssetDownloadService.cs`
- How to run/verify: `dotnet test StupidDict.slnx`
- Results: 2026-10-09 全套通过——Core 45/45，App 164/164（最终计数，含审查收尾的降级测试）（含本卡新增 4 条：1 条修复前实测红 + 3 条回归），build 0 警告 0 错误。
