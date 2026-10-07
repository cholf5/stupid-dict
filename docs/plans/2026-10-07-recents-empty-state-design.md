# 最近搜索条只在空态显示

日期：2026-10-07

## 背景与目标

最近搜索（`RecentPanel`，`MainWindow.axaml` Grid Row 3）只要有历史就永远可见：
`RefreshRecents` 只判断 `recent.Count > 0`，而 `RecentSearchStore` 固定容量 30 条，
WrapPanel 铺开成四五行 chip，把结果页（Row 2 的 `*` 行）挤到只剩小半屏。

目标：结果页空间全部还给查词结果，历史在合适的时机仍然触手可及，且不加任何设置项。

## 做了什么

历史条重新定位为**空态专属**（empty-state chrome）：

- `RefreshRecents` 可见性条件改为 `recent.Count > 0 && !ResultsPanel.IsVisible`，
  作为不变式兜底（无论谁在什么顺序调用都不会把历史条盖到结果页上）。
- `ShowEmptyState` 末尾调 `RefreshRecents()`：清空搜索框（Escape / ✕ 按钮 / 清空查询）
  回到空态时，历史条带着最新记录回来；构造函数里原有的独立 `RefreshRecents()` 调用
  因此删除。
- `RenderResult` / `RenderError` 显式 `RecentPanel.IsVisible = false`——这条必须有：
  前进/后退导航（`Navigate`）直接走 `RenderResult`，不经过 `RunSearch` 的
  `RefreshRecents`，只靠不变式兜不住这条路径。
- 护栏：`RecentList` 套 `<ScrollViewer MaxHeight="150">`（约三行 chip）。空态下窗口压到
  `MinHeight 440` 时，30 条历史也不会把居中的提示区挤没，超出部分滚动。
- 测试：`RecentSearchesAppearAfterLookup`（钉旧语义：查词后历史条可见）改写为
  `RecentSearchesHiddenOnResultsBackOnEmptyState`，钉住新语义——空态无历史不可见、
  结果页不可见但列表照常刷新、清空后可见。
- 显示/隐藏为瞬时 `IsVisible` 切换（`RefreshRecents` 与 `RenderResult`/`RenderError`
  直接赋值）。曾试过淡入淡出（`DoubleTransition` 动 Opacity + 代数令牌延迟折叠
  `IsVisible`），装上当天回滚，原因见「评估过什么」。

## 评估过什么、为何不做

- **历史条淡出动画**：试装当天回滚。Opacity 淡出要求 `IsVisible`（即 Row 3 的布局
  空间）在动画期间保持原状，而历史条在 Grid 里独立成行、不与结果页重叠——实际观感
  是结果页底部被一条不透明的空带压住 180 ms，动画结束行高回收、结果页突然弹开
  （用户反馈："背景挡死，然后突然消失"），"原地溶解"的前提不成立。抽屉式收起
  （动画高度/MaxHeight）只是把同一 180 ms 让位延迟做得平滑，且 Avalonia 不能过渡
  `GridLength`，需要自定义动画代码。结论：结果页接管应当瞬时，动画整体删除。
- **限行/限高单独作为方案**：结果页永久多占一行，滚动藏住大部分 chip，治标不治本。
  只保留 `MaxHeight` 作为空态下的护栏。
- **收进搜索框下拉（浏览器式浮层）**：零永久占位、最优雅，但要处理与现有建议下拉的
  互斥、浮层定位、键盘导航重排，等于把历史和输入补全两条链路合并；单纯为解决挤压
  不值得。若未来想统一两条链路再考虑。
- **砍 `MaxEntries`（30 → 更少）**：丢用户数据，Core 语义有测试钉住，单独做也不够。
- **加"显示/隐藏历史"设置项**：违反"新增设置项原则上禁止，优先自动决定"。
- 键盘语义不受影响：↑/↓ 本来就只在搜索框为空时跳进历史 chip（`OnSearchBoxKeyDown`），
  与"历史条只在空态出现"天然一致。

## 已知取舍

- 结果页上无法一键点回历史词——这是刻意交换：会话内回到之前词条走 ←/→
  （`LookupNavigator` 整页缓存），历史条的价值在"正要输入什么"的空态。
- 无词典（下载面板接管屏幕）时若已有历史，历史条仍会显示（沿用旧行为，未扩大改动面）。
