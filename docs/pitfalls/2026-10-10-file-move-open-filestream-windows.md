# `File.Move` 撞未释放的 `FileStream`：Windows 下载「100% 后归零无限循环」

- **状态**：已修复（`AssetDownloadService.DownloadFromAsync` 写入流作用域收窄到移动之前），回归验证：`dotnet test` Windows 本机 Core 83/83、App 201/201 全绿（修前 `AssetDownloadServiceTests` 恰 4 红）
- **日期**：2026-10-10
- **背景**：用户实测 Windows 首启下载词典——进度到 100% 后从 0 重来，反复循环永不成功；手动导入 zip 正常。macOS 开发机与 ubuntu CI 全程无此症状。

## 根因

`DownloadFromAsync` 的写入流与进位移动在同一个 using 作用域里：

```csharp
await using var target = new FileStream(partFile, append ? FileMode.Append : FileMode.Create);
// …读/写循环…
File.Move(partFile, destinationFile, overwrite: true);  // target 还开着
```

`FileStream(path, FileMode)` 默认 `FileShare.None`（独占锁）。Unix 允许 rename 打开中的文件，Windows 拒绝——`File.Move` 抛 IOException「另一个进程正在使用此文件」（本机 PowerShell 实证）。于是**每次其实已经下载完整**的尝试都在最后一步被判失败，再被源回退链放大成循环：

1. 源 A 下载 0→100%，读循环正常结束，`File.Move` 抛占用异常 → 尝试失败，完整 `.part` 留盘（异常退栈时才释放句柄）；
2. 换源 B：按 `.part` 全长发 `Range: bytes=<全长>-` 续传 → 服务器回 416 → 代码删 `.part` 判失败；
3. 换源 C：从 0 重新下载到 100% → `File.Move` 又失败 → 下一源 416 → 再从 0……

回退链 = 4 源 × 平台默认/直连两相位 + 检测到的代理 × 2 源，约 5 次完整的 174MB 全量下载才耗尽抛「所有源均失败」——界面上就是 100%→0→100% 无限循环。手动导入走 `ExtractZip`/`File.Copy` 不经过 `File.Move`，故正常。

## 修复

写入流收窄为语句级 using，读/写循环结束即释放句柄，再做进位移动；代码内注释钉住跨平台语义差异（Windows 拒绝 rename 在用文件，Unix 允许）。

## 为什么测试没拦住

`AssetDownloadServiceTests` 有 4 个用例完整走「下载到底 → move → 读回产物」路径（`StalledBody…`、`MisalignedContentRange…`、`AlignedContentRange…`、`OkResponse…`），但套件只跑过 macOS 本机与 ubuntu CI——rename 在用文件全合法。本次在 Windows 本机跑同一套件，修前恰这 4 红（错误即生产同款「所有下载源都失败了」），修后 23/23 绿：**现有用例天然就是回归测试，无需新增，但必须在 Windows 上跑才算数**。

## 教训

1. **「写完就 move/rename/delete」的文件，写句柄必须先出作用域**——与 sqlite-pooling 篇同族：Dispose 时机在 Unix 上无感、在 Windows 上是硬约束。凡 `await using var` 包住后续文件操作的写法都要检查作用域边界。
2. **绿灯平台不作证**（再次）：这个 bug 在 macOS + ubuntu CI 下通过了全部 284 个测试与 v0.1.x 两次发版，首次真实 Windows 分发才暴露——和封签篇的 Gatekeeper damaged 一样，属「发版链缺陷只在目标平台实测暴露」类。
3. **下载链路的故障形态会掩盖根因**：占用异常 → 尝试失败 → 416 删 part → 全量重下，用户看到的是「进度条循环」而非任何与文件锁相关的文案；排障时先问「哪一步让 100% 的尝试被判失败」，而不是先怀疑网络/镜像。
