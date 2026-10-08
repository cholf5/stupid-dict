# Stupid Dict — 傻瓜词典

A stupidly simple offline English-Chinese dictionary for Windows, macOS and Linux.

**No account. No dictionary management. Just type and search.**

输入一个词，立即告诉你它是什么意思。不登录，不联网，不选词典；仅有的设置是日间 / 夜间主题和界面语言。

## 功能

- **英文 → 中文 + 英英**：词头、音标、词性、中文释义为主，英英释义为辅
- **英音 / 美音发音**：音标行旁的 `UK` / `US` 按钮即点即读，英音音标来自 ECDICT，美音音标由 CMUdict 转换；发音优先播放离线发音包（常用词预生成的真人级英/美双口音音频），没覆盖到的词自动回退 macOS/Windows/Linux 系统语音，全部离线完成
- **近义词 / 反义词 / 联想词**：查到的英文词下方给出按词性分组的近义、反义和常用联想词，词与中文注释里的英文词都是链接，单击直接查词（数据来自 WordNet，见「词典数据」）
- **中文 → 英文 + 英英**：输入中文词，直接给出对应英文单词和释义
- **输入即补全**：输入英文时按前缀列出候选词，输入的词本身是词条时永远置顶，`↑ / ↓` 选中、`Enter` 确认。1–2 个字母只列常用词（内存索引，微秒级），3 个字母起覆盖全部词头；`Enter` 不选中就永远查你输入的词
- **精确查询优先**：`Enter` 的语义是“查询我输入的词”，永远优先精确匹配，不会跳到模糊搜索认为“更相关”的词
- **最近搜索**：本地保存最近 30 条，去重、点击即查，没有管理界面
- **双击取词**：结果里的英文文本可直接双击，双击哪个单词就查哪个单词，连续点连续查；中文释义不响应（中文没有空格分词，命中无意义）
- **前进 / 后退**：搜索框旁的 `← / →` 在查询历史里前后翻，查过的词整页缓存，后退不重新查询、不写最近搜索
- **日间 / 夜间模式**：搜索框右侧的 `⚙`（或 `Ctrl/Cmd + ,`）打开设置，主题可选跟随系统、日间或夜间，选择保存在本地
- **中英界面**：界面语言可选跟随系统、简体中文或 English，切换立即生效、无需重启；词典释义本身不翻译，查什么显示什么
- **完全离线**：查询全部在本地完成，断网不影响任何功能

匹配顺序：`Exact → Normalized Exact → Word Form（cats → cat）→ Prefix → Fuzzy`。未命中时给出“你是不是要找”的建议，但绝不替你做决定。

## 键盘操作

| 操作 | 行为 |
| --- | --- |
| `Enter` | 查询输入的词；候选列表有选中项时查询该词 |
| `Esc` | 关闭候选列表；再按清空 / 返回搜索状态；设置窗口内关闭设置 |
| `Ctrl/Cmd + A` | 全选输入 |
| `Ctrl/Cmd + V` | 粘贴 |
| `↑ / ↓` | 浏览补全候选；输入框为空时进入最近搜索 |
| `Ctrl/Cmd + [` / `Ctrl/Cmd + ]` | 后退 / 前进（查询历史） |
| `Ctrl/Cmd + ,` | 打开 / 关闭设置 |
| `Ctrl/Cmd + K` | 聚焦搜索框 |

## 架构

```text
┌───────────────────────┐
│      Avalonia UI      │
└───────────┬───────────┘
            │
┌───────────▼───────────┐
│   DictionaryService   │
└───────────┬───────────┘
            │
   ┌────────┼────────────┐
   ▼        ▼            ▼
English→Chinese  English→English  Chinese→English
   └────────┼────────────┘
            ▼
       SQLite（word / zh_index / word_form）
```

数据与代码分离：UI 不认识词典格式，`StupidDict.Core` 内按方向拆分（`Dictionary/EnglishChineseDictionary`、`EnglishEnglishDictionary`、`ChineseEnglishDictionary`），历史记录独立（`History/RecentSearchStore`）。换词库只需重写数据构建器。

## 构建

