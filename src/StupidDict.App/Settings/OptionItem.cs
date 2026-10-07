using System.ComponentModel;

namespace StupidDict.App.Settings;

/// <summary>
/// 设置下拉框的选项：实例跨语言稳定，切换语言只改 Label，选区按对象身份天然保持。
/// 选项文案必须经 ComboBox.ItemTemplate 绑定 Label，不能让 ComboBoxItem.Content
/// 直接绑 Translations——Avalonia 11.3 的选中框在选中时对内容做快照，事后
/// Content 变化不回显（切语言后闭合下拉框仍显示旧文案）。
/// </summary>
public sealed class OptionItem : INotifyPropertyChanged
{
    private string _label;

    public OptionItem(string label) => _label = label;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label
    {
        get => _label;
        set
        {
            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    public override string ToString() => Label;
}
