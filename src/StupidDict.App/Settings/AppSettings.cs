using System.ComponentModel;

namespace StupidDict.App.Settings;

/// <summary>The appearance theme: follow the OS, or force one mode.</summary>
public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>
/// Everything the user can configure — currently only the theme. Lives as one
/// shared instance: App wires it at startup, the settings window mutates it,
/// and every change applies live and is persisted by the wiring.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private AppTheme _theme = AppTheme.System;

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

    public event PropertyChangedEventHandler? PropertyChanged;
}
