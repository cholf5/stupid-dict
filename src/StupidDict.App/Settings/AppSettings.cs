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
/// Everything persisted across runs: the theme and the UI language are user
/// choices surfaced in the settings window; the main window's size and
/// maximized state are recorded automatically at close with no settings UI.
/// Lives as one shared instance: App wires it at startup, the settings window
/// mutates it, MainWindow records bounds into it, and every change applies
/// live and is persisted by the wiring.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private AppTheme _theme = AppTheme.System;
    private AppLanguage _language = AppLanguage.System;
    private double? _windowWidth;
    private double? _windowHeight;
    private bool _windowMaximized;

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

    /// <summary>Main window width in DIPs as of the last close; null until the first close.</summary>
    public double? WindowWidth
    {
        get => _windowWidth;
        set
        {
            if (_windowWidth == value) return;
            _windowWidth = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowWidth)));
        }
    }

    /// <summary>Main window height in DIPs as of the last close; null until the first close.</summary>
    public double? WindowHeight
    {
        get => _windowHeight;
        set
        {
            if (_windowHeight == value) return;
            _windowHeight = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowHeight)));
        }
    }

    /// <summary>Whether the main window was maximized at last close.</summary>
    public bool WindowMaximized
    {
        get => _windowMaximized;
        set
        {
            if (_windowMaximized == value) return;
            _windowMaximized = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowMaximized)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
