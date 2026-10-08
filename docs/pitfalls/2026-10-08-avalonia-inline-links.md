# Avalonia 11.3 流式文本内嵌控件（InlineUIContainer）三个坑

- **状态**：已绕开，结论沉淀于 `MainWindow` 的 `CreateLinkSurface`/`BuildWordLinksLine`/`BuildRelatedWordsLine` 与 AGENTS.md「UI 约定」
- **日期**：2026-10-08
- **相关提交**：8240988（第一次修法，错了）、0cd4800（联想词行 WrapPanel）、87734ff（近义/反义行 WrapPanel + 死代码清理）

## 背景

词库三行（近义词、反义词、联想词）的词链接最初是"一个会换行的 SelectableTextBlock，链接词用 `InlineUIContainer` 包 Border 内嵌"。选 InlineUIContainer 是因为多 run SelectableTextBlock 的字符级命中测试不可靠（点击落到邻字、偶发 GlyphRun 内部异常），Border 做整词点击面。这次修"联想词没空格"的三轮迭代里，把这套结构的三个坑全部踩实了。

## 坑 1：换行点落在 InlineUIContainer 上 → 不换行，原位溢出被视口裁掉

- **症状**：链接词整个消失，相邻文本照常渲染。用户实测：`nation 国家，民族; 陆地，地面;`——`land` 的释义在，词没了。headless 复现：`government` 被裁成 `governme` 贴在右缘，释义另起一行。同一个 bug 的两种表现（全裁/裁一半），取决于溢出量。
- **触发**：行尾剩余宽度容不下下一个 InlineUIContainer 时，Avalonia 11.3 的换行器不把它推到下一行，而是按"能放下"处理并原位渲染，超界部分被裁。
- **隐蔽性**：控件还在可视树里（`GetVisualDescendants()` 找得到、文本断言全过），**只有渲染是错的**——结构测试永远测不出来。
- **触发条件依赖窗口宽度**，复现要凑换行点：种 9 个联想词条、窗口 780 宽（默认宽度！）即可稳定命中。

## 坑 2：普通 Run 直接夹在两个 InlineUIContainer 之间 → 字形画低约一行

- **症状**：该 Run 的宽度正常占位（词间距是对的），但字形被画到下一行顶部——近义/反义行的逗号集体"出逃"，词与词之间只剩空隙，行间散落小点。
- **关键限定**：必须是 Run **两侧都是** InlineUIContainer。`[链接][CJK 释义 Run][";" Run][链接]` 没事（每个 Run 至少一边贴着普通 Run）；`[链接][", " Run][链接]` 必挂。全库只有近义/反义行有这个形状，所以是老 bug 却直到联想词修完才被看见。
- **与坑 1 无关**：不换行的单行也会触发，两个独立缺陷。

## 坑 3：挨着 CJK 文本的空格整形远宽于拉丁空格

- **症状**：`" " + 释义` 里那个空格渲染出来约是拉丁空格的两倍宽（15px 字号下量得 ~12px vs ~6px）。
- **拆分无用**：把空格拆成独立 Run（Latin 上下文）和放进 CJK Run 里像素级完全一致——两种分段都躲不开变宽的整形上下文。
- **绕法**：控件间距不用空格字符，用 margin（词-释义 4px、分隔符-下词 5px），跨字体稳定。

## 排查方法论（这次真正的教训）

1. **眼测截图会骗人，两次**：第一次把全角逗号自身的字面留白当成"超宽空格"；第二次把间距问题当主因，真凶是丢词。最后全靠按颜色分簇量像素（蓝=链接、灰=文本，逐簇算空隙宽度）才定位——对渲染问题，量测优先于目测。
2. **headless 复现生产渲染是可靠的**：`UseHeadlessDrawing=false + UseSkia()` 的 TestAppBuilder 造一个种好数据的 `MainWindow`，三个坑全部在 headless 里原样复现（含 macOS 生产截图里的全部症状），不用真装词典。
3. **结构测试测不出渲染 bug**：坑 1 的词在可视树里完好无损。回归测试只能钉住"防再犯"的结构（WrapPanel 原子词条、逗号与词同块、间距 margin>0），症状本身按仓库约定不做像素断言（截图仅 `File.Exists` 供人工目检）。
4. **验收渲染改动要扫换行边界**：第一次修法（加空格）单看一行没问题，恰恰是空格制造了新的换行机会，把坑 1 从潜伏里暴露出来——"改完后更糟糕"就是这么来的。改文本布局，必须多拉几个窗口宽度看。

## 最终格局

三个区块统一为 `WrapPanel` 按词条整块换行：每个词条是一个横向 `StackPanel`（`CreateLinkSurface` 的 Border 点击面 + 释义/分隔符的 `SelectableTextBlock`，共用 `ThesaurusText`），换行只发生在词条之间，两个换行 bug 都够不着；间距一律 margin。流式文本链接的旧机器（`LinkSegment`/`BuildLinkText`/`PosLineSegments`/`CreateLinkInline`）已删净，别再往回写。
