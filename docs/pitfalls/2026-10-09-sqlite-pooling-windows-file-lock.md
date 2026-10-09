# SQLite 连接池让文件在 Dispose 后仍被占用：Windows 本地 7 个测试全红

- **状态**：已修复（新增 `StupidDict.Core/SqliteConnections.cs`，三处开连接点改走 `Pooling = false`），回归验证：`dotnet test` 在 Windows 本地 Core.Tests 41/41、App.Tests 全绿
- **日期**：2026-10-09
- **背景**：在 Windows 本机跑 `dotnet test StupidDict.slnx`，Core 4 个 + App 3 个测试失败，报错全是同一句：

```
System.IO.IOException : The process cannot access the file 'history.db' because it is being used by another process.
System.IO.IOException : The process cannot access the file '…\release\dictionary.db' because it is being used by another process.
```

失败清单：`RecentSearchStoreTests`（4）、`DownloadFlowTests.ChecksumMismatchPurgesArtifactsAndRedownloadsOnce`、`DownloadFlowTests.ResumedDownloadAttemptIsLabeledAsResume`、`HeadlessWindowTests.DictionaryDownloadCompletesAndEnablesLookup`。CI（ubuntu）与本机 macOS 一直是绿的。

## 根因

`Microsoft.Data.Sqlite` **默认开启连接池**：`SqliteConnection.Dispose()` 只是把连接还给池，底层文件句柄继续留在进程里（共享模式含 `ReadWrite`）。Windows 的共享检查是双向的——已有句柄带写权限时，任何以 `FileShare.Read` 打开同一个文件的操作一律共享冲突：

- 测试清理 `Directory.Delete(dir, recursive: true)`（`RecentSearchStoreTests.Dispose`）
- 测试助手 `ZipFile.CreateFromDirectory`（内部 `File.OpenRead` 源文件）

Unix 同时忽略共享模式、又允许 unlink 正在使用的文件，所以这两个平台必然全绿：**绿灯平台不作证**。

## 修复

新增 `src/StupidDict.Core/SqliteConnections.cs`，把「开本地 SQLite 文件」收敛成一个工厂，连接串钉死 `Pooling = false`；三处开连接点改走它：`DictionaryDatabase.OpenRead`（只读）、`DictionaryDatabase.Create`、`RecentSearchStore` 构造器。

选产品侧而不是在测试里调 `SqliteConnection.ClearAllPools()`，理由：

1. 桌面应用每个库只有一个长生命周期连接（词典读连接 + 历史库），池化收益为零；
2. 应用自己就会**替换和重读**这些文件（首次下载落 `dictionary.db`、手动导入、热重建 `DictionaryService`），`DictionaryDatabase.Create` 开头那句 `File.Delete(path)` 同样会被池化句柄挡住——这是真实产品路径上的隐患，不只是测试问题；
3. 一处设对，以后新增连接自动继承，不用在每个测试清理处记得清池。

## 教训

1. **「文件被另一个进程占用」先怀疑自己没放句柄，而不是杀毒软件**：托管 API 的 `Dispose` 不等于句柄释放——连接池、`FileStream` 包装流、`ZipArchive` 都可能在 Dispose 后留下底层句柄。
2. **跨平台测试的隐性假设要显式列出来**：Windows 有共享模式检查、不允许删/改名在用文件；Unix 两样都没有。凡测试涉及「用完删掉」「把文件压进 zip」「原子替换」都要假设 Windows 会拒绝。
3. **修在契约层而不是调用点**：7 个失败散落在 3 个测试类，逐个加 `ClearAllPools()` 只是把知识复制 3 份；在开连接的地方统一关掉池化，才是把约束固定在唯一入口。