需要 [.NET SDK](https://dotnet.microsoft.com/download)（net10.0）。

```bash
dotnet build
dotnet test                                    # 单元测试 + 无头 UI 测试
dotnet run --project src/StupidDict.App -f net10.0   # 运行（需要先构建词典数据）
```

## 词典与资源

应用是零配置的：查词永远在本地完成，词典和发音包要么随安装包携带，要么首次启动自动下载。

**资源查找顺序**（`dictionary.db` 和 `audio/` 都相同）：

1. 可执行文件同目录（打包分发时随包携带）
2. 用户数据目录（`~/Library/Application Support/StupidDict/`、`%APPDATA%/StupidDict/`）

**自动下载**：首次启动检测不到 `dictionary.db` 时，界面内出现下载面板，自动从 GitHub Releases 拉取预构建的 `dictionary.zip`（约 170 MB），带进度、可取消、支持断点续传；词典就绪后自动排队下载发音包 `audio-pack.zip`（约 570 MB）。下载链按序回退，直到成功：

1. GitHub 直连
2. 加速镜像前缀（`ghfast.top`、`gh-proxy.com`、`ghproxy.net`，内置于代码，失效可改）
3. 自动探测本机代理（环境变量 → macOS `scutil --proxy` 系统代理 → Clash/V2Ray/Surge 等常见本地端口探测）
4. 全部失败时提供「选择本地文件…」手动导入 `dictionary.zip` 或裸 `dictionary.db`

已安装后的数据管理走设置「数据目录」页签：显示词典与发音包的安装状态、打开数据目录。想重装数据，删掉数据目录里的 `dictionary.db` 或 `audio/` 再重启应用即回到下载流程；更新数据则从发布页下载新版 zip 手动导入。

查询功能始终离线，联网只发生在两件事上：首次下载词典与发音包，以及在设置里手动「检查更新」。

### 词典数据（从源码构建）

```bash
# 下载 ECDICT 的 SQLite 发布包（约 217 MB）
curl -L -o stardict.db.zip \
  https://github.com/skywind3000/ECDICT/releases/download/1.0.28/ecdict-sqlite-28.zip
unzip stardict.db.zip

# 下载 WordNet 3.0 数据库文件（近义词/反义词/联想词的来源，约 30 MB）
curl -L -o wordnet.zip \
  https://raw.githubusercontent.com/nltk/nltk_data/gh-pages/packages/corpora/wordnet.zip
unzip wordnet.zip

# 下载 CMUdict 0.7b（美音音标来源，约 3.5 MB）
curl -L -o cmudict-0.7b \
  https://raw.githubusercontent.com/Alexir/CMUdict/master/cmudict-0.7b

# 生成优化后的 dictionary.db（340 万词条 + 中文反向索引 + 词形映射 + 词库扩展 + 美音音标）
dotnet run --project src/StupidDict.DataBuilder -- \
  stardict.db --wordnet wordnet/wordnet --cmudict cmudict-0.7b
```

也支持 CSV 源：`dotnet run --project src/StupidDict.DataBuilder -- ecdict.csv`。省略 `--wordnet` 也能构建，但近义词/反义词/联想词板块为空；省略 `--cmudict` 则美音音标为空（应用照常工作，只是不显示美音）。旧 `dictionary.db` 缺少这些数据时应用自动降级，不做迁移。

### 发音包（构建一次，发布到 Releases）

发音包是按词频选出的常用词（默认 `--top 80000`，实际覆盖约 5.8 万词）× 英/美双口音的 MP3 集合，用 [Piper](https://github.com/rhasspy/piper)（本地离线神经 TTS，模型 `en_GB-alan-medium` / `en_US-lessac-medium`，MIT/开放许可）一次性生成，断点续跑：

```bash
pip install piper-tts          # 或 pipx；ffmpeg 需要 PATH 可用
curl -L -o en_GB-alan-medium.onnx \
  https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_GB/alan/medium/en_GB-alan-medium.onnx
curl -L -o en_GB-alan-medium.onnx.json \
  https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_GB/alan/medium/en_GB-alan-medium.onnx.json
curl -L -o en_US-lessac-medium.onnx \
  https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx
curl -L -o en_US-lessac-medium.onnx.json \
  https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/en_US-lessac-medium.onnx.json

dotnet run --project src/StupidDict.AudioPackBuilder -c Release -- \
  --model-uk en_GB-alan-medium.onnx --model-us en_US-lessac-medium.onnx
# 产出 audio-pack/ 目录 + audio-pack.zip + audio-pack.zip.sha256（约 2 小时，可中断续跑）
```

生成是全本地的，没有云端调用。HuggingFace 不可达时模型可从 `hf-mirror.com` 镜像下载（把域名替换即可）。

### 打包与发布

```bash
scripts/release.sh 1.0.1 --watch    # bump 版本 → 打 tag → CI 三平台打包并建 Release
```

发版脚本把 `StupidDict.App.csproj` 的 `<Version>` 提升到目标版本并打 `v` tag，CI（`.github/workflows/dotnet-desktop.yml`）随后测试、打包 macOS（.app，arm64/x64）/ Windows / Linux 应用包并创建 Release；`--watch` 会等 CI 跑完并核对产物齐全。版本必须与 csproj 一致，CI 在 tag 时强制校验，in-app 更新检查也以它为比较基准。

数据资产（`dictionary.zip` / `audio-pack.zip`）与 App 版本解耦：它们放在独立的 **prerelease** `data-1`（对应代码里的 `ReleaseAssets.DataTag`），一次发布、基本不再动，应用内下载 URL 钉在该 tag 上（加速镜像前缀对其同样适用）。打 prerelease 标记是刻意的：prerelease 永远不参与 `releases/latest` 竞争，而应用内「检查更新」读的正是那个页面，必须始终指向 App 版本。App 发版不携带、也不需要这两件资产。若将来数据要更新：发布 `data-2`，把 `ReleaseAssets.DataTag` 提到新 tag，随下一个 App 版本生效。

首次发布数据资产（本地打包后一次上传）：

```bash
scripts/package.sh    # 产出 dist/dictionary.zip + dist/audio-pack.zip（含 .sha256）
gh release create data-1 dist/dictionary.zip dist/dictionary.zip.sha256 \
  dist/audio-pack.zip dist/audio-pack.zip.sha256 --prerelease \
  --title "数据资源包（词典 + 发音包）" \
  --notes "词典数据库与离线发音包；应用首次启动自动下载，与 App 版本号无关。"
```

本地打包（调试，或首次发布 / 更新数据资产时用）：

```bash
scripts/package.sh                                        # 本机平台
scripts/package.sh --rids "osx-arm64 osx-x64 linux-x64 win-x64"
```

macOS 首次打开未签名 `.app` 被拦截时：`xattr -cr "Stupid Dict.app"`。

## License

**Code**
MIT — 见 [LICENSE](LICENSE)

**Dictionary Data**
MIT（以 ECDICT 上游仓库对其代码与数据的整体授权声明为准）

**Dictionary Data Source**
[ECDICT](https://github.com/skywind3000/ECDICT)（发布版 `1.0.28`），由该上游项目汇总自公开语料（cdict、WordNet、BNC 词频等）。

**Thesaurus Data**
[WordNet 3.0](https://wordnet.princeton.edu/)（Princeton University），按其许可声明使用。近义词取自同义词集（synset）共现词、名词/动词的上位词与形容词的 similar-to 卫星集，反义词取自反义指针指向的同义词集；联想词为前两者按语料词频排序的前 10 个，中文注释取自词条本身的释义。

**US Phonetics Data**
[CMUdict 0.7b](https://github.com/Alexir/CMUdict)（CMU，BSD 风格许可），由 `StupidDict.DataBuilder` 内置的 ARPAbet→IPA 映射转换为美式音标。

**Pronunciation Audio**
由 [Piper](https://github.com/rhasspy/piper)（MIT）及其 [voices 模型](https://github.com/rhasspy/piper-voices)（`en_GB-alan-medium`、`en_US-lessac-medium`，随模型仓库的开源许可）在本地离线生成，生成脚本随源码分发。

> 注意：代码的 MIT 许可不自动延伸到词典数据。词典数据的再分发以上游 ECDICT 仓库的授权声明为准；如你的分发场景需要更严格的授权确认，请先核实上游声明。

## 明确不做的功能

登录、注册、云同步、生词本、课程、词典选择、AI、在线翻译、在线发音（发音只用离线音频包和系统语音）、划词、浏览器插件、移动端、广告、订阅——都不做。

设置只有两项：外观主题（跟随系统 / 日间 / 夜间）和界面语言（跟随系统 / 简体中文 / English）。这曾是刻意不做设置界面的项目，但夜间阅读是离线词典的真实需求，英语母语者学中文也需要英文界面，于是开了这两个例外。原则不变：以后再出现必须配置的东西，优先自动决定，而不是增加设置项。
