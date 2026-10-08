# Avalonia 11.3 ScrollViewer.Padding：extent 扣掉 padding、viewport 不扣，底部永远滚不到

- **状态**：已修复（padding 挪到内容 `Margin`），回归测试 `ResultsScrollReachesBottom`（AvaloniaTheory 三宽度）
- **日期**：2026-10-08
- **背景**：用户报告「滚动条滚不到最底下」——查词结果页滚到底后，联想词最后一行被裁掉半行（词库区块上线后结果页第一次真正可滚动，bug 才可见；`Padding="26,10,26,16"` 从 MVP 第一版就挂在结果区 ScrollViewer 上，不是词库 WrapPanel 改造的回归）。

## 症状与根因

滚动条推到最大，内容底部仍有约 26px 永远看不见：最后一行整行消失、上一行被裁一半——正是 ScrollViewer 上下 padding 之和（10+16）。

Avalonia 11.3 的 ScrollViewer 模板把 `ScrollViewer.Padding` 转发给 `ScrollContentPresenter.Padding`，而 presenter 的两个量各算各的：

- **Extent 把 padding 扣掉了**：内容排到 433px，`Extent = 407 = 433 − 26`；
- **Viewport 不扣**：`Viewport = 384`（presenter 整个 bounds，含 padding 区域）。

于是 `最大 Offset = Extent − Viewport = 23`，而内容实际需要滚 26px；滚到底后内容底部渲染在视口底缘之下，被 presenter 裁掉。headless 复现数据（780×460 窗口）：`Extent=407, Viewport=384, Offset_max=23`，内容最低点渲染在 sv 坐标 420 > presenter 底 384。

## 修复

内边距写在内容上，不写在 ScrollViewer 上（`MainWindow.axaml` 结果区）：

```xml
<ScrollViewer Grid.Row="2">
  <Panel Margin="26,10,26,16">
```

内容自身的 `Margin` 是 extent 计算的一等公民（presenter 的 `ComputeExtent` 会 inflate child margin），extent 覆盖全部内容，最大 offset 直达底部。修复后实测：`Extent=459`（内容 433 + margin 26），滚到底内容最低点 368 ≤ 视口底 384。可用宽度、空态居中与原先完全一致（padding 和 margin 都把内容区减掉同样的 52px）。仓库其余两个 ScrollViewer（建议弹层、最近搜索）没有 Padding，不受影响。

## 排查教训：这次是「眼测截图骗人」第三回

1. **headless 截图可能滞后滚动一拍**：改 `ScrollViewer.Offset` 后立刻 `CaptureRenderedFrame`，拿到的可能是**没滚动的旧帧**（滚动位移走 compositor transform，headless 场景更新晚一拍）；多 pump 几次再截有时对、有时还是旧的，没法稳定同步。这次差点把「修复无效」的假结论带回屋里——布局断言（读活布局 bounds 的 `TranslatePoint` 几何）三次宽度全过，和旧帧直接矛盾，才识破是截图滞后。**滚动状态的验证用几何断言，别信滚动后的 headless 截图**；`ResultsScrollReachesBottom` 因此故意不落截图。
2. **定位渲染/滚动问题仍然靠量测**：Extent/Viewport/Offset 与「最大 offset 下内容最低点 vs 视口底」四个数一对比，根因直接现形，比看十张截图都快。
3. **老配置的坑会在新内容下浮出**：padding 从 MVP 就在，但结果页矮的时候 Extent ≤ Viewport，永远滚不起来也就永远不裁；词库三行把结果页撑过视口，bug 才第一次有机会发作。「一直没报过 bug」不等于「代码没 bug」。
