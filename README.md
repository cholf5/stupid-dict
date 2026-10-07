# Stupid Dict — 傻瓜词典

A stupidly simple offline English-Chinese dictionary for Windows, macOS and Linux.

**No account. No settings. No dictionary management. Just type and search.**

输入一个词，立即告诉你它是什么意思。不登录，不联网，不选词典，不配置任何东西。

## 功能

- **英文 → 中文 + 英英**：词头、音标、词性、中文释义为主，英英释义为辅
- **中文 → 英文 + 英英**：输入中文词，直接给出对应英文单词和释义
- **输入即补全**：输入英文时按前缀列出候选词，输入的词本身是词条时永远置顶，`↑ / ↓` 选中、`Enter` 确认。1–2 个字母只列常用词（内存索引，微秒级），3 个字母起覆盖全部词头；`Enter` 不选中就永远查你输入的词
- **精确查询优先**：`Enter` 的语义是“查询我输入的词”，永远优先精确匹配，不会跳到模糊搜索认为“更相关”的词
- **最近搜索**：本地保存最近 30 条，去重、点击即查，没有管理界面
- **完全离线**：查询全部在本地完成，断网不影响任何功能

匹配顺序：`Exact → Normalized Exact → Word Form（cats → cat）→ Prefix → Fuzzy`。未命中时给出“你是不是要找”的建议，但绝不替你做决定。

## 键盘操作

| 操作 | 行为 |
| --- | --- |
| `Enter` | 查询输入的词；候选列表有选中项时查询该词 |
| `Esc` | 关闭候选列表；再按清空 / 返回搜索状态 |
| `Ctrl/Cmd + A` | 全选输入 |
| `Ctrl/Cmd + V` | 粘贴 |
| `↑ / ↓` | 浏览补全候选；输入框为空时进入最近搜索 |
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
dotnet run --project src/StupidDict.App       # 运行（需要先构建词典数据）
```

## 词典数据

应用启动时按顺序查找 `dictionary.db`：

1. 可执行文件同目录（打包分发时随包携带）
2. 用户数据目录（`~/Library/Application Support/StupidDict/`、`%APPDATA%/StupidDict/`）

词典数据不随源码分发，用构建器从上游数据生成（一次性，几分钟）：

```bash
# 下载 ECDICT 的 SQLite 发布包（约 217 MB）
curl -L -o stardict.db.zip \
  https://github.com/skywind3000/ECDICT/releases/download/1.0.28/ecdict-sqlite-28.zip
unzip stardict.db.zip

# 生成优化后的 dictionary.db（340 万词条 + 中文反向索引 + 词形映射）
dotnet run --project src/StupidDict.DataBuilder -- stardict.db
```

也支持 CSV 源：`dotnet run --project src/StupidDict.DataBuilder -- ecdict.csv`。

## License

**Code**
MIT — 见 [LICENSE](LICENSE)

**Dictionary Data**
MIT（以 ECDICT 上游仓库对其代码与数据的整体授权声明为准）

**Dictionary Data Source**
[ECDICT](https://github.com/skywind3000/ECDICT)（发布版 `1.0.28`），由该上游项目汇总自公开语料（cdict、WordNet、BNC 词频等）。

> 注意：代码的 MIT 许可不自动延伸到词典数据。词典数据的再分发以上游 ECDICT 仓库的授权声明为准；如你的分发场景需要更严格的授权确认，请先核实上游声明。

## 明确不做的功能

登录、注册、云同步、生词本、课程、词典选择、设置界面、AI、在线翻译、TTS、划词、浏览器插件、移动端、广告、订阅——都不做。

没有 Settings。如果以后真的出现一个必须配置的东西，优先考虑自动决定，而不是增加设置项。
