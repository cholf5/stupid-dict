# 词条一键复制 + 近义词可选中（2026-11-17）

## 背景

结果页每一块都是**独立的** `SelectableTextBlock`（词头、英音标、美音标、每条中文
释义、英文释义各一块），Avalonia 的选区不跨控件、⌘A 只作用于当前那一块——复制
一条完整词条要分 4–6 次选中。更糟的是近义词 / 反义词 / 联想词是 `Border` 包普通
`TextBlock`（点击热区需要），**连选中都做不到**；候选词 chip 是 `Button`，同理。

两个问题性质不同：一个是「要选好几次」，一个是「根本选不到」。

## 做了什么

### 1. 词条页一个「复制」chip（解决「选好几次」）

- 位置在词头行右侧（词头左、chip 右的 `Grid`），只在**有主词条**的英文结果页出现。
  中文查询页是多词条列表（term + 多个 headword + 其他词条 chips），整页复制出来
  是半张词典页，噪声大于价值——那里保持原样。
- 复制内容是**屏幕上能读到的整条词条**，纯文本、当前界面语言、不带 Markdown：

  ```text
  cat
  英 /kæt/  美 /kæt/
  n. 猫
  英文释义
  a small animal
  近义词
  n. tiger, lion
  联想词
  lion 狮子; tiger 老虎
  ```

- 细节取舍：
  - 词形提示（`cats → cat`）页面有就带上，粘贴物与所见一致。
  - 音标只带**有值**的那一侧；两侧都空时整行省略（页面为保持对称会显示裸「英」
    「美」，抄进笔记只是噪声——这是唯一「复制内容 ≠ 逐字照抄屏幕」的地方）。
  - 空段落整段省略（没有反义词就没有「反义词」这行）。
  - 近义 / 反义每组一行 `n. kitten, puss`；联想词一行 `word gloss; word gloss`，
    行尾不补分隔符（屏幕上每条后面都跟着 `;`，粘贴物里多余）。
  - 不带发音按钮、「你是不是要找」候选、其他词条 chips、翻译卡。
- 文本在渲染时就组装好（`BuildEntryCopyText`）并捕获进点击闭包，不依赖点击时的
  页面状态。
- 反馈走与翻译卡同一个 `CopyWithFeedbackAsync`：成功「已复制」、失败「复制失败」，
  1.5 s 复原；复原目标固定取「复制」文案，连点不会卡在确认态。

### 2. 近义词 / 联想词改成 `SelectableTextBlock`（解决「选不到」）

`CreateLinkSurface` 里的 `TextBlock` 换成 `SelectableTextBlock`。这带来一个冲突：
可选中文本会自己认领 `PointerPressed`（起选区），而原来的导航是**按下即查**——
两者一撞，任何拖动选词都会在按下瞬间跳走，选中等于不能用。

解法是把导航从 `PointerPressed` 移到 **`PointerReleased` + 位移阈值**：按下交给文本
块（拖动即选词），松开时若位移 ≤ 4px 才算一次「点击」并查词（`handledEventsToo`
让 Border 看得见被控件吃掉的这两个事件）。单击查词、拖选复制、双击查词三种手势
现在互不打架。

顺带踩到一个坑：`App.axaml` 里的链接样式原本是 `TextBlock.wordlink`，而 Avalonia 的
类型选择器是**精确类型匹配**——换成 `SelectableTextBlock` 后整排链接词立刻变回普通
文本（可点，但没有颜色和下划线）。样式改成 `:is(TextBlock).wordlink` 才继续命中，
测试用 `Assert.NotNull(link.TextDecorations)` 钉住。

## 评估过但不做

- **自动复制**（查完就写剪贴板）：静默覆盖用户剪贴板，代价最高。
- **Markdown / 富文本输出**：导出格式是导出功能；纯文本到哪儿都能用。
- **两个 chip（复制释义 / 复制全部）**：一次点击不值得给选择题。
- **快捷键**（⌘⇧C）：与选区复制语义打架，且这页面常有选区。
- **中文查询页也加 chip**：那页是列表不是词条，见上。
- **复制按钮进托盘迷你卡**：那张卡只有词头 + 前几条释义 + 发音，没有整页词条。

## 验证

- `HeadlessWindowTests.EntryCopyChipCopiesTheWholeEntryAsPlainText`：真实 headless
  剪贴板，逐行比对整条词条文本（含空段落省略）。
- `SynonymSectionsRenderAndClickLooksUp`（既有）：`MouseDown` + `MouseUp` 仍能点词
  导航——这条测试同时钉住了「按下不再导航，松开才导航」的回归。
- `SynonymLineKeepsCommaInsideWordEntry` / `RelatedWordsLineSeparatesWordGlossAndEntries`
  的断言从 `TextBlock` 收紧为 `SelectableTextBlock`。
- 结果页断言用 `Name = "ResultHeadword"` 找词头（词头现在在 `Grid` 里，不再是
  `ResultsPanel.Children[0]`）。
