# 设置窗口下拉选项随语言切换刷新

日期：2026-10-07

## 背景与目标

设置窗口的两个下拉框（主题 / 语言）切语言后文案不刷新：语言切到 English，
主题框闭合显示仍是「跟随系统」。同一问题在 inpaint（829e23e）修过一次，
照同款方案搬到本仓库。

根因在 Avalonia 11.3 的 `ComboBox.UpdateSelectionBoxItem`：**选中框在选中时
对选中项内容做快照**——原实现里选项是 XAML 静态 `ComboBoxItem`、`Content`
直接绑 `Translations.Instance`，语言切换确实更新了 `Content`（下拉列表项是
对的），但闭合显示持有的是选区建立那一刻的旧字符串，之后无人回填。
`LanguageSwitchLiveRetitlesAndRerendersResults` 只断言了主窗口换语言，
没断言设置窗口下拉框，所以漏网。

目标：语言切换后设置窗口全部可见文案即时刷新，选中项保持，不加设置项。

## 做了什么

照 inpaint 的方案：**选项实例跨语言稳定，切换语言只改 Label**。

- 新增 `Settings/OptionItem.cs`：INPC 的 `Label` + `ToString()` 兜底。
- `SettingsWindow.axaml`：静态 `ComboBoxItem` 换成 `ItemTemplate` 绑
  `OptionItem.Label`。选中框与下拉列表共用同一模板，走的是对选项实例的
  活绑定，快照问题消失。
- `SettingsWindow.axaml.cs`：选项数组构造时一次创建（顺序与枚举下标一一
  对应），`ItemsSource` 先于 `SelectedIndex` 就位后不再更换；订阅
  `Translations.PropertyChanged` 更新 Label，`OnClosed` 解订（Translations
  是进程级单例，不解订会把已关窗口钉住）。
- 回归测试 `LanguageSwitchRefreshesSettingsComboOptionsAndKeepsSelection`：
  走真实链路（选 English → 共享设置 → `App.WireSettings` →
  `Translations.SetLanguage`），断言两个下拉框闭合显示文本换语言
  （`GetVisualDescendants` 层，直接钉住本次 bug）、`ItemsSource` 的
  Label 换语言、选中索引原位保持、再切回中文复原。修前断言失败
  （主题框停留在「跟随系统」），修后通过。

## 评估过什么、为何不做

- **给 ComboBoxItem.Content 直绑 Translations 之外补一个选中框回填**
  （如切语言后重设 SelectedIndex 触发快照重算）：能治症状，但重设选区
  语义脆弱（SelectionChanged 会把中间态写回设置），且逐处补救不如让
  显示本身走活绑定。弃。
- **重建 ItemsSource 换新文案数组**：inpaint 已踩过——重建会异步清空
  选区，并把滞留的旧选中项经双向绑定推回设置，把语言切换自己吞掉。
  明确不做，`ItemsSource` 全程不变。
- **升级 Avalonia**：12.x 未验证整仓兼容，为一个已知问题冒险不值；
  且稳定实例方案在新版同样成立。
- 下拉列表项文案在旧实现下其实是刷新的（`Content` 绑定活着），只有
  闭合显示是旧的；新方案两条路径共用模板，行为一致，无需区分对待。

## 已知取舍

- 选项 Label 初始值在窗口构造时从 `Translations.Instance` 取一次，
  之后靠事件刷新——窗口生命期内 Translations 只会被本窗口的语言选择
  改动，语义闭合。
- 测试对下拉列表项按 `Label` 断言而非视觉树：Avalonia 弹层内容挂在
  PopupRoot，进不了 ComboBox 的 `GetVisualDescendants()`；列表与选中框
  共用同一 `ItemTemplate`，选中框已做渲染层断言。
