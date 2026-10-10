# 发音包改单文件 SQLite（audio-pack.db）设计

- **日期**：2026-10-10
- **状态**：已实施
- **背景**：发音包解压后是 ~11.6 万个小 MP3（~5.8 万词 × 双口音，zip 约 570MB）。散文件形态带来三类真实成本：①安装/卸载要写/删十几万文件（Windows 上杀软逐文件扫描是主要耗时）；②备份/同步等第三方工具扫盘慢；③Windows 保留设备名（con.mp3）一类文件名语义问题（2026-10-09 pitfall，2026-10-10 已用映射收尾）。评估过「游戏式打包」（同日会话）：三平台播放器（afplay/MCI/mpg123）全部只认文件路径且进程外，pak 要发声必须解临时文件；用户随后提出 SQLite 方向，实测后采纳。

## 实测依据（/tmp/storeprobe 探针，116k 条目 × 5KB blob 合成）

- zip 就地读：中央目录解析 + 条目对象图一次 ~5-20ms、托管内存 ~70MB 常驻（ZipArchive 惰性解析，缓存句柄 + 自建字典才行）；每次播放查询 ~0.01ms。
- SQLite 单 db：**每次播放全新只读连接（Pooling=false）+ 点查 + 写临时文件合计 ~1-1.5ms**，零常驻状态、零句柄生命周期管理。
- 结论：SQLite 赢在生命周期而非查询速度——更新/替换数据时没有「先关句柄再换文件」的时序问题。

## 决策

1. **单 db，不按字母拆**：`CREATE TABLE audio(word TEXT PRIMARY KEY, uk BLOB, us BLOB) WITHOUT ROWID`，词头小写（与查找端 `ToLowerInvariant` 一致）。每播放一次开新只读连接实测 ~1ms，拆 26 个 db 省不出可测量收益，反引入 26 倍的下载/导入/更新流程复杂度。
   - **页大小保持 4KB 默认（实测钉死，勿"优化"）**：db 715MB 比旧 zip 570MB 大 ~25%，疑似页浪费可压——真实数据全曲线实测否决：4KB 715MB、8KB 800MB、16KB 1112MB、32KB 2068MB、64KB 834MB。SQLite 的 local/overflow 余数算术惩罚中等页大小，参差的 blob 尺寸（每词 3-15KB 不等）让大页装箱碎片化；均匀 blob 的合成模型会给出完全错误的结论（64KB 合成 631MB、真实 834MB），必须用真数据测。裸 db vs zip 的差值（+21%）是单文件随机访问的形态成本；对 db 文件再套 zip/zstd 无意义——MP3 是已压缩数据，旧 zip 也只从 612.8MB 压到 593.6MB（3%）。真正的压缩杠杆是音频本身重编码（Opus 可省 30-50%），但 afplay/MCI 不支持 Opus，那是进程内播放项目的门。
   - **转换是确定性的**：同输入 zip 同代码产出逐字节一致（重建 sha 与已发布资产比对相同），重建资产无需担心漂移。
2. **资产格式**：最初按「应用内转换、不发新数据资产」实施（data-2 的 zip 原样可用）；发布前用户决策改为**直接发预构建 db**——产品尚无 1.0 正式版、全是 PreRelease，没有兼容包袱。data-3 = `dictionary.zip`（与 data-2 同字节搬运）+ `audio-pack.db`（715MB）+ 各自 `.sha256`，`ReleaseAssets.DataTag` 提至 `data-3`、`AudioPackAsset` 改为 `audio-pack.db`，下载流程按扩展名分派：`.db` 走 `ImportAudioPack` 的验证+落位分支（integrity_check + audio 表非空），`.zip` 走转换分支（保留给手动导入旧格式）。老 App（≤0.1.2）钉 data-2 的 zip，互不干扰。
   - **db 资产的生成**：反射 harness（临时工程 ProjectReference App + 反射调用 internal `AudioPackConverter.ConvertZipToDatabase`）跑 data-2 的 zip，产出经三重验证：`integrity_check` ok、57,784 行、全量 115,568 个 MP3 条目逐字节对账零差异（zip 里另有 `manifest.txt` 185 字节，非发音包条目，转换器正确跳过）。下次重新生成数据：复用同法，或届时给 AudioPackBuilder 加 `--from-zip` 模式。
