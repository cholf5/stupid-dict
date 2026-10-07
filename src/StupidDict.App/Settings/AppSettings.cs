using System.ComponentModel;

namespace StupidDict.App.Settings;

/// <summary>The appearance theme: follow the OS, or force one mode.</summary>
public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>The UI language: follow the OS, or force one. (0/1/2 mirror the settings combo order.)</summary>
public enum AppLanguage
{
    System = 0,
    SimplifiedChinese = 1,
    English = 2,
}

/// <summary>
/// Everything the user can configure — currently the theme and the UI
/// language. Lives as one shared instance: App wires it at startup, the
/// settings window mutates it, and every change applies live and is persisted
/// by the wiring.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private AppTheme _theme = AppTheme.System;
    private AppLanguage _language = AppLanguage.System;

    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (_theme == value) return;
            _theme = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Theme)));
        }
    }

    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