3. **查找优先级：db 优先，散文件兜底**。旧用户（v0.1.x 已解压散文件）无 db、照常工作；手动导入 db 的用户（含散文件老用户）导入即生效。散文件路径保留 2026-10-10 的保留名映射 + 裸名回退，作为遗留布局兼容，随散文件形态退役。
4. **播放仍经临时文件**：db 取 blob → 写 `$TMPDIR/stupiddict-audio-{pid}-{n}.mp3` → 现有播放器原样播该路径。临时文件名固定模式（不含词头），**保留名在主路径上不存在**。单播放者假设是既有事实（一个 MCI alias、一个进程），临时文件只跟踪当前一个：写新的前 best-effort 删上一个、`Stop()` 时 best-effort 删——`ProcessPlayer` 发射后不管（Kill 在下次 Play 起）、MCI 持句柄到 close，所以删除时机在「下次写入前」与「Stop 后」，删不掉（句柄未释放）就留给 OS 临时目录清理，吞 IO 异常与 Stop 的 best-effort 风格一致。
5. **手动导入双收**：`.db`（只读连接跑 `PRAGMA integrity_check` 验证后复制进数据目录，不移动用户原文件）；`.zip`（走同一转换器）；其他扩展名报 `ImportUnsupportedFormat`。选择器过滤加 `*.db`。
6. **完整性**：转换时逐条 CRC-32 手工校验（复用 `Crc32`，与 `ExtractEntry` 同款——ZipArchive 读条目不验 CRC，B-008），CRC 不符抛 `InvalidDataException` → 下载流程既有的「损坏 purge 重下」分类照常工作。手动导入 db 用 `integrity_check` 兜底。

## 不做（YAGNI 留痕）

- **进程内播放**（blob→解码→音频输出，省掉临时文件一跳）：Linux 无保底进程内音频栈，三平台原生绑定是大项目；现有临时文件一跳成本 ~1ms，等真实需求再立项。
- **启动清扫残留临时文件**：最多遗留一两个小文件在 OS 临时目录，系统自清。
- **db 侧增量更新 / per-letter 分区**：数据资产整体替换的发布模式没变。
- **散文件迁移**（自动把旧散文件转成 db 或删除）：旧布局照常工作，不折腾用户磁盘上的既有数据；散文件支持整体退役时一并处理。

## 改动面

- `Speech/AudioPackStore`（新）：`IAudioPackStore { TryGetAudioFile(word, accent, out file); Cleanup(); }`——db 查询 + 临时文件 + 散文件兼容解析（映射/裸名），全部失败返回 false（TTS 接管），绝不抛进播放路径。
- `Speech/AudioPackPlayer`：改持 `IAudioPackStore`，`Stop()` 在文件播放器 Stop 后调 `store.Cleanup()`。
- `Assets/AudioPackConverter`（新）：`ConvertZipToDatabase(zip, dbPath, progress, cancellation)`——校验 uk//us/ 存在、逐条 CRC、单事务 INSERT、staging（`audio-pack.db.building`）+ 原子进位、条目级进度（256 步进，与解压同款）。
- `MainWindow`：`AudioPackInstalled` = 散文件 uk/ 存在 **或** db 存在；下载流程解压步换转换步（B-009 完成序不变）；`ImportAudioPack` 按 `.db`/`.zip` 分派；选择器过滤加 `*.db`。
- `AppLocations`/`AppPaths`：新增 `AudioPackDatabasePath`（= 数据目录下 `audio-pack.db`）。
- `SettingsWindow` 数据目录页签：安装状态同语义（db 或散文件任一）。
- `ExtractZip` 及其全部机制对**词典 zip 原样保留**；音频包不再走解压（保留名映射 `ReservedDeviceNames` 保留：散文件兼容路径 + 词典 zip 的防御性落盘）。
